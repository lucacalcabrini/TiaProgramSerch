using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

namespace TiaVarAnalyzer.Openness
{
    public class OpennessClient
    {
        public bool Mock { get; }
        public string TiaVersion { get; }

        readonly string _opennessDir;

        public OpennessClient(bool mock, string tiaVersion = "V18")
        {
            Mock = mock;
            TiaVersion = tiaVersion;
            _opennessDir = $@"C:\Program Files\Siemens\Automation\Portal {tiaVersion}\PublicAPI\{tiaVersion}";

            // Il resolver va registrato PRIMA che il JIT compili un qualunque metodo che
            // usa tipi Siemens.* (succede all'ingresso del metodo, non alla prima riga):
            // il costruttore non ne usa, quindi qui siamo sempre in tempo.
            if (!mock)
                AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        }

        // Verifica che la DLL Openness esista (per dare un errore comprensibile subito).
        public void Initialize()
        {
            if (Mock) return;
            string dll = Path.Combine(_opennessDir, "Siemens.Engineering.dll");
            if (!File.Exists(dll))
                throw new FileNotFoundException("Siemens.Engineering.dll non trovata.\nVerifica TIA " + TiaVersion + ": " + dll);
        }

        Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            string path = Path.Combine(_opennessDir, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }

        // ---- export da file progetto (.ap18) o archivio (.zap18/.zap) ---------
        // Avvia TIA Portal headless, apre/dearchivia il progetto, esporta i blocchi
        // e li analizza. Il file originale non viene MAI modificato: gli archivi
        // vengono estratti in una cartella temporanea (eliminata a fine export).

