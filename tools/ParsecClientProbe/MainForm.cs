using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using static ParsecClientProbe.Native;

namespace ParsecClientProbe
{
    sealed class Candidate
    {
        public Candidate(string name, params INPUT[] inputs)
        {
            Name = name;
            Inputs = inputs;
        }

        public string Name { get; }
        public INPUT[] Inputs { get; }
        public override string ToString() => Name;
    }

    sealed class KeyEvent
    {
        public DateTime Time;
        public uint Vk, Scan, Flags;
        public IntPtr ExtraInfo;
        public string Process;
        public bool Blocked;
    }

    sealed class MainForm : Form
    {
        // Marks input we inject ourselves so the hook can skip it.
        static readonly IntPtr Marker = (IntPtr)0x50524B43;

        static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client-probe-log.txt");

        readonly LowLevelKeyboardProc _proc;
        IntPtr _hook;
        readonly ConcurrentQueue<KeyEvent> _pending = new ConcurrentQueue<KeyEvent>();
        readonly Dictionary<uint, string> _processNames = new Dictionary<uint, string>();
        bool _toggleHeld;

        readonly Label _fgLabel = new Label { AutoSize = true };
        readonly CheckBox _remapCheck = new CheckBox { Text = "Parsec 창 활성 시 한/영(오른쪽 Alt) → 후보 키로 변환", AutoSize = true };
        readonly ComboBox _candidateCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        readonly Button _testButton = new Button { Text = "3초 후 후보 키 전송", AutoSize = true };
        readonly Button _clearButton = new Button { Text = "로그 지우기", AutoSize = true };
        readonly ListView _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };

        public MainForm()
        {
            Text = "Parsec Client Probe";
            Width = 1100;
            Height = 650;
            Font = new Font("Malgun Gothic", 9f);

            _candidateCombo.Items.AddRange(BuildCandidates());
            _candidateCombo.SelectedIndex = 0;

            var status = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
            status.Controls.Add(_fgLabel);

            var controls = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
            controls.Controls.AddRange(new Control[] { _candidateCombo, _testButton, _remapCheck, _clearButton });

            foreach (var (name, width) in new[]
                     {
                         ("시간", 95), ("방향", 45), ("VK", 150), ("Scan", 55), ("Ext", 40), ("Injected", 65),
                         ("ExtraInfo", 90), ("출처", 60), ("포커스 프로세스", 150), ("처리", 60),
                     })
                _list.Columns.Add(name, width);

            Controls.Add(_list);
            Controls.Add(controls);
            Controls.Add(status);

            _testButton.Click += async (s, e) => await DelayedSendAsync();
            _clearButton.Click += (s, e) => _list.Items.Clear();

            var drain = new Timer { Interval = 50 };
            drain.Tick += (s, e) => Drain();
            drain.Start();

            var poll = new Timer { Interval = 500 };
            poll.Tick += (s, e) => _fgLabel.Text = $"현재 포커스: {ForegroundProcess()} {(IsParsecForeground() ? "(Parsec 활성 ✔)" : "")}";
            poll.Start();

            _proc = HookProc;
            Load += (s, e) =>
            {
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                WriteLog($"===== 세션 시작 {DateTime.Now:yyyy-MM-dd HH:mm:ss} | " +
                         $"훅 {(_hook != IntPtr.Zero ? "OK" : $"실패 err={Marshal.GetLastWin32Error()}")} =====");
            };
            FormClosed += (s, e) =>
            {
                if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
                WriteLog("===== 세션 종료 =====");
            };
        }

        static object[] BuildCandidates() => new object[]
        {
            new Candidate("1. VK_HANGUL (0x15)", Press(0x15)),
            new Candidate("2. VK_HANGUL + scan 0x72", Press(0x15, 0x72)),
            new Candidate("3. RAlt: VK 0xA5 + scan E0 38", Press(0xA5, 0x38, true)),
            new Candidate("4. RAlt: 스캔코드만 E0 38", ScanPress(0x38, true)),
            new Candidate("5. 한/영 스캔코드만 0x72", ScanPress(0x72, false)),
            new Candidate("6. [대조군] LAlt", Press(0xA4, 0x38)),
            new Candidate("7. Shift+Space",
                Key(0xA0, 0x2A, 0, Marker), Key(0x20, 0x39, 0, Marker),
                Key(0x20, 0x39, KEYEVENTF_KEYUP, Marker), Key(0xA0, 0x2A, KEYEVENTF_KEYUP, Marker)),
            new Candidate("8. RCtrl", Press(0xA3, 0x1D, true)),
            new Candidate("9. Hanja (0x19)", Press(0x19)),
            new Candidate("10. F13", Press(0x7C)),
            new Candidate("11. F24", Press(0x87)),
            new Candidate("12. Scroll Lock", Press(0x91)),
            new Candidate("13. Pause", Press(0x13)),
            new Candidate("14. Menu (Apps)", Press(0x5D, 0x5D, true)),
        };

        static INPUT[] Press(ushort vk, ushort scan = 0, bool extended = false)
        {
            if (scan == 0) scan = (ushort)MapVirtualKey(vk, 0);
            var flags = extended ? KEYEVENTF_EXTENDEDKEY : 0;
            return new[] { Key(vk, scan, flags, Marker), Key(vk, scan, flags | KEYEVENTF_KEYUP, Marker) };
        }

