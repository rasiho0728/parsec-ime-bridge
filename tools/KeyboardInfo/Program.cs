using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyboardInfo
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    sealed class KeyboardDevice
    {
        public string DeviceId, Name, Service, Manufacturer, Status, InfName, DriverVersion, DriverProvider, HardwareId;
    }

    /// <summary>
    /// Shows the keyboard layout/driver configuration and, via Raw Input,
    /// which physical device each key press comes from.
    /// </summary>
    sealed class MainForm : Form
    {
        static readonly string ReportPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keyboard-info.txt");

        readonly TextBox _report = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            WordWrap = false, Font = new Font("Consolas", 9.5f),
        };
        readonly ListView _keys = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        readonly Dictionary<IntPtr, string> _deviceNames = new Dictionary<IntPtr, string>();
        List<KeyboardDevice> _devices = new List<KeyboardDevice>();

        public MainForm()
        {
            Text = "Keyboard Info";
            Width = 1100;
            Height = 800;
            Font = new Font("Malgun Gothic", 9f);

            var refresh = new Button { Text = "새로고침", AutoSize = true };
            var copy = new Button { Text = "보고서 복사", AutoSize = true };
            var hint = new Label { Text = "아래 목록: 키를 누르면 어느 장치에서 어떤 값이 들어오는지 표시 (Raw Input)", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
            top.Controls.AddRange(new Control[] { refresh, copy, hint });

            foreach (var (name, width) in new[]
                     {
                         ("시간", 95), ("방향", 45), ("VK", 120), ("MakeCode", 75), ("E0/E1", 55), ("장치", 650),
                     })
                _keys.Columns.Add(name, width);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 480 };
            split.Panel1.Controls.Add(_report);
            split.Panel2.Controls.Add(_keys);

            Controls.Add(split);
            Controls.Add(top);

            refresh.Click += (s, e) => BuildReport();
            copy.Click += (s, e) => Clipboard.SetText(_report.Text);

            Load += (s, e) =>
            {
                BuildReport();
                var rid = new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RIDEV_INPUTSINK, hwndTarget = Handle };
                RegisterRawInputDevices(new[] { rid }, 1, Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            };
        }

        // ---------------------------------------------------------------- report

        void BuildReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Keyboard Info  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  ({Environment.MachineName}, {WindowsVersion()})");
            sb.AppendLine();

            Section(sb, "키보드 종류 (GetKeyboardType)");
            var type = GetKeyboardType(0);
            sb.AppendLine($"  Type    = {type} {(type == 8 ? "(Korean)" : type == 7 ? "(Japanese)" : type == 4 ? "(Enhanced 101/102)" : "")}");
            sb.AppendLine($"  Subtype = {GetKeyboardType(1)}");
            sb.AppendLine($"  F-keys  = {GetKeyboardType(2)}");

            Section(sb, "레이어 드라이버 (HKLM\\...\\i8042prt\\Parameters)");
            DumpValues(sb, Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\i8042prt\Parameters",
                n => n.StartsWith("LayerDriver") || n.StartsWith("Override") || n.StartsWith("Keyboard"));

            Section(sb, "Scancode Map (HKLM\\...\\Control\\Keyboard Layout)");
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Keyboard Layout"))
            {
                var map = key?.GetValue("Scancode Map") as byte[];
                sb.AppendLine(map == null ? "  (없음)" : "  " + DescribeScancodeMap(map));
            }

            Section(sb, "설치된 입력 레이아웃 (GetKeyboardLayoutList)");
            var count = GetKeyboardLayoutList(0, null);
            var hkls = new IntPtr[count];
            GetKeyboardLayoutList(count, hkls);
            var currentKlid = new StringBuilder(9);
            GetKeyboardLayoutName(currentKlid);
            foreach (var hkl in hkls)
            {
                var klid = KlidFromHkl(hkl);
                var (text, file) = LayoutInfo(klid);
                sb.AppendLine($"  HKL 0x{(long)hkl:X8}  KLID {klid}  {file,-14} {text}");
            }
            sb.AppendLine($"  이 프로그램 스레드의 현재 KLID = {currentKlid}");

            Section(sb, "사용자 레이아웃 설정 (HKCU\\Keyboard Layout)");
            DumpValues(sb, Registry.CurrentUser, @"Keyboard Layout\Preload", null, "Preload");
            DumpValues(sb, Registry.CurrentUser, @"Keyboard Layout\Substitutes", null, "Substitutes");
            DumpValues(sb, Registry.CurrentUser, @"Keyboard Layout\Toggle", null, "Toggle");

            Section(sb, "Microsoft 한국어 IME 설정 (HKCU\\Software\\Microsoft\\IME\\15.0\\IMEKR)");
            DumpTree(sb, Registry.CurrentUser, @"Software\Microsoft\IME\15.0\IMEKR", 1);

            Section(sb, "키보드 장치와 드라이버 (PnP)");
            _devices = QueryDevices();
            if (_devices.Count == 0) sb.AppendLine("  (조회 실패 또는 없음)");
            foreach (var d in _devices)
            {
                sb.AppendLine($"  ▶ {d.Name}  [{d.Status}]");
                sb.AppendLine($"      DeviceID  : {d.DeviceId}");
                sb.AppendLine($"      HardwareID: {d.HardwareId}");
                sb.AppendLine($"      Service   : {d.Service}   제조사: {d.Manufacturer}");
                sb.AppendLine($"      Driver    : {d.InfName}  v{d.DriverVersion}  ({d.DriverProvider})");
            }

            Section(sb, "Raw Input 키보드 장치");
            foreach (var (handle, path) in RawKeyboards())
                sb.AppendLine($"  0x{(long)handle:X}  {(path == "" ? "(이름 없음)" : path)}");

            _report.Text = sb.ToString();
            try { File.WriteAllText(ReportPath, _report.Text, Encoding.UTF8); }
            catch (IOException) { }
        }

        // Environment.OSVersion is capped at 6.2 for apps without a compatibility manifest.
        static string WindowsVersion()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                return $"{k?.GetValue("ProductName")} {k?.GetValue("DisplayVersion")} build {k?.GetValue("CurrentBuild")}.{k?.GetValue("UBR")}";
        }

        static void Section(StringBuilder sb, string title)
        {
            sb.AppendLine();
            sb.AppendLine($"■ {title}");
        }

        static void DumpValues(StringBuilder sb, RegistryKey root, string path, Func<string, bool> filter, string label = null)
        {
            using (var key = root.OpenSubKey(path))
            {
                var prefix = label == null ? "  " : $"  [{label}] ";
                if (key == null)
                {
                    sb.AppendLine($"{prefix}(키 없음)");
                    return;
                }
                var names = key.GetValueNames().Where(n => filter == null || filter(n)).ToList();
                if (names.Count == 0) sb.AppendLine($"{prefix}(값 없음)");
                foreach (var n in names)
                    sb.AppendLine($"{prefix}{(n == "" ? "(기본값)" : n)} = {FormatValue(key.GetValue(n))}");
            }
        }

        static void DumpTree(StringBuilder sb, RegistryKey root, string path, int depth)
        {
            using (var key = root.OpenSubKey(path))
            {
                if (key == null)
                {
                    sb.AppendLine($"  {path}: (키 없음)");
                    return;
                }
                sb.AppendLine($"  [{path}]");
                foreach (var n in key.GetValueNames())
                    sb.AppendLine($"    {(n == "" ? "(기본값)" : n)} = {FormatValue(key.GetValue(n))}");
                if (depth > 0)
                    foreach (var sub in key.GetSubKeyNames())
                        DumpTree(sb, root, path + "\\" + sub, depth - 1);
            }
        }

        static string FormatValue(object v) => v is byte[] bytes ? BitConverter.ToString(bytes) : v is int i ? $"{i} (0x{i:X})" : $"{v}";

        static string DescribeScancodeMap(byte[] map)
        {
            // Header (8 bytes) + count (4) + entries of [new scan (2), old scan (2)].
            var count = BitConverter.ToInt32(map, 8);
            var parts = new List<string>();
            for (var i = 0; i < count - 1; i++)
            {
                var to = BitConverter.ToUInt16(map, 12 + i * 4);
                var from = BitConverter.ToUInt16(map, 14 + i * 4);
                parts.Add($"{from:X4} → {to:X4}");
            }
            return string.Join(", ", parts) + $"   (raw {BitConverter.ToString(map)})";
        }

        static string KlidFromHkl(IntPtr hkl)
        {
            var value = (long)hkl;
            var lang = (int)(value & 0xFFFF);
            var device = (int)((value >> 16) & 0xFFFF);
            if ((device & 0xF000) == 0xF000)
            {
                // Variant layout: look up the "Layout Id" among the installed layouts.
                var layoutId = (device & 0x0FFF).ToString("X4");
                using (var layouts = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts"))
                    foreach (var klid in layouts?.GetSubKeyNames() ?? new string[0])
                        using (var k = layouts.OpenSubKey(klid))
                            if (string.Equals(k?.GetValue("Layout Id") as string, layoutId, StringComparison.OrdinalIgnoreCase))
                                return klid;
                return $"?{device:X4}";
            }
            return (device == 0 ? lang : device).ToString("X8");
        }

        static (string text, string file) LayoutInfo(string klid)
        {
            using (var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{klid}"))
                return (k?.GetValue("Layout Text") as string ?? "", k?.GetValue("Layout File") as string ?? "");
        }

        static List<KeyboardDevice> QueryDevices()
        {
            var devices = new List<KeyboardDevice>();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE PNPClass = 'Keyboard'"))
                    foreach (ManagementObject o in s.Get())
                        devices.Add(new KeyboardDevice
                        {
                            DeviceId = o["DeviceID"] as string,
                            Name = o["Name"] as string,
                            Service = o["Service"] as string,
                            Manufacturer = o["Manufacturer"] as string,
                            Status = o["Status"] as string,
                            HardwareId = (o["HardwareID"] as string[])?.FirstOrDefault(),
                        });
                using (var s = new ManagementObjectSearcher("SELECT DeviceID, InfName, DriverVersion, DriverProviderName FROM Win32_PnPSignedDriver WHERE DeviceClass = 'KEYBOARD'"))
                    foreach (ManagementObject o in s.Get())
                    {
                        var d = devices.FirstOrDefault(x => string.Equals(x.DeviceId, o["DeviceID"] as string, StringComparison.OrdinalIgnoreCase));
                        if (d == null) continue;
                        d.InfName = o["InfName"] as string;
                        d.DriverVersion = o["DriverVersion"] as string;
                        d.DriverProvider = o["DriverProviderName"] as string;
                    }
            }
            catch (ManagementException) { }
            return devices;
        }

        static IEnumerable<(IntPtr handle, string path)> RawKeyboards()
        {
            uint count = 0;
            var size = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
            GetRawInputDeviceList(null, ref count, size);
            var list = new RAWINPUTDEVICELIST[count];
            GetRawInputDeviceList(list, ref count, size);
            return list.Where(d => d.dwType == RIM_TYPEKEYBOARD).Select(d => (d.hDevice, DevicePath(d.hDevice))).ToList();
        }

        // ---------------------------------------------------------------- raw input

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT) OnRawInput(m.LParam);
            base.WndProc(ref m);
        }

        void OnRawInput(IntPtr hRawInput)
        {
            var headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
            uint size = 0;
            GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, headerSize) != size) return;
                var header = (RAWINPUTHEADER)Marshal.PtrToStructure(buffer, typeof(RAWINPUTHEADER));
                if (header.dwType != RIM_TYPEKEYBOARD) return;
                var kb = (RAWKEYBOARD)Marshal.PtrToStructure(buffer + (int)headerSize, typeof(RAWKEYBOARD));

                var up = (kb.Flags & RI_KEY_BREAK) != 0;
                var prefix = (kb.Flags & RI_KEY_E0) != 0 ? "E0" : (kb.Flags & RI_KEY_E1) != 0 ? "E1" : "";
                var item = new ListViewItem(new[]
                {
                    DateTime.Now.ToString("HH:mm:ss.fff"),
                    up ? "UP" : "DOWN",
                    $"0x{kb.VKey:X2} {(Keys)kb.VKey}",
                    $"0x{kb.MakeCode:X2}",
                    prefix,
                    DeviceLabel(header.hDevice),
                });
                _keys.Items.Add(item);
                if (_keys.Items.Count > 500) _keys.Items.RemoveAt(0);
                item.EnsureVisible();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        string DeviceLabel(IntPtr hDevice)
        {
            if (hDevice == IntPtr.Zero) return "(장치 없음 = SendInput 등 소프트웨어 주입)";
            if (_deviceNames.TryGetValue(hDevice, out var label)) return label;

            var path = DevicePath(hDevice);
            // \\?\HID#VID_xxxx&PID_xxxx#...#{guid}  →  HID\VID_xxxx&PID_xxxx\...
            var instanceId = path.TrimStart('\\', '?');
            var guidAt = instanceId.LastIndexOf("#{", StringComparison.Ordinal);
            if (guidAt > 0) instanceId = instanceId.Substring(0, guidAt);
            instanceId = instanceId.Replace('#', '\\');
            var device = _devices.FirstOrDefault(d => string.Equals(d.DeviceId, instanceId, StringComparison.OrdinalIgnoreCase));
            label = device != null ? $"{device.Name} | {device.Service} | {device.DeviceId}" : path;
            _deviceNames[hDevice] = label;
            return label;
        }

        static string DevicePath(IntPtr hDevice)
        {
            uint chars = 0;
            GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, null, ref chars);
            var sb = new StringBuilder((int)chars + 1);
            GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, sb, ref chars);
            return sb.ToString();
        }

        // ---------------------------------------------------------------- native

        const int WM_INPUT = 0x00FF;
        const uint RIDEV_INPUTSINK = 0x00000100;
        const uint RID_INPUT = 0x10000003;
        const uint RIDI_DEVICENAME = 0x20000007;
        const uint RIM_TYPEKEYBOARD = 1;
        const ushort RI_KEY_BREAK = 0x01, RI_KEY_E0 = 0x02, RI_KEY_E1 = 0x04;

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICELIST
        {
            public IntPtr hDevice;
            public uint dwType;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [DllImport("user32.dll")]
        static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, int size);

        [DllImport("user32.dll")]
        static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        [DllImport("user32.dll")]
        static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint count, uint size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint command, StringBuilder data, ref uint size);

        [DllImport("user32.dll")]
        static extern int GetKeyboardType(int typeFlag);

        [DllImport("user32.dll")]
        static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] list);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetKeyboardLayoutName(StringBuilder klid);
    }
}
