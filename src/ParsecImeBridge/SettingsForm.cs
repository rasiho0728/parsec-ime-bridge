using System;
using System.Drawing;
using System.Windows.Forms;

namespace ParsecImeBridge
{
    /// <summary>Lets the user pick which keys trigger Hangul / Hanja in Parsec.</summary>
    sealed class SettingsForm : Form
    {
        readonly Settings _settings;
        readonly KeyBridge _bridge;
        readonly TriggerRow _hangul;
        readonly TriggerRow _hanja;
        readonly Button _ok = new Button { Text = "확인", AutoSize = true };
        readonly Button _cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };

        public SettingsForm(Settings settings, KeyBridge bridge)
        {
            _settings = settings;
            _bridge = bridge;

            Text = "Parsec IME Bridge 설정";
            Font = new Font("Malgun Gothic", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);
            AcceptButton = _ok;
            CancelButton = _cancel;

            var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Dock = DockStyle.Fill };
            _hangul = new TriggerRow(this, table, "한/영 전환", settings.HangulTrigger, KeyTrigger.DefaultHangul);
            _hanja = new TriggerRow(this, table, "한자 변환", settings.HanjaTrigger, KeyTrigger.DefaultHanja);

            var note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(460, 0),
                Margin = new Padding(3, 10, 3, 6),
                ForeColor = SystemColors.GrayText,
                Text = "「변경」을 누른 뒤 원하는 키나 조합(예: Ctrl + Shift + C)을 누르세요.\n" +
                       "Esc를 누르면 취소됩니다.\n" +
                       "지정한 키는 Parsec 창에서 원래 기능으로 쓸 수 없습니다.",
            };
            table.Controls.Add(note, 0, 2);
            table.SetColumnSpan(note, 4);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            buttons.Controls.AddRange(new Control[] { _cancel, _ok });
            table.Controls.Add(buttons, 0, 3);
            table.SetColumnSpan(buttons, 4);

            Controls.Add(table);

            // Shown modeless, so DialogResult alone doesn't close the form.
            _ok.Click += (s, e) => Save();
            _cancel.Click += (s, e) => Close();
            FormClosed += (s, e) => _bridge.CancelCapture();
        }

        void Save()
        {
            if (_hangul.Trigger.SameKeyAs(_hanja.Trigger))
            {
                MessageBox.Show(this, "한/영 전환과 한자 변환에 같은 키를 지정할 수 없습니다.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _settings.HangulTrigger = _hangul.Trigger;
            _settings.HanjaTrigger = _hanja.Trigger;
            _settings.Save();
            DialogResult = DialogResult.OK;
            Close();
        }

        void BeginCapture(TriggerRow row)
        {
            SetCapturing(true);
            row.Box.Text = "키를 누르세요… (Esc: 취소)";
            _bridge.BeginCapture(trigger =>
            {
                if (trigger != null) row.Trigger = trigger;
                row.Refresh();
                SetCapturing(false);
            });
        }

        void SetCapturing(bool capturing)
        {
            foreach (var row in new[] { _hangul, _hanja }) row.SetButtonsEnabled(!capturing);
            _ok.Enabled = _cancel.Enabled = !capturing;
        }

        sealed class TriggerRow
        {
            readonly Button _change = new Button { Text = "변경", AutoSize = true };
            readonly Button _reset = new Button { Text = "기본값", AutoSize = true };

            public TriggerRow(SettingsForm form, TableLayoutPanel table, string label, KeyTrigger trigger, KeyTrigger fallback)
            {
                Trigger = trigger;
                var row = table.RowCount++;
                table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 3) }, 0, row);
                table.Controls.Add(Box, 1, row);
                table.Controls.Add(_change, 2, row);
                table.Controls.Add(_reset, 3, row);

                _change.Click += (s, e) => form.BeginCapture(this);
                _reset.Click += (s, e) =>
                {
                    Trigger = fallback;
                    Refresh();
                };
                Refresh();
            }

            public TextBox Box { get; } = new TextBox { ReadOnly = true, Width = 240, TabStop = false };

            public KeyTrigger Trigger { get; set; }

            public void Refresh() => Box.Text = Trigger.ToString();

            public void SetButtonsEnabled(bool enabled) => _change.Enabled = _reset.Enabled = enabled;
        }
    }
}
