using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ParsecImeBridge
{
    [Flags]
    enum Modifiers
    {
        None = 0,
        Ctrl = 1,
        Shift = 2,
        Alt = 4,
        Win = 8,
    }

    /// <summary>A physical key, optionally with modifiers, that triggers an action.</summary>
    sealed class KeyTrigger
    {
        public const uint VK_HANGUL = 0x15;
        public const uint VK_HANJA = 0x19;

        // Keys that exist both on the main block and the numpad; only for these does the
        // extended flag tell two physical keys apart.
        static readonly HashSet<uint> ExtendedSensitive = new HashSet<uint>
        {
            0x0D, 0x0C, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E,
        };

        public KeyTrigger(uint vk, bool extended, Modifiers modifiers)
        {
            Vk = vk;
            Extended = ExtendedSensitive.Contains(vk) ? extended : (bool?)null;
            Modifiers = modifiers;
        }

        KeyTrigger(uint vk, bool? extended, Modifiers modifiers)
        {
            Vk = vk;
            Extended = extended;
            Modifiers = modifiers;
        }

        public uint Vk { get; }

        /// <summary>Null when the extended flag doesn't matter for this key.</summary>
        public bool? Extended { get; }

        public Modifiers Modifiers { get; }

        public static KeyTrigger DefaultHangul => new KeyTrigger(VK_HANGUL, null, Modifiers.None);
        public static KeyTrigger DefaultHanja => new KeyTrigger(VK_HANJA, null, Modifiers.None);

        public bool Matches(uint vk, bool extended, Modifiers modifiers) =>
            vk == Vk && (Extended == null || Extended == extended) && modifiers == Modifiers;

        public bool SameKeyAs(KeyTrigger other) =>
            Vk == other.Vk && Modifiers == other.Modifiers &&
            (Extended == null || other.Extended == null || Extended == other.Extended);

        /// <summary>Hangul/Hanja keys report key-down only; there is no release to wait for.</summary>
        public static bool IsMakeOnly(uint vk) => vk == VK_HANGUL || vk == VK_HANJA;

        public static Modifiers ModifierOf(uint vk)
        {
            switch (vk)
            {
                case 0xA2: case 0xA3: return Modifiers.Ctrl;
                case 0xA0: case 0xA1: return Modifiers.Shift;
                case 0xA4: case 0xA5: return Modifiers.Alt;
                case 0x5B: case 0x5C: return Modifiers.Win;
                default: return Modifiers.None;
            }
        }

        public string Serialize() =>
            $"{Vk:X2}|{(Extended == null ? "*" : Extended.Value ? "1" : "0")}|{(int)Modifiers}";

        public static KeyTrigger Parse(string text, KeyTrigger fallback)
        {
            var parts = text?.Split('|');
            if (parts == null || parts.Length != 3) return fallback;
            try
            {
                var vk = Convert.ToUInt32(parts[0], 16);
                var extended = parts[1] == "*" ? (bool?)null : parts[1] == "1";
                return new KeyTrigger(vk, extended, (Modifiers)int.Parse(parts[2]));
            }
            catch (FormatException) { return fallback; }
            catch (OverflowException) { return fallback; }
        }

        public override string ToString()
        {
            var parts = new List<string>();
            if ((Modifiers & Modifiers.Ctrl) != 0) parts.Add("Ctrl");
            if ((Modifiers & Modifiers.Shift) != 0) parts.Add("Shift");
            if ((Modifiers & Modifiers.Alt) != 0) parts.Add("Alt");
            if ((Modifiers & Modifiers.Win) != 0) parts.Add("Win");
            parts.Add(KeyName());
            return string.Join(" + ", parts);
        }

        string KeyName()
        {
            switch (Vk)
            {
                // Korean keyboard drivers report Right Alt / Right Ctrl as these keys.
                case VK_HANGUL: return "오른쪽 Alt (한/영 키)";
                case VK_HANJA: return "오른쪽 Ctrl (한자 키)";
                case 0xA0: return "왼쪽 Shift";
                case 0xA1: return "오른쪽 Shift";
                case 0xA2: return "왼쪽 Ctrl";
                case 0xA3: return "오른쪽 Ctrl";
                case 0xA4: return "왼쪽 Alt";
                case 0xA5: return "오른쪽 Alt";
                case 0x5B: return "왼쪽 Win";
                case 0x5C: return "오른쪽 Win";
                case 0x20: return "Space";
                case 0x0D: return Extended == true ? "숫자패드 Enter" : "Enter";
            }
            if ((Vk >= 0x30 && Vk <= 0x39) || (Vk >= 0x41 && Vk <= 0x5A)) return ((char)Vk).ToString();
            var name = ((Keys)Vk).ToString();
            return Extended == false ? $"숫자패드 {name}" : name;
        }
    }
}
