using System;
using System.Drawing;
using System.Windows.Forms;

namespace EdgeChatRelay
{
    /// <summary>
    /// THE CONDUIT — the third prompt box in the middle of the two panels.
    /// It connects to nothing. It is only a text clipboard: relayed messages
    /// visibly pass through it on their way from one panel to the other, the
    /// user can edit a message mid-flight, and the user can type their own
    /// messages and send them to either or both panels.
    /// </summary>
    public class ConduitBox : UserControl
    {
        /// <summary>target: -1 = left panel (1), +1 = right panel (2), 0 = both</summary>
        public event Action<int>? SendRequested;

        private readonly Label _header = new Label()
        {
            Text = "CONDUIT — text-only middleman,\r\nconnected to nothing",
            Dock = DockStyle.Top,
            Height = 34,
            Font = new Font("Segoe UI", 8.25F, FontStyle.Bold),
            ForeColor = Color.DarkSlateGray,
            TextAlign = ContentAlignment.BottomLeft,
        };
        private readonly TextBox _box = new TextBox()
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            Font = new Font("Segoe UI", 10F),
            Dock = DockStyle.Top,
            Height = 150,
        };
        private readonly FlowLayoutPanel _buttons = new FlowLayoutPanel()
        {
            Dock = DockStyle.Top,
            Height = 62,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(0, 4, 0, 2),
        };
        private readonly Button _sendRight = new Button() { Text = "→ Panel 2", Width = 88, Height = 26 };
        private readonly Button _sendLeft = new Button() { Text = "← Panel 1", Width = 88, Height = 26 };
        private readonly Button _sendBoth = new Button() { Text = "→ Both", Width = 88, Height = 26 };
        private readonly Button _clearBtn = new Button() { Text = "Clear", Width = 60, Height = 26 };
        private readonly Label _transitHeader = new Label()
        {
            Text = "transit log:",
            Dock = DockStyle.Top,
            Height = 18,
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.BottomLeft,
        };
        private readonly TextBox _transit = new TextBox()
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 8.25F),
        };
        private readonly Label _state = new Label()
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            Text = "idle",
            ForeColor = Color.DarkBlue,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        private string _lastTransit = "";

        /// <summary>True when the box holds the user's own text (draft or edit).</summary>
        public bool IsDirty
        {
            get { return _box.Text.Trim().Length > 0 && _box.Text != _lastTransit; }
        }

        public string BoxText { get { return _box.Text; } }

        public ConduitBox()
        {
            SuspendLayout();

            _buttons.Controls.Add(_sendRight);
            _buttons.Controls.Add(_sendLeft);
            _buttons.Controls.Add(_sendBoth);
            _buttons.Controls.Add(_clearBtn);

            // Dock fill is added first so the top-docked items stack above it
            Controls.Add(_transit);
            Controls.Add(_transitHeader);
            Controls.Add(_buttons);
            Controls.Add(_box);
            Controls.Add(_header);
            Controls.Add(_state);

            ResumeLayout(false);
            PerformLayout();

            _sendRight.Click += (s, e) => SendRequested?.Invoke(1);
            _sendLeft.Click += (s, e) => SendRequested?.Invoke(-1);
            _sendBoth.Click += (s, e) => SendRequested?.Invoke(0);
            _clearBtn.Click += (s, e) => { _box.Text = ""; _lastTransit = ""; SetState("cleared"); };
        }

        /// <summary>Place relayed text into the box (the visible pass-through).</summary>
        public void SetTransitText(string text, int fromPanel, int toPanel)
        {
            void apply()
            {
                _box.Text = text;
                _lastTransit = text;
                SetState($"passing through: panel {fromPanel} → panel {toPanel} (edit now to revise it)");
            }
            if (InvokeRequired) BeginInvoke(new Action(apply)); else apply();
        }

        /// <summary>Box content has been dispatched — log it and clear the box.</summary>
        public void MarkSent(string line)
        {
            void apply()
            {
                AppendTransit(line);
                _box.Text = "";
                _lastTransit = "";
                SetState("sent — idle");
            }
            if (InvokeRequired) BeginInvoke(new Action(apply)); else apply();
        }

        public void AppendTransit(string line)
        {
            void apply()
            {
                _transit.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + "\r\n");
            }
            if (InvokeRequired) BeginInvoke(new Action(apply)); else apply();
        }

        public void SetState(string s)
        {
            void apply() { _state.Text = s; }
            if (InvokeRequired) BeginInvoke(new Action(apply)); else apply();
        }
    }
}
