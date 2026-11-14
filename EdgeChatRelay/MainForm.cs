using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace EdgeChatRelay
{
    public class MainForm : Form
    {
        public PanelPane[] Panels { get; }
        public ConduitBox Conduit { get; }

        private readonly RelayEngine _engine;
        private readonly AppSettings _settings;

        private readonly Panel _topBar = new Panel() { Dock = DockStyle.Top, Height = 42, Padding = new Padding(6, 6, 6, 2) };
        private readonly Button _armBtn = new Button() { Text = "ARM RELAY", Width = 120, Height = 28, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        private readonly Button _stopBtn = new Button() { Text = "STOP", Width = 64, Height = 28, Enabled = false };
        private readonly NumericUpDown _delayNum = new NumericUpDown() { Minimum = 0, Maximum = 60, Increment = 0.5M, DecimalPlaces = 1, Value = 2.5M, Width = 58 };
        private readonly NumericUpDown _turnsNum = new NumericUpDown() { Minimum = 0, Maximum = 9999, Value = 50, Width = 58 };
        private readonly Label _turnsLabel = new Label() { Text = "turns: 0", AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
        private readonly Label _globalStatus = new Label() { Text = "Starting…", AutoSize = true, Padding = new Padding(8, 6, 0, 0), ForeColor = Color.DarkBlue };
        private readonly TableLayoutPanel _layout = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        private bool _manualSendInFlight;

        public MainForm()
        {
            _settings = AppSettings.Load();
            _engine = new RelayEngine(this);

            Text = "Edge Chat Relay — dual-panel chatbot conduit (Edge WebView2)";
            Width = _settings.Width;
            Height = _settings.Height;
            MinimumSize = new Size(1180, 640);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            Panels = new PanelPane[]
            {
                new PanelPane(0, Path.Combine(AppSettings.ConfigDir, "ProfileA")) { Dock = DockStyle.Fill },
                new PanelPane(1, Path.Combine(AppSettings.ConfigDir, "ProfileB")) { Dock = DockStyle.Fill },
            };
            Conduit = new ConduitBox() { Dock = DockStyle.Fill };

            // ---- top bar ----
            var delayLbl = new Label() { Text = "delay (s):", AutoSize = true, Padding = new Padding(10, 6, 0, 0) };
            var turnsLbl = new Label() { Text = "max turns:", AutoSize = true, Padding = new Padding(10, 6, 0, 0) };
            var helpBtn = new Button() { Text = "?", Width = 30, Height = 28 };

            var flow = new FlowLayoutPanel() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            flow.Controls.Add(_armBtn);
            flow.Controls.Add(_stopBtn);
            flow.Controls.Add(delayLbl);
            flow.Controls.Add(_delayNum);
            flow.Controls.Add(turnsLbl);
            flow.Controls.Add(_turnsNum);
            flow.Controls.Add(_turnsLabel);
            flow.Controls.Add(helpBtn);
            flow.Controls.Add(_globalStatus);
            _topBar.Controls.Add(flow);

            // ---- three columns: panel 1 | conduit | panel 2 ----
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330F));
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _layout.Controls.Add(Panels[0], 0, 0);
            _layout.Controls.Add(Conduit, 1, 0);
            _layout.Controls.Add(Panels[1], 2, 0);

            Controls.Add(_layout);
            Controls.Add(_topBar);

            // ---- events ----
            _armBtn.Click += (s, e) => ToggleArm();
            _stopBtn.Click += (s, e) => { _engine.Stop("manual STOP"); UpdateArmUi(); };
            helpBtn.Click += (s, e) => ShowHelp();
            _delayNum.ValueChanged += (s, e) => _engine.DelayMs = (int)(_delayNum.Value * 1000);
            _turnsNum.ValueChanged += (s, e) => _engine.MaxTurns = (int)_turnsNum.Value;

            _engine.RelayStatus += msg => { if (IsHandleCreated) BeginInvoke(new Action(() => _globalStatus.Text = msg)); };
            _engine.TurnsChanged += () => { if (IsHandleCreated) BeginInvoke(new Action(() => _turnsLabel.Text = "turns: " + _engine.TurnsDone)); };

            foreach (var p in Panels)
            {
                p.ResponseCaptured += OnResponseCaptured;
                p.UserMessageCaptured += OnUserCaptured;
            }

            Conduit.SendRequested += async target => await ManualSendAsync(target);

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { _engine.Stop("manual STOP (Esc)"); UpdateArmUi(); } };

            Load += OnFirstLoad;
            FormClosing += (s, e) => SaveSettings();
        }

        private async void OnFirstLoad(object? sender, EventArgs e)
        {
            try
            {
                _globalStatus.Text = "Starting Edge (WebView2)…";
                var baseDir = AppSettings.ConfigDir;
                var envA = await CoreWebView2Environment.CreateAsync(null, Path.Combine(baseDir, "ProfileA"));
                var envB = await CoreWebView2Environment.CreateAsync(null, Path.Combine(baseDir, "ProfileB"));

                await Panels[0].InitAsync(envA);
                await Panels[1].InitAsync(envB);

                // restore panel settings
                Panels[0].LogPath = string.IsNullOrEmpty(_settings.LogPath1)
                    ? Path.Combine(AppSettings.DefaultLogDir, "panel1_chat_log.txt") : _settings.LogPath1;
                Panels[1].LogPath = string.IsNullOrEmpty(_settings.LogPath2)
                    ? Path.Combine(AppSettings.DefaultLogDir, "panel2_chat_log.txt") : _settings.LogPath2;
                Panels[0].RelayEnabledRestore(_settings.Relay1);
                Panels[1].RelayEnabledRestore(_settings.Relay2);
                Panels[0].LogEnabledRestore(_settings.Log1);
                Panels[1].LogEnabledRestore(_settings.Log2);
                Panels[0].SendMode = _settings.SendMode1;
                Panels[1].SendMode = _settings.SendMode2;

                _delayNum.Value = Math.Min(_delayNum.Maximum, Math.Max(_delayNum.Minimum, _settings.DelayMs / 1000M));
                _turnsNum.Value = Math.Min(_turnsNum.Maximum, Math.Max(_turnsNum.Minimum, _settings.MaxTurns));

                if (!string.IsNullOrEmpty(_settings.Url1) && _settings.Url1 != "about:blank")
                    Panels[0].NavigateTo(_settings.Url1);
                if (!string.IsNullOrEmpty(_settings.Url2) && _settings.Url2 != "about:blank")
                    Panels[1].NavigateTo(_settings.Url2);

                _globalStatus.Text = "Enter chatbot URLs in the panels, sign in normally, then ARM RELAY. Messages will pass through the CONDUIT box.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Could not start the Edge WebView2 runtime.\n\n" + ex.Message +
                    "\n\nInstall the WebView2 Runtime (ships with Windows 10/11 and Microsoft Edge) from:\n" +
                    "https://developer.microsoft.com/microsoft-edge/webview2/",
                    "Edge Chat Relay", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            }
        }

        private void OnResponseCaptured(int panelId, string text)
        {
            Panels[panelId].AppendLog("response", text);
            _engine.OnResponse(panelId, text);
        }

        private void OnUserCaptured(int panelId, string text)
        {
            Panels[panelId].AppendLog("user", text);
        }

        /// <summary>
        /// The user typed into the conduit box themselves and pressed a send
        /// button: fire the box contents into the chosen panel(s) like a human.
        /// target: -1 = panel 1, +1 = panel 2, 0 = both.
        /// </summary>
        private async Task ManualSendAsync(int target)
        {
            if (_manualSendInFlight) return;
            var text = Conduit.BoxText;
            if (text.Trim().Length == 0) { Conduit.SetState("nothing to send — box is empty"); return; }

            _manualSendInFlight = true;
            try
            {
                int[] targets = target == 0 ? new[] { 0, 1 } : new[] { target < 0 ? 0 : 1 };
                bool anyOk = false, anyFail = false;
                foreach (int t in targets)
                {
                    var resultJson = await Panels[t].InjectAndSendAsync(text, _engine.DelayMs);
                    if (resultJson.Contains("\"ok\":true"))
                    {
                        anyOk = true;
                        Conduit.AppendTransit($"YOU → P{t + 1}  " + OneLine(text));
                    }
                    else
                    {
                        anyFail = true;
                        Conduit.AppendTransit($"[failed] YOU → P{t + 1}: {resultJson}");
                    }
                }
                if (anyOk && !anyFail) Conduit.MarkSent("(manual) " + OneLine(text));
                else Conduit.SetState(anyOk ? "sent to one panel; box kept (see transit log)" : "send failed — box kept (see transit log)");
            }
            finally
            {
                _manualSendInFlight = false;
            }
        }

        private static string OneLine(string s)
        {
            var n = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return n.Length > 90 ? n.Substring(0, 90) + "…" : n;
        }

        private void ToggleArm()
        {
            if (_engine.Armed)
            {
                _engine.Stop("manual disarm");
            }
            else
            {
                if (!Panels[0].RelayEnabled && !Panels[1].RelayEnabled)
                {
                    MessageBox.Show(this, "Both panels have Relay unchecked.\nEnable Relay on at least one panel first.",
                        "Edge Chat Relay", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                _engine.MaxTurns = (int)_turnsNum.Value;
                _engine.DelayMs = (int)(_delayNum.Value * 1000);
                _engine.Arm();
            }
            UpdateArmUi();
        }

        private void UpdateArmUi()
        {
            _armBtn.Text = _engine.Armed ? "RELAY: ON" : "ARM RELAY";
            _armBtn.BackColor = _engine.Armed ? Color.LimeGreen : SystemColors.Control;
            _stopBtn.Enabled = _engine.Armed;
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "HOW IT WORKS\n\n" +
                "1. Left and right panels are real Edge browsers with their own profiles —\n" +
                "   sign in to your chatbot sites exactly like a human would. No API keys.\n\n" +
                "2. THE CONDUIT (middle column) is the third prompt box. It connects to\n" +
                "   nothing — it is only a text clipboard. Every relayed message physically\n" +
                "   passes through it: left response → conduit box → right chat box, and back.\n\n" +
                "3. ARM RELAY: chatbot responses flow through the conduit automatically.\n" +
                "   During the delay window the message sits in the conduit box — EDIT IT\n" +
                "   there to revise what actually gets sent (or erase it to cancel the relay).\n\n" +
                "4. TAKE THE REINS anytime: press STOP (or Esc), type into the CONDUIT box,\n" +
                "   and press '→ Panel 1', '→ Panel 2' or '→ Both' to interject. The next\n" +
                "   chatbot reply to your interjection relays as usual when you re-ARM.\n\n" +
                "5. Each panel logs captured responses to its own text file (see 'Log file…').\n\n" +
                "SAFETY: 'max turns' stops the relay automatically (0 = unlimited),\n" +
                "'delay (s)' paces each forward, your drafts (in panels or the conduit box)\n" +
                "are never overwritten. 'Send: Fill only' stages messages in the target\n" +
                "panel's chat box without sending so you can review first. 'Rescan' grabs\n" +
                "a response the auto-capture missed.",
                "Edge Chat Relay — Help", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SaveSettings()
        {
            _settings.Width = Width;
            _settings.Height = Height;
            _settings.Url1 = Panels[0].CurrentUrl;
            _settings.Url2 = Panels[1].CurrentUrl;
            _settings.Relay1 = Panels[0].RelayEnabled;
            _settings.Relay2 = Panels[1].RelayEnabled;
            _settings.Log1 = Panels[0].LogEnabled;
            _settings.Log2 = Panels[1].LogEnabled;
            _settings.LogPath1 = Panels[0].LogPath;
            _settings.LogPath2 = Panels[1].LogPath;
            _settings.SendMode1 = Panels[0].SendMode;
            _settings.SendMode2 = Panels[1].SendMode;
            _settings.DelayMs = (int)(_delayNum.Value * 1000);
            _settings.MaxTurns = (int)_turnsNum.Value;
            _settings.Save();
        }
    }
}
