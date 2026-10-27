using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Threading;
using System.Windows.Forms;

namespace ParsecImeBridge
{
    sealed class TrayApp : ApplicationContext
    {
        readonly Settings _settings = Settings.Load();
        readonly NotifyIcon _tray = new NotifyIcon();
        readonly ContextMenuStrip _menu = new ContextMenuStrip();
        readonly ToolStripMenuItem _status = new ToolStripMenuItem { Enabled = false };
        readonly ToolStripMenuItem _enabledItem = new ToolStripMenuItem("사용");
        readonly ToolStripMenuItem _hangulItem = new ToolStripMenuItem();
        readonly ToolStripMenuItem _hanjaItem = new ToolStripMenuItem();
        readonly ToolStripMenuItem _settingsItem = new ToolStripMenuItem("키 설정...");
        readonly ToolStripMenuItem _autoStartItem = new ToolStripMenuItem("Windows 시작 시 실행");
        readonly Icon _offIcon = MakeIcon(Color.Gray);
        readonly Icon _idleIcon = MakeIcon(Color.FromArgb(0x3B, 0x6E, 0xD8));
        readonly Icon _activeIcon = MakeIcon(Color.FromArgb(0x1E, 0x9E, 0x4A));
        readonly KeyBridge _bridge;
        SettingsForm _settingsForm;

        public TrayApp()
        {
            _menu.Items.AddRange(new ToolStripItem[]
            {
                _status,
                new ToolStripSeparator(),
                _enabledItem, _hangulItem, _hanjaItem,
                new ToolStripSeparator(),
                _settingsItem, _autoStartItem,
                new ToolStripSeparator(),
                new ToolStripMenuItem("종료", null, (s, e) => ExitThread()),
            });
            _enabledItem.Click += (s, e) => Toggle(() => _settings.Enabled = !_settings.Enabled);
            _hangulItem.Click += (s, e) => Toggle(() => _settings.Hangul = !_settings.Hangul);
            _hanjaItem.Click += (s, e) => Toggle(() => _settings.Hanja = !_settings.Hanja);
            _settingsItem.Click += (s, e) => ShowSettings();
            _autoStartItem.Click += (s, e) =>
            {
                Settings.AutoStart = !Settings.AutoStart;
                UpdateUi();
            };

            _tray.ContextMenuStrip = _menu;
            _tray.DoubleClick += (s, e) => Toggle(() => _settings.Enabled = !_settings.Enabled);

            // Creating the menu control installed the WinForms SynchronizationContext.
            _bridge = new KeyBridge(_settings, SynchronizationContext.Current);
            _bridge.StateChanged += UpdateUi;
            _bridge.Start();

            UpdateUi();
            _tray.Visible = true;
        }

        void Toggle(Action change)
        {
            change();
            _settings.Save();
            UpdateUi();
        }

        void ShowSettings()
        {
            if (_settingsForm != null)
            {
                _settingsForm.Activate();
                return;
            }
            _settingsForm = new SettingsForm(_settings, _bridge);
            _settingsForm.FormClosed += (s, e) =>
            {
                _settingsForm.Dispose();
                _settingsForm = null;
                UpdateUi();
            };
            _settingsForm.Show();
            _settingsForm.Activate();
        }

        void UpdateUi()
        {
            _hangulItem.Text = $"한/영 전환: {_settings.HangulTrigger}";
            _hanjaItem.Text = $"한자 변환: {_settings.HanjaTrigger}";
            _enabledItem.Checked = _settings.Enabled;
            _hangulItem.Checked = _settings.Hangul;
            _hanjaItem.Checked = _settings.Hanja;
            _hangulItem.Enabled = _hanjaItem.Enabled = _settings.Enabled;
            _autoStartItem.Checked = Settings.AutoStart;

            string state;
            if (!_settings.Enabled)
            {
                state = "꺼짐";
                _tray.Icon = _offIcon;
            }
            else if (_bridge.ParsecForeground)
            {
                state = "Parsec 활성 · 변환 중";
                _tray.Icon = _activeIcon;
            }
            else
            {
                state = "대기 (Parsec 창 아님)";
                _tray.Icon = _idleIcon;
            }
            _status.Text = $"상태: {state}";
            _tray.Text = $"Parsec IME Bridge - {state}";
        }

        static Icon MakeIcon(Color background)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var brush = new SolidBrush(background))
                using (var path = RoundedRect(new Rectangle(1, 1, 30, 30), 7))
                using (var font = new Font("Malgun Gothic", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.FillPath(brush, path);
                    g.DrawString("한", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
                }
                var hIcon = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(hIcon).Clone();
                Native.DestroyIcon(hIcon);
                return icon;
            }
        }

        static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void ExitThreadCore()
        {
            _bridge.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            base.ExitThreadCore();
        }
    }
}
