using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ParsecImeBridge
{
    /// <summary>User settings, stored under HKCU\Software\ParsecImeBridge.</summary>
    sealed class Settings
    {
        const string KeyPath = @"Software\ParsecImeBridge";
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "ParsecImeBridge";

        public bool Enabled { get; set; } = true;
        public bool Hangul { get; set; } = true;
        public bool Hanja { get; set; } = true;

        public static Settings Load()
        {
            var s = new Settings();
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                if (key == null) return s;
                s.Enabled = ReadBool(key, nameof(Enabled), s.Enabled);
                s.Hangul = ReadBool(key, nameof(Hangul), s.Hangul);
                s.Hanja = ReadBool(key, nameof(Hanja), s.Hanja);
            }
            return s;
        }

        public void Save()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key.SetValue(nameof(Enabled), Enabled ? 1 : 0);
                key.SetValue(nameof(Hangul), Hangul ? 1 : 0);
                key.SetValue(nameof(Hanja), Hanja ? 1 : 0);
            }
        }

        static bool ReadBool(RegistryKey key, string name, bool fallback) =>
            key.GetValue(name) is int v ? v != 0 : fallback;

        static string RunCommand => $"\"{Application.ExecutablePath}\"";

        public static bool AutoStart
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                    return string.Equals(key?.GetValue(RunValueName) as string, RunCommand, StringComparison.OrdinalIgnoreCase);
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (value) key.SetValue(RunValueName, RunCommand);
                    else key.DeleteValue(RunValueName, false);
                }
            }
        }
    }
}
