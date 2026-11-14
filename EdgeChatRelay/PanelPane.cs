using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace EdgeChatRelay
{
    /// <summary>
    /// One browser panel: nav bar + WebView2 + capture status.
    /// Each panel gets its own WebView2 profile (cookie jar) so two different
    /// accounts / sites can run side by side.
    /// </summary>
    public class PanelPane : UserControl
    {
        public int PanelId { get; }
        public string ProfileDir { get; }

        public event Action<int, string>? ResponseCaptured;
        public event Action<int, string>? UserMessageCaptured;
        public event Action<int, string>? StatusChanged;

        /// <summary>Should captured responses from this panel be forwarded to the other one?</summary>
        public bool RelayEnabled { get { return _relayBtn.Checked; } }

        /// <summary>Should captured messages be appended to this panel's log file?</summary>
        public bool LogEnabled { get { return _logBtn.Checked; } }

        /// <summary>'auto' | 'button' | 'enter' | 'none'</summary>
        public string SendMode
        {
            get
            {
                return _sendModeCombo.SelectedIndex switch
                {
                    1 => "button",
                    2 => "enter",
                    3 => "none",
                    _ => "auto",
                };
            }
            set
            {
                _sendModeCombo.SelectedIndex = value switch
                {
                    "button" => 1,
                    "enter" => 2,
                    "none" => 3,
                    _ => 0,
                };
            }
        }

        public string LogPath
        {
            get { return _logPath; }
            set { _logPath = value; UpdateLogLabel(); }
        }

        public string CurrentUrl { get { return _urlBox.Text; } set { _urlBox.Text = value; } }

        private readonly WebView2 _web = new WebView2();
        private readonly ToolStrip _navStrip = new ToolStrip();
        private readonly ToolStrip _ctlStrip = new ToolStrip();
        private readonly StatusStrip _statusStrip = new StatusStrip();
        private readonly ToolStripButton _backBtn = new ToolStripButton("◀") { ToolTipText = "Back" };
        private readonly ToolStripButton _fwdBtn = new ToolStripButton("▶") { ToolTipText = "Forward" };
        private readonly ToolStripButton _reloadBtn = new ToolStripButton("⟳") { ToolTipText = "Reload" };
        private readonly ToolStripSpringTextBox _urlBox = new ToolStripSpringTextBox();
        private readonly ToolStripButton _goBtn = new ToolStripButton("Go") { ToolTipText = "Navigate" };
        private readonly ToolStripButton _relayBtn = new ToolStripButton("Relay") { CheckOnClick = true, ToolTipText = "Forward this panel's chatbot responses to the other panel" };
        private readonly ToolStripButton _logBtn = new ToolStripButton("Log") { CheckOnClick = true, ToolTipText = "Append captured messages to this panel's text file" };
        private readonly ToolStripButton _logFileBtn = new ToolStripButton("Log file…") { ToolTipText = "Choose this panel's log file" };
        private readonly ToolStripComboBox _sendModeCombo = new ToolStripComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 78 };
        private readonly ToolStripButton _rescanBtn = new ToolStripButton("Rescan") { ToolTipText = "Manually grab the most recent message if the auto-capture missed it" };
        private readonly ToolStripStatusLabel _statusLabel = new ToolStripStatusLabel() { Spring = true, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
        private readonly ToolStripStatusLabel _counterLabel = new ToolStripStatusLabel();
        private CoreWebView2Environment? _env;
        private string _logPath = "";
        private int _captureCount;

        public PanelPane(int panelId, string profileDir)
        {
            PanelId = panelId;
            ProfileDir = profileDir;

            SuspendLayout();

            _web.Dock = DockStyle.Fill;
            _web.CreationProperties = new CoreWebView2CreationProperties() { UserDataFolder = profileDir };

            // ---- nav strip ----
            _navStrip.GripStyle = ToolStripGripStyle.Hidden;
            _navStrip.Items.Add(_backBtn);
            _navStrip.Items.Add(_fwdBtn);
            _navStrip.Items.Add(_reloadBtn);
            _navStrip.Items.Add(_urlBox);
            _navStrip.Items.Add(_goBtn);

            // ---- control strip ----
            _ctlStrip.GripStyle = ToolStripGripStyle.Hidden;
            _sendModeCombo.Items.AddRange(new object[] { "Send: Auto", "Send: Button", "Send: Enter", "Send: Fill only" });
            _sendModeCombo.SelectedIndex = 0;
            _relayBtn.Checked = true;
            _logBtn.Checked = true;
            _ctlStrip.Items.Add(_relayBtn);
            _ctlStrip.Items.Add(_logBtn);
            _ctlStrip.Items.Add(_logFileBtn);
            _ctlStrip.Items.Add(new ToolStripSeparator());
            _ctlStrip.Items.Add(_sendModeCombo);
            _ctlStrip.Items.Add(_rescanBtn);

            // ---- status ----
            _statusStrip.Items.Add(_statusLabel);
            _statusStrip.Items.Add(_counterLabel);

            Controls.Add(_web);
            Controls.Add(_ctlStrip);
            Controls.Add(_navStrip);
            Controls.Add(_statusStrip);

            ResumeLayout(false);
            PerformLayout();

            _goBtn.Click += (s, e) => NavigateFromBar();
            _urlBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; NavigateFromBar(); } };
            _backBtn.Click += (s, e) => { if (_web.CanGoBack) _web.GoBack(); };
            _fwdBtn.Click += (s, e) => { if (_web.CanGoForward) _web.GoForward(); };
            _reloadBtn.Click += (s, e) => _web.CoreWebView2?.Reload();
            _rescanBtn.Click += async (s, e) => await RescanAsync();
            _logFileBtn.Click += (s, e) => ChooseLogFile();

            SetStatus("Ready");
        }

        public async Task InitAsync(CoreWebView2Environment env)
        {
            _env = env;
            await _web.EnsureCoreWebView2Async(env);

            var cw = _web.CoreWebView2;
            cw.Settings.AreDevToolsEnabled = true;
            cw.Settings.IsStatusBarEnabled = true;
            cw.Settings.AreDefaultContextMenusEnabled = true;
            cw.Settings.IsWebMessageEnabled = true;
            cw.Settings.AreBrowserAcceleratorKeysEnabled = true;

            await cw.AddScriptToExecuteOnDocumentCreatedAsync(CaptureScript.Source);

            cw.WebMessageReceived += OnWebMessage;
            cw.NavigationCompleted += (s, e) =>
            {
                _backBtn.Enabled = cw.CanGoBack;
                _fwdBtn.Enabled = cw.CanGoForward;
                if (e.IsSuccess) SetStatus("Loaded: " + cw.Source);
                else SetStatus("Navigation failed (" + e.WebErrorStatus + ")");
            };
            cw.SourceChanged += (s, e) => { _urlBox.Text = cw.Source; };
            cw.NewWindowRequested += OnNewWindow;

            cw.Navigate("about:blank");
        }

        private void OnNewWindow(object? s, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // Open popups (OAuth sign-in flows etc.) as real little windows sharing
            // this panel's profile — exactly like a normal browser.
            e.Handled = true;
            try
            {
                var popup = new PopupForm(_env!, e.Uri);
                popup.Show(FindForm());
            }
            catch (Exception ex)
            {
                SetStatus("Popup failed: " + ex.Message);
            }
        }

        private void OnWebMessage(object? s, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                // postMessage(object) arrives as a JSON object
                if (root.ValueKind == JsonValueKind.Object)
                {
                    var type = root.GetProperty("type").GetString();
                    var text = root.GetProperty("text").GetString() ?? "";
                    if (type == "response")
                    {
                        _captureCount++;
                        _counterLabel.Text = "captured: " + _captureCount;
                        ResponseCaptured?.Invoke(PanelId, text);
                    }
                    else if (type == "user")
                    {
                        UserMessageCaptured?.Invoke(PanelId, text);
                    }
                }
            }
            catch
            {
                // ignore malformed messages
            }
        }

        private void NavigateFromBar()
        {
            var url = _urlBox.Text.Trim();
            if (url.Length == 0) return;
            if (!url.Contains("://") && !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                url = url.Contains(".") && !url.Contains(" ") ? "https://" + url : "https://www.bing.com/search?q=" + Uri.EscapeDataString(url);
            }
            _web.CoreWebView2?.Navigate(url);
        }

        public void NavigateTo(string url)
        {
            _urlBox.Text = url;
            _web.CoreWebView2?.Navigate(url);
        }

        public void RelayEnabledRestore(bool on) { _relayBtn.Checked = on; }

        public void LogEnabledRestore(bool on) { _logBtn.Checked = on; }

        /// <summary>
        /// Type text into this panel's chat input and (optionally) send it.
        /// Returns the JS result JSON.
        /// </summary>
        public async Task<string> InjectAndSendAsync(string text, int delayMs)
        {
            if (_web.CoreWebView2 == null) return "{\"ok\":false,\"detail\":\"panel not ready\"}";
            string jsArg = JsonSerializer.Serialize(text);
            string autoSend = SendMode == "none" ? "false" : "true";
            string modeJson = JsonSerializer.Serialize(SendMode);
            string script =
                "(function(){ try { if (window.__relayPauseCapture) window.__relayPauseCapture(true);" +
                " var r = window.__relayInjectAndSend ? window.__relayInjectAndSend(" + jsArg + ", " + autoSend + ", " + modeJson + ")" +
                " : JSON.stringify({ok:false, detail:'relay script not loaded'});" +
                " setTimeout(function(){ if (window.__relayPauseCapture) window.__relayPauseCapture(false); }, 500);" +
                " return r; } catch(e) { return JSON.stringify({ok:false, detail:String(e)}); } })()";

            var raw = await _web.CoreWebView2.ExecuteScriptAsync(script);
            // ExecuteScriptAsync returns the value JSON-encoded (strings are quoted).
            try
            {
                using var d = JsonDocument.Parse(raw);
                if (d.RootElement.ValueKind == JsonValueKind.String)
                    return d.RootElement.GetString() ?? raw;
            }
            catch { }
            return raw;
        }

        private async Task RescanAsync()
        {
            if (_web.CoreWebView2 == null) return;
            var raw = await _web.CoreWebView2.ExecuteScriptAsync(
                "(function(){ try { return window.__relayRescan ? window.__relayRescan() : '{\"ok\":false}'; } catch(e) { return '{\"ok\":false}'; } })()");
            SetStatus("Rescan: " + Truncate(raw, 120));
        }

        public void SetStatus(string msg)
        {
            if (_statusLabel.Owner != null && _statusLabel.Owner.InvokeRequired)
                _statusLabel.Owner.BeginInvoke(new Action(() => _statusLabel.Text = msg));
            else
                _statusLabel.Text = msg;
            StatusChanged?.Invoke(PanelId, msg);
        }

        private void ChooseLogFile()
        {
            using var dlg = new SaveFileDialog()
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = Path.GetFileName(_logPath),
                InitialDirectory = Path.GetDirectoryName(_logPath),
                Title = "Choose log file for Panel " + (PanelId + 1),
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                LogPath = dlg.FileName;
            }
        }

        private void UpdateLogLabel()
        {
            _logFileBtn.ToolTipText = "Log file: " + _logPath;
        }

        public void AppendLog(string kind, string text)
        {
            if (!LogEnabled || string.IsNullOrEmpty(_logPath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath) ?? ".");
                File.AppendAllText(_logPath,
                    "──── " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ──── [" + kind + "]\r\n" +
                    text.Trim() + "\r\n\r\n");
            }
            catch (Exception ex)
            {
                SetStatus("Log error: " + ex.Message);
            }
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    /// <summary>A ToolStripTextBox that springs to fill the remaining toolstrip width.</summary>
    public class ToolStripSpringTextBox : ToolStripTextBox
    {
        public override System.Drawing.Size GetPreferredSize(System.Drawing.Size constrainingSize)
        {
            if (Owner is ToolStrip tool && !IsOnDropDown)
            {
                int width = tool.DisplayRectangle.Width - tool.Padding.Horizontal;
                foreach (ToolStripItem item in tool.Items)
                {
                    if (item != this && item.Bounds.Width > 0)
                        width -= item.Bounds.Width + item.Margin.Horizontal;
                }
                var baseSize = base.GetPreferredSize(constrainingSize);
                return new System.Drawing.Size(Math.Max(120, width), baseSize.Height);
            }
            return base.GetPreferredSize(constrainingSize);
        }
    }
}