        static INPUT[] ScanPress(ushort scan, bool extended)
        {
            var flags = KEYEVENTF_SCANCODE | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
            return new[] { Key(0, scan, flags, Marker), Key(0, scan, flags | KEYEVENTF_KEYUP, Marker) };
        }

        Candidate SelectedCandidate => (Candidate)_candidateCombo.SelectedItem;

        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                var up = (k.flags & LLKHF_UP) != 0;
                var ours = k.dwExtraInfo == Marker;
                var injected = (k.flags & LLKHF_INJECTED) != 0;
                var isToggleKey = k.vkCode == 0x15 || k.vkCode == 0xA5;
                var process = ForegroundProcess();

                var block = false;
                if (!ours && !injected && isToggleKey && _remapCheck.Checked && IsParsecProcess(process))
                {
                    block = true;
                    // Send once per press, ignoring auto-repeat; run after the hook returns.
                    if (!up && !_toggleHeld)
                    {
                        var candidate = SelectedCandidate;
                        BeginInvoke(new Action(() => SendCandidate(candidate, "리맵")));
                    }
                    _toggleHeld = !up;
                }

                _pending.Enqueue(new KeyEvent
                {
                    Time = DateTime.Now, Vk = k.vkCode, Scan = k.scanCode, Flags = k.flags,
                    ExtraInfo = k.dwExtraInfo, Process = process, Blocked = block,
                });
                if (block) return (IntPtr)1;
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        void SendCandidate(Candidate candidate, string reason)
        {
            var sent = SendInput((uint)candidate.Inputs.Length, candidate.Inputs, Marshal.SizeOf(typeof(INPUT)));
            var err = sent == candidate.Inputs.Length ? "" : $" err={Marshal.GetLastWin32Error()}";
            AddAction($"[{reason}] {candidate.Name} → SendInput={sent}/{candidate.Inputs.Length}{err} | 포커스: {ForegroundProcess()}");
        }

        async Task DelayedSendAsync()
        {
            _testButton.Enabled = false;
            var candidate = SelectedCandidate;
            for (var i = 3; i > 0; i--)
            {
                _testButton.Text = $"{i}초... Parsec 창을 클릭하세요";
                await Task.Delay(1000);
            }
            SendCandidate(candidate, "수동 전송");
            _testButton.Text = "3초 후 후보 키 전송";
            _testButton.Enabled = true;
        }

        string ForegroundProcess()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (_processNames.TryGetValue(pid, out var name)) return name;
            try { name = Process.GetProcessById((int)pid).ProcessName; }
            catch (ArgumentException) { name = $"pid {pid}"; }
            _processNames[pid] = name;
            return name;
        }

        bool IsParsecForeground() => IsParsecProcess(ForegroundProcess());

        static bool IsParsecProcess(string name) => name.StartsWith("parsec", StringComparison.OrdinalIgnoreCase);

        void Drain()
        {
            if (_pending.IsEmpty) return;
            _list.BeginUpdate();
            while (_pending.TryDequeue(out var e))
            {
                var injected = (e.Flags & LLKHF_INJECTED) != 0;
                var cells = new[]
                {
                    e.Time.ToString("HH:mm:ss.fff"),
                    (e.Flags & LLKHF_UP) != 0 ? "UP" : "DOWN",
                    $"0x{e.Vk:X2} {VkName(e.Vk)}",
                    $"0x{e.Scan:X2}",
                    (e.Flags & LLKHF_EXTENDED) != 0 ? "E" : "",
                    injected ? "Y" : "",
                    $"0x{(long)e.ExtraInfo:X}",
                    e.ExtraInfo == Marker ? "본 툴" : injected ? "주입" : "물리",
                    e.Process,
                    e.Blocked ? "차단" : "",
                };
                AddRow(new ListViewItem(cells));
                WriteLog(string.Join(" | ", cells));
            }
            _list.EndUpdate();
        }

        void AddAction(string text)
        {
            var item = new ListViewItem(DateTime.Now.ToString("HH:mm:ss.fff")) { BackColor = Color.LightYellow };
            item.SubItems.Add("ACT");
            item.SubItems.Add(text);
            AddRow(item);
            WriteLog($"{item.Text} | ACT | {text}");
        }

        void AddRow(ListViewItem item)
        {
            _list.Items.Add(item);
            if (_list.Items.Count > 2000) _list.Items.RemoveAt(0);
            item.EnsureVisible();
        }

        static string VkName(uint vk)
        {
            switch (vk)
            {
                case 0x12: return "Alt";
                case 0xA4: return "LAlt";
                case 0xA5: return "RAlt";
                case 0x15: return "Hangul";
                case 0x19: return "Hanja";
                case 0x11: return "Ctrl";
                case 0xA2: return "LCtrl";
                case 0xA3: return "RCtrl";
                case 0xE5: return "ProcessKey";
                default: return ((Keys)vk).ToString();
            }
        }

        static void WriteLog(string line)
        {
            try { File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch (IOException) { }
        }
    }
}
