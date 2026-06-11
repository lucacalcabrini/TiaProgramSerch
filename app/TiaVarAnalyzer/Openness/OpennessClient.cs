using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        bool _resolverRegistered;

        public OpennessClient(bool mock, string tiaVersion = "V18")
        {
            Mock = mock;
            TiaVersion = tiaVersion;
            _opennessDir = $@"C:\Program Files\Siemens\Automation\Portal {tiaVersion}\PublicAPI\{tiaVersion}";
        }

        // Registra il resolver che carica Siemens.* dalla cartella PublicAPI (requisito Openness).
        public void Initialize()
        {
            if (Mock || _resolverRegistered) return;
            string dll = Path.Combine(_opennessDir, "Siemens.Engineering.dll");
            if (!File.Exists(dll))
                throw new FileNotFoundException("Siemens.Engineering.dll non trovata.\nVerifica TIA " + TiaVersion + ": " + dll);
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
            _resolverRegistered = true;
        }

        Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            string path = Path.Combine(_opennessDir, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }

        // ---- progetti aperti -------------------------------------------------

        public List<ProjectInfo> GetProjects()
        {
            if (Mock) return MockProjects();
            Initialize();

            var list = new List<ProjectInfo>();
            foreach (var p in TiaPortal.GetProcesses())
            {
                string path = null;
                try { if (p.ProjectPath != null) path = p.ProjectPath.FullName; } catch { }
                list.Add(new ProjectInfo
                {
                    Pid = p.Id,
                    Name = path != null ? Path.GetFileName(path) : "(nessun progetto aperto)",
                    Path = path,
                    TiaVersion = TiaVersion
                });
            }
            return list;
        }

        // ---- export completo -------------------------------------------------

        public Bundle ExportBundle(int pid)
        {
            if (Mock) return MockBundle();
            Initialize();

            var proc = TiaPortal.GetProcesses().FirstOrDefault(x => x.Id == pid);
            if (proc == null) throw new Exception($"Nessun processo TIA Portal con pid {pid} (forse è stato chiuso?).");

            TiaPortal portal = proc.Attach();   // mostra il popup di sicurezza in TIA
            try
            {
                Project project = portal.Projects.FirstOrDefault();
                if (project == null) throw new Exception("Il processo TIA selezionato non ha un progetto aperto.");

                var plcs = GetPlcSoftwares(project);
                if (plcs.Count == 0) throw new Exception("Nessun PLC trovato nel progetto.");

                string tmp = Path.Combine(Path.GetTempPath(), "tia-app-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp);

                var vs = new List<VsRow>();
                var app = new List<AppRow>();
                int blockCount = 0, skipped = 0;

                foreach (var plc in plcs)
                {
                    var blocks = new List<PlcBlock>();
                    CollectBlocks(plc.BlockGroup, blocks);
                    foreach (var blk in blocks)
                    {
                        blockCount++;
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
                }

                try { Directory.Delete(tmp, true); } catch { }

                return new Bundle
                {
                    ExportedAt = DateTime.Now.ToString("s"),
                    Project = new ProjectMeta { Name = project.Name, Path = SafePath(project), TiaVersion = TiaVersion },
                    Stats = new Stats { Blocks = blockCount, Skipped = skipped, Vs = vs.Count, App = app.Count },
                    Vs = vs,
                    App = app
                };
            }
            finally
            {
                // Dispose dell'handle: NON chiude TIA dell'utente, rilascia solo la connessione.
                try { portal.Dispose(); } catch { }
            }
        }

        // ---- XML grezzo di un blocco (debug / calibrazione) ------------------

        public string GetRawXml(int pid, string blockName)
        {
            if (Mock) return $"<Mock><Block Name='{blockName}'/></Mock>";
            Initialize();

            var proc = TiaPortal.GetProcesses().FirstOrDefault(x => x.Id == pid);
            if (proc == null) throw new Exception($"Nessun processo TIA con pid {pid}.");

            TiaPortal portal = proc.Attach();
            try
            {
                Project project = portal.Projects.FirstOrDefault();
                if (project == null) throw new Exception("Nessun progetto aperto.");
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
                try { portal.Dispose(); } catch { }
            }
        }

        // ---- helpers ---------------------------------------------------------

        static string SafePath(Project p)
        {
            try { return p.Path != null ? p.Path.FullName : null; } catch { return null; }
        }

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

        static List<ProjectInfo> MockProjects() => new List<ProjectInfo>
        {
            new ProjectInfo { Pid = 11111, Name = "Linea_Imballaggio.ap18", Path = @"D:\TIA\Linea_Imballaggio\Linea_Imballaggio.ap18", TiaVersion = "V18" },
            new ProjectInfo { Pid = 22222, Name = "Cella_Robot_07.ap18",    Path = @"D:\TIA\Cella_Robot_07\Cella_Robot_07.ap18",       TiaVersion = "V18" }
        };

        static Bundle MockBundle() => new Bundle
        {
            ExportedAt = DateTime.Now.ToString("s"),
            Project = new ProjectMeta { Name = "Linea_Imballaggio", Path = @"D:\TIA\Linea_Imballaggio\Linea_Imballaggio.ap18", TiaVersion = "V18" },
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
