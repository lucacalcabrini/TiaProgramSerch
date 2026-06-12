using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using TiaVarAnalyzer.Openness;
using Velopack;
using Velopack.Sources;

namespace TiaVarAnalyzer
{
    internal static class Program
    {
        public const string AppVersion = "3.3.2";
        const string RepoUrl = "https://github.com/lucacalcabrini/TiaProgramSerch";

        [STAThread]
        static void Main(string[] args)
        {
            // Velopack deve girare per primissimo (gestisce install/update hooks).
            VelopackApp.Build().Run();

            // Modalità batch senza UI:
            //   --export <progetto> [--out <file.json>]        analisi VS_Pos/APP
            //   --exportxml <progetto> --outdir <cartella>     export completo XML del SW
            if (args.Contains("--export"))
            {
                Environment.Exit(RunCliExport(args));
                return;
            }
            if (args.Contains("--exportxml"))
            {
                Environment.Exit(RunCliExportXml(args));
                return;
            }
            if (args.Contains("--loadxml"))
            {
                Environment.Exit(RunCliLoadXml(args));
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool mock = args.Contains("--mock");

            // Auto-update SOLO all'avvio (non bloccante se offline / non installato).
            try { CheckUpdatesAtStartup(); } catch { /* ignora */ }

            Application.Run(new MainForm(mock));
        }

        // Esporta un .ap18/.zap da riga di comando e scrive il bundle JSON.
        // Uso: --export <progetto> [--out <file.json>] [--user <utente> --pass <password>]
        // Output: <out>.json + log testuale <out>.json.log. Exit code 0 = ok.
        static int RunCliExport(string[] args)
        {
            string ArgAfter(string name)
            {
                int k = Array.IndexOf(args, name);
                return (k >= 0 && k + 1 < args.Length) ? args[k + 1] : null;
            }

            string path = ArgAfter("--export") ?? "";
            string outJson = ArgAfter("--out") ?? path + ".analysis.json";
            string user = ArgAfter("--user");
            string pass = ArgAfter("--pass");
            string log = outJson + ".log";

            void Log(string s)
            {
                try { File.AppendAllText(log, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s + Environment.NewLine); }
                catch { }
            }

            try
            {
                Log("export: " + path);
                var client = new OpennessClient(mock: false);
                var bundle = client.ExportFromFile(path, (p, t) => Log(p + "% " + t), user, pass);
                var settings = new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() };
                File.WriteAllText(outJson, JsonConvert.SerializeObject(bundle, Formatting.Indented, settings));
                Log("OK -> " + outJson);
                return 0;
            }
            catch (ProtectedProjectException pex)
            {
                Log("ERRORE: " + pex.Message + " Usa --user <utente> --pass <password>.");
                return 2;
            }
            catch (Exception ex)
            {
                Log("ERRORE: " + ex);
                return 1;
            }
        }

        // Export completo del SW del progetto in XML (SimaticML) da riga di comando.
        // Uso: --exportxml <progetto> --outdir <cartella> [--user <utente> --pass <password>]
        // Log: <outdir>\export-xml.log. Exit code 0 = ok, 2 = servono credenziali.
        static int RunCliExportXml(string[] args)
        {
            string ArgAfter(string name)
            {
                int k = Array.IndexOf(args, name);
                return (k >= 0 && k + 1 < args.Length) ? args[k + 1] : null;
            }

            string path = ArgAfter("--exportxml") ?? "";
            string outDir = ArgAfter("--outdir") ?? Path.GetDirectoryName(path);
            string user = ArgAfter("--user");
            string pass = ArgAfter("--pass");
            string log = Path.Combine(outDir ?? ".", "export-xml.log");

            void Log(string s)
            {
                try { File.AppendAllText(log, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s + Environment.NewLine); }
                catch { }
            }

            try
            {
                Log("exportxml: " + path + " -> " + outDir);
                var client = new OpennessClient(mock: false);
                var res = client.ExportProjectXml(path, outDir, (p, t) => Log(p + "% " + t), user, pass);
                Log($"OK -> {res.OutDir} | plc={res.Plcs} blocchi={res.Blocks} tabelle={res.TagTables} udt={res.Types} saltati={res.Skipped}");
                return 0;
            }
            catch (ProtectedProjectException pex)
            {
                Log("ERRORE: " + pex.Message + " Usa --user <utente> --pass <password>.");
                return 2;
            }
            catch (Exception ex)
            {
                Log("ERRORE: " + ex);
                return 1;
            }
        }

        // Analizza una cartella di XML SimaticML già esportati e scrive il bundle JSON.
        // NON avvia TIA Portal: è puro parsing, funziona su qualunque PC.
        // Uso: --loadxml <cartella> [--out <file.json>]
        static int RunCliLoadXml(string[] args)
        {
            string ArgAfter(string name)
            {
                int k = Array.IndexOf(args, name);
                return (k >= 0 && k + 1 < args.Length) ? args[k + 1] : null;
            }

            string dir = ArgAfter("--loadxml") ?? "";
            string outJson = ArgAfter("--out") ?? Path.Combine(dir, "analysis.json");
            string log = outJson + ".log";

            void Log(string s)
            {
                try { File.AppendAllText(log, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s + Environment.NewLine); }
                catch { }
            }

            try
            {
                Log("loadxml: " + dir);
                var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
                var bundle = OpennessClient.ParseXmlPaths(new[] { dir }, name, (p, t) => Log(p + "% " + t));
                var settings = new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() };
                File.WriteAllText(outJson, JsonConvert.SerializeObject(bundle, Formatting.Indented, settings));
                Log($"OK -> {outJson} | file={bundle.Stats.Blocks} saltati={bundle.Stats.Skipped} vs={bundle.Stats.Vs} app={bundle.Stats.App}");
                return 0;
            }
            catch (Exception ex)
            {
                Log("ERRORE: " + ex);
                return 1;
            }
        }

        static void CheckUpdatesAtStartup()
        {
            try
            {
                var mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
                if (!mgr.IsInstalled) return;                 // exe "sciolto" in dev: niente update

                var info = mgr.CheckForUpdates();
                if (info == null) return;                     // già aggiornato

                var res = MessageBox.Show(
                    $"È disponibile una nuova versione: {info.TargetFullRelease.Version}\n(installata: {AppVersion})\n\nVuoi aggiornare adesso?",
                    "TIA Var Analyzer — Aggiornamento",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (res != DialogResult.Yes) return;

                mgr.DownloadUpdates(info);
                mgr.ApplyUpdatesAndRestart(info);             // applica e riavvia
            }
            catch
            {
                // offline, repo irraggiungibile o app non installata via Velopack: prosegui normalmente
            }
        }
    }
}