        public Bundle ExportFromFile(string path, Action<int, string> progress = null,
                                     string umacUser = null, string umacPassword = null)
        {
            if (Mock) return MockBundle(path);
            Initialize();

            void Report(int p, string t) { try { progress?.Invoke(p, t); } catch { } }

            Project project = null;
            string retrieveDir = null;
            TiaPortal portal = OpenPortalWithProject(path, Report, out project, out retrieveDir, umacUser, umacPassword);
            try
            {
                Report(22, "Ricerca dei PLC nel progetto...");
                var plcs = GetPlcSoftwares(project);
                if (plcs.Count == 0) throw new Exception("Nessun PLC trovato nel progetto.");

                string tmp = Path.Combine(Path.GetTempPath(), "tia-app-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp);

                var all = new List<PlcBlock>();
                foreach (var plc in plcs) CollectBlocks(plc.BlockGroup, all);

                var vs = new List<VsRow>();
                var app = new List<AppRow>();
                int blockCount = 0, skipped = 0;

                for (int i = 0; i < all.Count; i++)
                {
                    var blk = all[i];
                    blockCount++;
                    if (i % 5 == 0 || i == all.Count - 1)
                        Report(25 + (int)(65.0 * (i + 1) / all.Count), $"Export blocchi: {i + 1}/{all.Count} — {blk.Name}");

                    string safe = string.Join("_", blk.Name.Split(Path.GetInvalidFileNameChars()));
                    string file = Path.Combine(tmp, safe + ".xml");
                    try
                    {
                        if (File.Exists(file)) File.Delete(file);
                        blk.Export(new FileInfo(file), ExportOptions.WithDefaults);
                        var xml = new XmlDocument();
                        xml.Load(file);
                        SimaticMlParser.Parse(xml, blk.Name, vs, app);
                    }
                    catch { skipped++; }
                }

                try { Directory.Delete(tmp, true); } catch { }

                Report(92, "Chiusura di TIA Portal...");
                return new Bundle
                {
                    ExportedAt = DateTime.Now.ToString("s"),
                    Project = new ProjectMeta { Name = project.Name, Path = path, TiaVersion = TiaVersion },
                    Stats = new Stats { Blocks = blockCount, Skipped = skipped, Vs = vs.Count, App = app.Count },
                    Vs = vs,
                    App = app
                };
            }
            finally
            {
                CloseAll(portal, project, retrieveDir);
            }
        }

        // ---- XML grezzo di un blocco (debug / calibrazione parser) ------------

        public string GetRawXml(string path, string blockName)
        {
            if (Mock) return $"<Mock><Block Name='{blockName}'/></Mock>";
            Initialize();

            Project project = null;
            string retrieveDir = null;
            TiaPortal portal = OpenPortalWithProject(path, null, out project, out retrieveDir);
            try
            {
                foreach (var plc in GetPlcSoftwares(project))
                {
                    var blocks = new List<PlcBlock>();
                    CollectBlocks(plc.BlockGroup, blocks);
                    var blk = blocks.FirstOrDefault(b => b.Name == blockName);
                    if (blk != null)
                    {
                        string tmp = Path.Combine(Path.GetTempPath(), "tia-raw-" + Guid.NewGuid().ToString("N") + ".xml");
                        blk.Export(new FileInfo(tmp), ExportOptions.WithDefaults);
                        string content = File.ReadAllText(tmp);
                        try { File.Delete(tmp); } catch { }
                        return content;
                    }
                }
                throw new Exception($"Blocco '{blockName}' non trovato.");
            }
            finally
            {
                CloseAll(portal, project, retrieveDir);
            }
        }

        // ---- apertura headless -------------------------------------------------

        TiaPortal OpenPortalWithProject(string path, Action<int, string> report, out Project project, out string retrieveDir,
                                        string umacUser = null, string umacPassword = null)
        {
            project = null;
            retrieveDir = null;
            void Report(int p, string t) { try { report?.Invoke(p, t); } catch { } }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new Exception("File di progetto non trovato:\n" + path);

            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool isArchive = ext.StartsWith(".zap");
            bool isProject = ext.StartsWith(".ap");
            if (!isArchive && !isProject)
                throw new Exception("Formato non supportato: " + ext + "\nUsa un progetto .ap18 o un archivio .zap18/.zap.");

            // Credenziali per progetti protetti (UMAC): utente di progetto.
            UmacDelegate umac = null;
            if (!string.IsNullOrEmpty(umacUser))
            {
                umac = creds =>
                {
                    creds.Type = UmacUserType.Project;
                    creds.Name = umacUser;
                    var ss = new System.Security.SecureString();
                    foreach (char c in umacPassword ?? "") ss.AppendChar(c);
                    creds.SetPassword(ss);
                };
            }

            Report(4, "Avvio di TIA Portal " + TiaVersion + " in background (1-2 min). " +
                      "Se compare la finestra \"Accesso Openness\", conferma con \"Sì a tutti\".");
            // Il popup di sicurezza Siemens nasce DIETRO le altre finestre e ha un timeout
            // di ~15 min: se l'utente non lo vede, l'avvio sembra bloccato e poi fallisce.
            // Il watcher lo porta in primo piano appena compare (la conferma resta all'utente).
            TiaPortal portal;
            using (var watcher = OpennessDialogWatcher.Start())
                portal = new TiaPortal(TiaPortalMode.WithoutUserInterface);
            try
            {
                if (isArchive)
                {
                    // Gli archivi vanno estratti in una cartella vuota: usiamo una temp dedicata.
                    retrieveDir = Path.Combine(Path.GetTempPath(), "TiaVarAnalyzer", "retr-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(retrieveDir);
                    Report(12, "Estrazione dell'archivio " + Path.GetFileName(path) + "...");
                    var src = new FileInfo(path);
                    try
                    {
                        project = umac == null
                            ? portal.Projects.Retrieve(src, new DirectoryInfo(retrieveDir))
                            : portal.Projects.Retrieve(src, new DirectoryInfo(retrieveDir), umac);
                    }
                    catch (EngineeringException exRetr)
                    {
                        ThrowIfProtected(exRetr, umac);
                        // Tipico: archivio di una versione TIA precedente → upgrade della sola
                        // copia temporanea (l'archivio originale resta intatto). Il Retrieve
                        // fallito può aver lasciato un'estrazione parziale: si riparte puliti.
                        try { Directory.Delete(retrieveDir, true); } catch { }
                        Directory.CreateDirectory(retrieveDir);
                        Report(14, "Estrazione semplice fallita (" + FirstLine(exRetr.Message) + ") — riprovo con upgrade della copia temporanea...");
                        try
                        {
                            project = umac == null
                                ? portal.Projects.RetrieveWithUpgrade(src, new DirectoryInfo(retrieveDir))
                                : portal.Projects.RetrieveWithUpgrade(src, new DirectoryInfo(retrieveDir), umac);
                        }
                        catch (EngineeringException exUp)
                        {
                            ThrowIfProtected(exUp, umac);
                            throw new Exception(
                                "Impossibile estrarre l'archivio " + Path.GetFileName(path) + ".\n\n" +
                                "Retrieve: " + exRetr.Message + "\n\n" +
                                "RetrieveWithUpgrade: " + exUp.Message);
                        }
                    }
                }
                else
                {
                    Report(12, "Apertura del progetto " + Path.GetFileName(path) + "...");
                    try
                    {
                        project = umac == null
                            ? portal.Projects.Open(new FileInfo(path))
                            : portal.Projects.Open(new FileInfo(path), umac);
                    }
                    catch (EngineeringException ex)
                    {
                        ThrowIfProtected(ex, umac);
                        // Niente upgrade automatico sugli .ap: scriverebbe accanto al progetto
                        // dell'utente. Meglio chiedere di archiviare in .zap (upgrade su copia).
                        throw new Exception(
                            "Impossibile aprire il progetto (versione diversa da TIA " + TiaVersion + "?).\n" +
                            "Apri e salva il progetto con TIA " + TiaVersion + ", oppure archivialo in .zap e riprova.\n\nDettaglio: " + ex.Message);
                    }
                }
                return portal;
            }
            catch
            {
                CloseAll(portal, project, retrieveDir);
                project = null;
                retrieveDir = null;
                throw;
            }
        }

        // Riconosce l'errore "progetto protetto" (messaggio localizzato it/en/de) e lo converte
        // in ProtectedProjectException, così il chiamante può chiedere le credenziali e ritentare.
        static void ThrowIfProtected(Exception ex, UmacDelegate umacAlreadyProvided)
        {
            string m = (ex.Message ?? "").ToLowerInvariant();
            bool prot = m.Contains("protetto") || m.Contains("protected") || m.Contains("geschützt")
                     || m.Contains("autorizzazione di accesso") || m.Contains("access authorization");
            if (!prot) return;
            throw new ProtectedProjectException(umacAlreadyProvided == null
                ? "Il progetto è protetto: servono nome utente e password di progetto."
                : "Credenziali non valide per il progetto protetto.");
        }

        static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? s : s.Substring(0, i);
        }

        static void CloseAll(TiaPortal portal, Project project, string retrieveDir)
        {
            try { project?.Close(); } catch { }
            try { portal?.Dispose(); } catch { }   // chiude l'istanza TIA headless avviata da noi
            if (retrieveDir != null) { try { Directory.Delete(retrieveDir, true); } catch { } }
        }

        // ---- helpers ---------------------------------------------------------

        List<PlcSoftware> GetPlcSoftwares(Project project)
        {
            var result = new List<PlcSoftware>();
            foreach (Device device in project.Devices)
                foreach (DeviceItem di in device.DeviceItems)
                    CollectPlc(di, result);
            return result;
        }

        void CollectPlc(DeviceItem di, List<PlcSoftware> acc)
        {
            try
            {
                var sc = di.GetService<SoftwareContainer>();
                if (sc != null && sc.Software is PlcSoftware plc) acc.Add(plc);
            }
            catch { }
            foreach (DeviceItem child in di.DeviceItems) CollectPlc(child, acc);
        }

        void CollectBlocks(PlcBlockGroup group, List<PlcBlock> acc)
        {
            foreach (PlcBlock b in group.Blocks) acc.Add(b);
            foreach (PlcBlockUserGroup g in group.Groups) CollectBlocks(g, acc);
        }

        // ---- mock ------------------------------------------------------------

        static Bundle MockBundle(string path)
        {
            string name = string.IsNullOrWhiteSpace(path)
                ? "Linea_Imballaggio"
                : Path.GetFileNameWithoutExtension(path);
            return new Bundle
            {
                ExportedAt = DateTime.Now.ToString("s"),
                Project = new ProjectMeta { Name = name, Path = path, TiaVersion = "V18" },
                Stats = new Stats { Blocks = 3, Skipped = 0, Vs = 3, App = 2 },
                Vs = new List<VsRow>
                {
                    new VsRow { Asse = "Asse111", Indice = 1, Blocco = "FC100_Movimenti", Segmento = "Network 2: Posizionamento", Operazione = "=", Commento = "Posizione di prelievo", Testo = "axes[Asse111].vsPos[1]", Linea = 12 },
                    new VsRow { Asse = "Asse105", Indice = 3, Blocco = "FC100_Movimenti", Segmento = "Network 4: Deposito",       Operazione = "S", Commento = "",                     Testo = "axes[Asse105].vsPos[3]", Linea = 28 },
                    new VsRow { Asse = "Gripper", Indice = 2, Blocco = "FB20_Pinza",       Segmento = "Network 1",                Operazione = "=", Commento = "Apertura pinza",        Testo = "axes[Gripper].vsPos[2]", Linea = 5  }
                },
                App = new List<AppRow>
                {
                    new AppRow { Numero = 2, Blocco = "FB20_Pinza",       Segmento = "Network 3", Operazione = "=", Commento = "Pezzo extra rilevato", Testo = "\"DB_IO\".additionalPiecePresence2", Linea = 33 },
                    new AppRow { Numero = 5, Blocco = "FC100_Movimenti", Segmento = "Network 6", Operazione = "",  Commento = "",                      Testo = "\"DB_IO\".additionalPiecePresence5", Linea = 51 }
                }
            };
        }
    }

