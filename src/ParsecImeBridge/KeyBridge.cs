using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using static ParsecImeBridge.Native;

namespace ParsecImeBridge
{
    /// <summary>
    /// Korean keyboard drivers turn Right Alt / Right Ctrl into the dedicated Hangul (0xF2) and
    /// Hanja (0xF1) scan codes, which Parsec does not forward. While a Parsec window has focus,
    /// this swaps them for the Right Alt (E0 38) / Right Ctrl (E0 1D) scan codes, which Parsec
    /// forwards and the host's Korean driver turns back into Hangul / Hanja.
    /// </summary>
    sealed class KeyBridge : IDisposable
    {
        const uint VK_HANGUL = 0x15;
        const uint VK_HANJA = 0x19;
        const uint SCAN_HANGUL = 0xF2;
        const uint SCAN_HANJA = 0xF1;
        const ushort SCAN_RALT = 0x38;
        const ushort SCAN_RCTRL = 0x1D;

        readonly Settings _settings;
        readonly SynchronizationContext _ui;
        readonly LowLevelKeyboardProc _keyboardProc;
        readonly WinEventProc _foregroundProc;
        IntPtr _keyboardHook;
        IntPtr _foregroundHook;

        public KeyBridge(Settings settings, SynchronizationContext ui)
        {
            _settings = settings;
            _ui = ui;
            _keyboardProc = OnKeyboard;
            _foregroundProc = OnForegroundChanged;
        }

        public bool ParsecForeground { get; private set; }

        public event Action StateChanged;

        public void Start()
        {
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(null), 0);
            if (_keyboardHook == IntPtr.Zero)
                throw new InvalidOperationException($"키보드 훅 설치 실패 (err={Marshal.GetLastWin32Error()})");
            _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);
            UpdateForeground(GetForegroundWindow());
        }

        public void Dispose()
        {
            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
            if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
            _keyboardHook = _foregroundHook = IntPtr.Zero;
        }

        void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
            => UpdateForeground(hwnd);

        void UpdateForeground(IntPtr hwnd)
        {
            var parsec = IsParsecWindow(hwnd);
            if (parsec == ParsecForeground) return;
            ParsecForeground = parsec;
            StateChanged?.Invoke();
        }

        static bool IsParsecWindow(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                    return p.ProcessName.StartsWith("parsec", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && ParsecForeground && _settings.Enabled)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                var replacement = (k.flags & LLKHF_INJECTED) == 0 ? Replacement(k) : (ushort)0;
                if (replacement != 0)
                {
                    // These keys usually report key-down only, so each down becomes a full press.
                    if ((k.flags & LLKHF_UP) == 0)
                        _ui.Post(_ => PressExtendedScan(replacement), null);
                    return (IntPtr)1;
                }
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        ushort Replacement(KBDLLHOOKSTRUCT k)
        {
            if (_settings.Hangul && k.vkCode == VK_HANGUL && k.scanCode == SCAN_HANGUL) return SCAN_RALT;
            if (_settings.Hanja && k.vkCode == VK_HANJA && k.scanCode == SCAN_HANJA) return SCAN_RCTRL;
            return 0;
        }

        static void PressExtendedScan(ushort scan)
        {
            const uint flags = KEYEVENTF_SCANCODE | KEYEVENTF_EXTENDEDKEY;
            var inputs = new[] { ScanInput(scan, flags), ScanInput(scan, flags | KEYEVENTF_KEYUP) };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        static INPUT ScanInput(ushort scan, uint flags) => new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wScan = scan, dwFlags = flags } }
        };
    }
}
