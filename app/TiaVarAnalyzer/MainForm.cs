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
                    case "projects":
                        result = new { projects = await Task.Run(() => _client.GetProjects()) };
                        break;
                    case "export":
                        {
                            int pid = args.Value<int?>("pid") ?? 0;
                            result = await Task.Run(() => _client.ExportBundle(pid));
                            break;
                        }
                    case "raw":
                        {
                            int pid = args.Value<int?>("pid") ?? 0;
                            string block = (string)args["block"] ?? "";
                            result = new { xml = await Task.Run(() => _client.GetRawXml(pid, block)) };
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
    }
}
