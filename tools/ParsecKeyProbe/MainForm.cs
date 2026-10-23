using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using static ParsecKeyProbe.Native;

namespace ParsecKeyProbe;

enum ToggleMethod
{
    SendVkHangul,
    SendScanRAlt,
}

record KeyEvent(DateTime Time, uint Vk, uint Scan, uint Flags, IntPtr ExtraInfo, bool Blocked);

sealed class MainForm : Form
{
    // Marks input we inject ourselves so the hook can skip it.
    static readonly IntPtr Marker = (IntPtr)0x50524B42;

    static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "probe-log.txt");
    static readonly string ParsecLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Parsec", "log.txt");

    readonly LowLevelKeyboardProc _proc;
    IntPtr _hook;
    readonly ConcurrentQueue<KeyEvent> _pending = new();
    bool _rAltHeld;

    readonly Label _parsecLabel = new() { AutoSize = true };
    readonly CheckBox _remapCheck = new() { Text = "오른쪽 Alt → 한/영 변환 적용", AutoSize = true };
    readonly CheckBox _injectedOnlyCheck = new() { Text = "주입된(원격) 입력에만", AutoSize = true, Checked = true };
    readonly ComboBox _methodCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly Button _testButton = new() { Text = "3초 후 변환 실행", AutoSize = true };
    readonly Button _clearButton = new() { Text = "로그 지우기", AutoSize = true };
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };

    public MainForm()
    {
        Text = "Parsec Key Probe";
        Width = 1100;
        Height = 650;
        Font = new Font("Malgun Gothic", 9f);

        _methodCombo.Items.AddRange(new object[]
        {
            "1. SendInput VK_HANGUL (0x15)",
            "2. SendInput 스캔코드 E0 38 (물리 RAlt 흉내)",
        });
        _methodCombo.SelectedIndex = 0;

        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        var status = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        status.Controls.AddRange(new Control[]
        {
            _parsecLabel,
            new Label { Text = admin ? "| 관리자 권한" : "| 일반 권한", AutoSize = true },
        });

        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        controls.Controls.AddRange(new Control[] { _remapCheck, _injectedOnlyCheck, _methodCombo, _testButton, _clearButton });

        foreach (var (name, width) in new[]
                 {
                     ("시간", 95), ("방향", 45), ("VK", 150), ("Scan", 55), ("Ext", 40), ("Injected", 65),
                     ("LowIL", 50), ("ExtraInfo", 90), ("출처", 70), ("처리", 70),
                 })
            _list.Columns.Add(name, width);

        Controls.Add(_list);
        Controls.Add(controls);
        Controls.Add(status);

        _testButton.Click += async (_, _) => await DelayedTestAsync();
        _clearButton.Click += (_, _) => _list.Items.Clear();

        var drain = new System.Windows.Forms.Timer { Interval = 50 };
        drain.Tick += (_, _) => Drain();
        drain.Start();

        var poll = new System.Windows.Forms.Timer { Interval = 1000 };
        poll.Tick += (_, _) => PollStatus();
        poll.Start();

        _proc = HookProc;
        Load += (_, _) =>
        {
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            WriteLog($"===== 세션 시작 {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {(admin ? "관리자" : "일반")} 권한 | " +
                     $"훅 {(_hook != IntPtr.Zero ? "OK" : $"실패 err={Marshal.GetLastWin32Error()}")} =====");
            PollStatus();
        };
        FormClosed += (_, _) =>
        {
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            WriteLog("===== 세션 종료 =====");
        };
    }

    ToggleMethod SelectedMethod => (ToggleMethod)_methodCombo.SelectedIndex;

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var up = (k.flags & LLKHF_UP) != 0;
            var ours = k.dwExtraInfo == Marker;
            var injected = (k.flags & LLKHF_INJECTED) != 0;
            var isRAlt = k.vkCode == 0xA5;

            var block = false;
            if (!ours && isRAlt && _remapCheck.Checked && (injected || !_injectedOnlyCheck.Checked))
            {
                block = true;
                // Toggle once per press, ignoring auto-repeat; run after the hook returns.
                if (!up && !_rAltHeld)
                {
                    var method = SelectedMethod;
                    BeginInvoke(() => RunToggle(method, "리맵"));
                }
                _rAltHeld = !up;
            }

            _pending.Enqueue(new KeyEvent(DateTime.Now, k.vkCode, k.scanCode, k.flags, k.dwExtraInfo, block));
            if (block) return 1;
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // IME state can't be read reliably from another process (TSF), so the result is checked by eye.
    void RunToggle(ToggleMethod method, string reason)
    {
        var sent = method == ToggleMethod.SendVkHangul
            ? Send(Key(0x15, 0, 0, Marker), Key(0x15, 0, KEYEVENTF_KEYUP, Marker))
            : Send(
                Key(0, 0x38, KEYEVENTF_SCANCODE | KEYEVENTF_EXTENDEDKEY, Marker),
                Key(0, 0x38, KEYEVENTF_SCANCODE | KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, Marker));
        AddAction($"[{reason}] 방식{(int)method + 1} SendInput={sent}");
    }

    static uint Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    async Task DelayedTestAsync()
    {
        _testButton.Enabled = false;
        for (var i = 3; i > 0; i--)
        {
            _testButton.Text = $"{i}초... 대상 창을 클릭하세요";
            await Task.Delay(1000);
        }
        RunToggle(SelectedMethod, "수동 테스트");
        _testButton.Text = "3초 후 변환 실행";
        _testButton.Enabled = true;
    }

    void Drain()
    {
        if (_pending.IsEmpty) return;
        _list.BeginUpdate();
        while (_pending.TryDequeue(out var e))
        {
            var injected = (e.Flags & LLKHF_INJECTED) != 0;
            var source = e.ExtraInfo == Marker ? "본 툴" : injected ? "주입" : "물리";
            var cells = new[]
            {
                e.Time.ToString("HH:mm:ss.fff"),
                (e.Flags & LLKHF_UP) != 0 ? "UP" : "DOWN",
                $"0x{e.Vk:X2} {VkName(e.Vk)}",
                $"0x{e.Scan:X2}",
                (e.Flags & LLKHF_EXTENDED) != 0 ? "E" : "",
                injected ? "Y" : "",
                (e.Flags & LLKHF_LOWER_IL_INJECTED) != 0 ? "Y" : "",
                $"0x{(long)e.ExtraInfo:X}",
                source,
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

    void PollStatus()
    {
        _parsecLabel.Text = $"Parsec: {ReadParsecState()}";
    }

    static readonly Regex ParsecLine = new(@"^\[\w (?<t>[^\]]+)\] (?<who>\S+) (?<ev>connected|disconnected)\.$",
        RegexOptions.Multiline);

    static string ReadParsecState()
    {
        try
        {
            using var fs = new FileStream(ParsecLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(Math.Max(0, fs.Length - 256 * 1024), SeekOrigin.Begin);
            var text = new StreamReader(fs, Encoding.UTF8).ReadToEnd().Replace("\r", "");
            var last = ParsecLine.Matches(text).LastOrDefault();
            if (last == null) return "기록 없음";
            var connected = last.Groups["ev"].Value == "connected";
            return $"{(connected ? "스트리밍 중" : "연결 안됨")} ({last.Groups["who"].Value}, {last.Groups["t"].Value})";
        }
        catch (Exception ex)
        {
            return $"로그 읽기 실패 ({ex.GetType().Name})";
        }
    }

    static string VkName(uint vk) => vk switch
    {
        0x12 => "Alt",
        0xA4 => "LAlt",
        0xA5 => "RAlt",
        0x15 => "Hangul",
        0x19 => "Hanja",
        0x11 => "Ctrl",
        0xA2 => "LCtrl",
        0xA3 => "RCtrl",
        0xE5 => "ProcessKey",
        _ => ((Keys)vk).ToString(),
    };

    static void WriteLog(string line)
    {
        try { File.AppendAllText(LogPath, line + Environment.NewLine); }
        catch (IOException) { }
    }
}
