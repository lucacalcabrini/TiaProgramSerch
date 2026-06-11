using System;
using System.Linq;
using System.Windows.Forms;
using Velopack;
using Velopack.Sources;

namespace TiaVarAnalyzer
{
    internal static class Program
    {
        public const string AppVersion = "3.0.1";
        const string RepoUrl = "https://github.com/lucacalcabrini/TiaProgramSerch";

        [STAThread]
        static void Main(string[] args)
        {
            // Velopack deve girare per primissimo (gestisce install/update hooks).
            VelopackApp.Build().Run();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool mock = args.Contains("--mock");

            // Auto-update SOLO all'avvio (non bloccante se offline / non installato).
            try { CheckUpdatesAtStartup(); } catch { /* ignora */ }

            Application.Run(new MainForm(mock));
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
