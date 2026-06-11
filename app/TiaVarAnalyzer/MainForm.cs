using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using TiaVarAnalyzer.Openness;

namespace TiaVarAnalyzer
{
    public class MainForm : Form
    {
        readonly bool _mock;
        readonly OpennessClient _client;
        WebView2 _web;

        static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        };

        public MainForm(bool mock)
        {
            _mock = mock;
            _client = new OpennessClient(mock);

            Text = "TIA Var Analyzer" + (mock ? "  (MOCK)" : "");
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }
            Width = 1280; Height = 820;
            StartPosition = FormStartPosition.CenterScreen;
            WindowState = FormWindowState.Maximized;
            BackColor = System.Drawing.Color.FromArgb(9, 12, 18);

            _web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_web);

            Shown += async (s, e) => await InitAsync();
        }

        async Task InitAsync()
        {
            try
            {
                string udf = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TiaVarAnalyzer", "WebView2");
                Directory.CreateDirectory(udf);

                var env = await CoreWebView2Environment.CreateAsync(null, udf);
                await _web.EnsureCoreWebView2Async(env);

                _web.CoreWebView2.WebMessageReceived += OnWebMessage;
#if !DEBUG
                _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
#endif

                if (!_mock)
                {
                    try { _client.Initialize(); }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Openness non inizializzata:\n" + ex.Message,
                            "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }

                string html = Path.Combine(AppDir(), "web", "index.html");
                if (!File.Exists(html))
                {
                    MessageBox.Show("Interfaccia non trovata:\n" + html,
                        "Errore", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                _web.CoreWebView2.Navigate(new Uri(html).AbsoluteUri);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Errore inizializzazione WebView2:\n" + ex.Message,
                    "Errore", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static string AppDir() => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        // Canale richiesta/risposta con la UI: la UI invia {id, action, args}, noi rispondiamo
        // {id, ok, result, error}. Il lavoro Openness gira fuori dal thread UI (Task.Run).
        async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw;
            try { raw = e.TryGetWebMessageAsString(); }
            catch { try { raw = e.WebMessageAsJson; } catch { return; } }

            JObject req;
            try { req = JObject.Parse(raw); } catch { return; }

            long id = req.Value<long?>("id") ?? 0;
            string action = (string)req["action"] ?? "";
            var args = req["args"] as JObject ?? new JObject();

            object result = null;
            bool ok = true;
            string error = null;

            try
            {
                switch (action)
                {
                    case "ping":
                        result = new { ok = true, tool = "tia-app", version = Program.AppVersion, mock = _mock, tiaVersion = _client.TiaVersion };
                        break;
                    case "pick":
                        // Dialog nativo: serve il percorso reale del file, che il JS non può ottenere.
                        using (var dlg = new OpenFileDialog
                        {
                            Title = "Apri progetto TIA Portal",
                            Filter = "Progetti TIA Portal (*.ap18;*.zap18;*.zap*)|*.ap18;*.zap18;*.zap*|Tutti i file (*.*)|*.*",
                            CheckFileExists = true
                        })
                        {
                            if (dlg.ShowDialog(this) == DialogResult.OK)
                                result = new { cancelled = false, path = dlg.FileName, name = Path.GetFileName(dlg.FileName) };
                            else
                                result = new { cancelled = true };
                        }
                        break;
                    case "export":
                        {
                            string path = (string)args["path"] ?? "";
                            string user = null, pass = null;
                            while (true)
                            {
                                try
                                {
                                    result = await Task.Run(() => _client.ExportFromFile(path, PostProgress, user, pass));
                                    break;
                                }
                                catch (ProtectedProjectException pex)
                                {
                                    // Progetto protetto (UMAC): chiedi le credenziali e ritenta.
                                    var cred = PromptUmacCredentials(Path.GetFileName(path), pex.Message);
                                    if (cred == null)
                                        throw new Exception("Progetto protetto: analisi annullata (credenziali non fornite).");
                                    user = cred.Item1; pass = cred.Item2;
                                }
                            }
                            break;
                        }
                    case "exportXml":
                        {
                            // Export completo del SW del progetto in XML (SimaticML).
                            string path = (string)args["path"] ?? "";
                            string outDir;
                            using (var dlg = new FolderBrowserDialog
                            {
                                Description = "Cartella di destinazione per l'export XML",
                                ShowNewFolderButton = true
                            })
                            {
                                if (dlg.ShowDialog(this) != DialogResult.OK)
                                {
                                    result = new { cancelled = true };
                                    break;
                                }
                                outDir = dlg.SelectedPath;
                            }

                            string user = null, pass = null;
                            while (true)
                            {
                                try
                                {
                                    result = await Task.Run(() => _client.ExportProjectXml(path, outDir, PostProgress, user, pass));
                                    break;
                                }
                                catch (ProtectedProjectException pex)
                                {
                                    var cred = PromptUmacCredentials(Path.GetFileName(path), pex.Message);
                                    if (cred == null)
                                        throw new Exception("Progetto protetto: export annullato (credenziali non fornite).");
                                    user = cred.Item1; pass = cred.Item2;
                                }
                            }
                            break;
                        }
                    case "openFolder":
                        {
                            // Apre la cartella dell'export in Esplora risorse.
                            string dir = (string)args["dir"] ?? "";
                            if (Directory.Exists(dir))
                                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                            result = new { ok = Directory.Exists(dir) };
                            break;
                        }
                    case "raw":
                        {
                            string path = (string)args["path"] ?? "";
                            string block = (string)args["block"] ?? "";
                            result = new { xml = await Task.Run(() => _client.GetRawXml(path, block)) };
                            break;
                        }
                    default:
                        ok = false; error = "azione sconosciuta: " + action;
                        break;
                }
            }
            catch (Exception ex)
            {
                ok = false; error = ex.Message;
            }

            var resp = JsonConvert.SerializeObject(new { id, ok, result, error }, JsonSettings);
            try { _web.CoreWebView2.PostWebMessageAsJson(resp); } catch { }
        }

        // Dialog credenziali per progetti protetti (UMAC). Ritorna (utente, password) o null.
        Tuple<string, string> PromptUmacCredentials(string projectName, string reason)
        {
            using (var f = new Form
            {
                Text = "Progetto protetto — " + projectName,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false,
                ClientSize = new System.Drawing.Size(420, 168)
            })
            {
                var lbl = new Label { Text = reason + "\nInserisci le credenziali del progetto:", Left = 14, Top = 12, Width = 392, Height = 34 };
                var lu  = new Label { Text = "Utente",   Left = 14, Top = 56, Width = 70 };
                var tu  = new TextBox { Left = 90, Top = 52, Width = 316 };
                var lp  = new Label { Text = "Password", Left = 14, Top = 88, Width = 70 };
                var tp  = new TextBox { Left = 90, Top = 84, Width = 316, UseSystemPasswordChar = true };
                var ok  = new Button { Text = "OK",      Left = 240, Top = 124, Width = 80, DialogResult = DialogResult.OK };
                var no  = new Button { Text = "Annulla", Left = 326, Top = 124, Width = 80, DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { lbl, lu, tu, lp, tp, ok, no });
                f.AcceptButton = ok; f.CancelButton = no;
                if (f.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(tu.Text))
                    return null;
                return Tuple.Create(tu.Text.Trim(), tp.Text);
            }
        }

        // Avanzamento export → UI (chiamato da thread di lavoro: marshal sul thread UI).
        void PostProgress(int percent, string text)
        {
            string json = JsonConvert.SerializeObject(new { @event = "progress", percent, text }, JsonSettings);
            try
            {
                if (InvokeRequired)
                    BeginInvoke((Action)(() => { try { _web.CoreWebView2.PostWebMessageAsJson(json); } catch { } }));
                else
                    _web.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch { }
        }
    }
}
