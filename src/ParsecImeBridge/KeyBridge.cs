using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using static ParsecImeBridge.Native;

namespace ParsecImeBridge
{
    /// <summary>
    /// Korean keyboard drivers turn Right Alt / Right Ctrl into the dedicated Hangul (0xF2) and
    /// Hanja (0xF1) scan codes, which Parsec does not forward. While a Parsec window has focus,
    /// this swallows the configured trigger keys and sends the Right Alt (E0 38) / Right Ctrl
    /// (E0 1D) scan codes instead, which Parsec forwards and the host's Korean driver turns
    /// back into Hangul / Hanja.
    /// </summary>
    sealed class KeyBridge : IDisposable
    {
        const ushort SCAN_RALT = 0x38;
        const ushort SCAN_RCTRL = 0x1D;
        const uint VK_ESCAPE = 0x1B;

        // Modifier keys with the scan codes used to release / re-press them on the host.
        static readonly (uint vk, ushort scan, bool extended)[] ModifierKeys =
        {
            (0xA2, 0x1D, false), (0xA3, 0x1D, true),
            (0xA0, 0x2A, false), (0xA1, 0x36, false),
            (0xA4, 0x38, false), (0xA5, 0x38, true),
            (0x5B, 0x5B, true), (0x5C, 0x5C, true),
        };

        readonly Settings _settings;
        readonly SynchronizationContext _ui;
        readonly LowLevelKeyboardProc _keyboardProc;
        readonly WinEventProc _foregroundProc;
        readonly HashSet<uint> _heldTriggers = new HashSet<uint>();
        IntPtr _keyboardHook;
        IntPtr _foregroundHook;

        Action<KeyTrigger> _capture;
        Modifiers _captureModifiers;
        uint _captureLastModifier;

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

        /// <summary>
        /// Swallows keyboard input until a key (with any held modifiers) is pressed, or a modifier
        /// is tapped on its own, then reports it. Esc reports null.
        /// </summary>
        public void BeginCapture(Action<KeyTrigger> done)
        {
            _capture = done;
            _captureModifiers = Modifiers.None;
            _captureLastModifier = 0;
        }

        public void CancelCapture() => _capture = null;

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
            if (nCode >= 0)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                if ((k.flags & LLKHF_INJECTED) == 0)
                {
                    if (_capture != null)
                    {
                        Capture(k);
                        return (IntPtr)1;
                    }
                    if (ParsecForeground && _settings.Enabled && HandleTrigger(k))
                        return (IntPtr)1;
                }
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        /// <summary>Returns true when the key event belongs to a trigger and must be swallowed.</summary>
        bool HandleTrigger(KBDLLHOOKSTRUCT k)
        {
            var vk = k.vkCode;
            if ((k.flags & LLKHF_UP) != 0)
                return _heldTriggers.Remove(vk) || (KeyTrigger.IsMakeOnly(vk) && IsTriggerKey(vk));
            if (_heldTriggers.Contains(vk)) return true; // auto-repeat

            var extended = (k.flags & LLKHF_EXTENDED) != 0;
            var held = HeldModifiers(vk);
            var modifiers = held.Aggregate(Modifiers.None, (m, key) => m | KeyTrigger.ModifierOf(key.vk));

            ushort scan;
            if (_settings.Hangul && _settings.HangulTrigger.Matches(vk, extended, modifiers)) scan = SCAN_RALT;
            else if (_settings.Hanja && _settings.HanjaTrigger.Matches(vk, extended, modifiers)) scan = SCAN_RCTRL;
            else return false;

            if (!KeyTrigger.IsMakeOnly(vk)) _heldTriggers.Add(vk);
            _ui.Post(_ => SendAction(scan, held), null);
            return true;
        }

        bool IsTriggerKey(uint vk) =>
            (_settings.Hangul && _settings.HangulTrigger.Vk == vk) || (_settings.Hanja && _settings.HanjaTrigger.Vk == vk);

        static List<(uint vk, ushort scan, bool extended)> HeldModifiers(uint exceptVk) =>
            ModifierKeys.Where(m => m.vk != exceptVk && (GetAsyncKeyState((int)m.vk) & 0x8000) != 0).ToList();

        /// <summary>
        /// Sends the action key to the host with the trigger's modifiers released, so the host
        /// sees a plain Hangul / Hanja press, then restores the modifiers the user is still holding.
        /// </summary>
        static void SendAction(ushort scan, List<(uint vk, ushort scan, bool extended)> held)
        {
            var inputs = new List<INPUT>();
            // A lone Alt / Win release would open the menu bar / Start menu; a Ctrl tap in between prevents that.
            if (held.Any(m => KeyTrigger.ModifierOf(m.vk) == Modifiers.Alt || KeyTrigger.ModifierOf(m.vk) == Modifiers.Win))
            {
                inputs.Add(ScanInput(0x1D, false, false));
                inputs.Add(ScanInput(0x1D, false, true));
            }
            inputs.AddRange(held.Select(m => ScanInput(m.scan, m.extended, true)));
            inputs.Add(ScanInput(scan, true, false));
            inputs.Add(ScanInput(scan, true, true));
            inputs.AddRange(held.Select(m => ScanInput(m.scan, m.extended, false)));
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }

        static INPUT ScanInput(ushort scan, bool extended, bool up) => new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wScan = scan,
                    dwFlags = KEYEVENTF_SCANCODE | (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0),
                }
            }
        };

        void Capture(KBDLLHOOKSTRUCT k)
        {
            var vk = k.vkCode;
            var up = (k.flags & LLKHF_UP) != 0;
            var extended = (k.flags & LLKHF_EXTENDED) != 0;
            var modifier = KeyTrigger.ModifierOf(vk);

            if (modifier != Modifiers.None)
            {
                if (!up)
                {
                    _captureModifiers |= modifier;
                    _captureLastModifier = vk;
                }
                else if (_captureLastModifier == vk)
                    FinishCapture(new KeyTrigger(vk, extended, _captureModifiers & ~modifier)); // tapped on its own
                else
                    _captureModifiers &= ~modifier;
                return;
            }

            if (up) return;
            FinishCapture(vk == VK_ESCAPE && _captureModifiers == Modifiers.None
                ? null
                : new KeyTrigger(vk, extended, _captureModifiers));
        }

        void FinishCapture(KeyTrigger trigger)
        {
            var done = _capture;
            _capture = null;
            _ui.Post(_ => done(trigger), null);
        }
    }
}