    // Porta in primo piano il popup di sicurezza "Accesso Openness" di TIA Portal
    // appena compare (altrimenti nasce dietro le altre finestre, l'utente non lo vede
    // e dopo ~15 minuti l'avvio fallisce con EngineeringSecurityException).
    // NON conferma nulla: rende solo visibile il prompt, la scelta resta all'utente.
    sealed class OpennessDialogWatcher : IDisposable
    {
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int n);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO fi);
        delegate bool EnumProc(IntPtr h, IntPtr lp);

        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
        const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;

        readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public static OpennessDialogWatcher Start()
        {
            var w = new OpennessDialogWatcher();
            var t = new Thread(w.Loop) { IsBackground = true };
            t.Start();
            return w;
        }

        void Loop()
        {
            var seen = new HashSet<long>();
            while (!_cts.IsCancellationRequested)
            {
                EnumWindows((h, lp) =>
                {
                    if (!IsWindowVisible(h)) return true;
                    var sb = new System.Text.StringBuilder(256);
                    GetWindowText(h, sb, 256);
                    string title = sb.ToString();
                    // titolo localizzato: it / en / de
                    if ((title.StartsWith("Accesso Openness") || title.StartsWith("Openness access") || title.StartsWith("Openness-Zugriff"))
                        && seen.Add(h.ToInt64()))
                    {
                        ShowWindow(h, 5 /*SW_SHOW*/);
                        SetForegroundWindow(h);
                        var fi = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)), hwnd = h, dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG, uCount = 0, dwTimeout = 0 };
                        FlashWindowEx(ref fi);
                    }
                    return true;
                }, IntPtr.Zero);
                if (_cts.Token.WaitHandle.WaitOne(800)) break;
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
        }
    }
}
