using System;
using System.Collections.Generic;
using System.Text;
using NovaEngine.InputSystem.Utilities;

namespace NovaEngine.InputSystem
{
    /// <summary>
    /// Unity 의 InputControlPath — "&lt;Gamepad&gt;/leftStick/x", "/Keyboard/space", "*/{Submit}", "&lt;Touchscreen&gt;/touch*/position".
    ///  &lt;레이아웃&gt; (기반 레이아웃도 맞는다 — &lt;Gamepad&gt; 는 XInput 패드), {용도}, 이름 · 별칭 (* 와일드카드), #(표시 이름)
    /// </summary>
    public static class InputControlPath
    {
        public const string Wildcard = "*";
        public const string DoubleWildcard = "**";
        public const char Separator = '/';

        [Flags]
        public enum HumanReadableStringOptions { None = 0, OmitDevice = 1 << 1, UseShortNames = 1 << 2 }

        internal struct Component
        {
            public string layout;
            public List<string> usages;
            public string name;
            public string displayName;
            public bool isDoubleWildcard => name == DoubleWildcard;
        }

        // 경로 → 조각 (첫 조각 = 장치)
        internal static List<Component> Parse(string path)
        {
            var list = new List<Component>();
            if (string.IsNullOrEmpty(path)) return list;
            int i = 0;
            if (path[0] == Separator) i = 1;
            var name = new StringBuilder();
            var c = new Component();
            bool any = false;
            while (i <= path.Length)
            {
                if (i == path.Length || path[i] == Separator)
                {
                    if (name.Length > 0) c.name = name.ToString();
                    if (any || name.Length > 0) list.Add(c);
                    c = new Component();
                    name.Clear();
                    any = false;
                    i++;
                    continue;
                }
                char ch = path[i];
                if (ch == '<')
                {
                    int end = path.IndexOf('>', i + 1);
                    if (end < 0) end = path.Length;
                    c.layout = path.Substring(i + 1, end - i - 1);
                    any = true;
                    i = end + 1;
                }
                else if (ch == '{')
                {
                    int end = path.IndexOf('}', i + 1);
                    if (end < 0) end = path.Length;
                    (c.usages ??= new List<string>()).Add(path.Substring(i + 1, end - i - 1));
                    any = true;
                    i = end + 1;
                }
                else if (ch == '#' && i + 1 < path.Length && path[i + 1] == '(')
                {
                    int end = path.IndexOf(')', i + 2);
                    if (end < 0) end = path.Length;
                    c.displayName = path.Substring(i + 2, end - i - 2);
                    any = true;
                    i = end + 1;
                }
                else if (ch == '\\' && i + 1 < path.Length)
                {
                    name.Append(path[i + 1]);
                    i += 2;
                }
                else
                {
                    name.Append(ch);
                    i++;
                }
            }
            return list;
        }

        internal static string JoinControls(List<Component> comps, int start)
        {
            var sb = new StringBuilder();
            for (int i = start; i < comps.Count; i++)
            {
                if (sb.Length > 0) sb.Append(Separator);
                var c = comps[i];
                if (c.layout != null) sb.Append('<').Append(c.layout).Append('>');
                if (c.usages != null) foreach (var u in c.usages) sb.Append('{').Append(u).Append('}');
                if (c.name != null) sb.Append(c.name);
            }
            return sb.ToString();
        }

        static bool NameMatches(string pattern, InputControl control)
        {
            if (StringUtil.WildcardMatch(pattern, control.m_Name)) return true;
            foreach (string a in control.m_Aliases)
                if (StringUtil.WildcardMatch(pattern, a)) return true;
            return false;
        }

        static bool ComponentMatches(in Component c, InputControl control)
        {
            if (c.layout != null && !control.IsBasedOnLayout(c.layout)) return false;
            if (c.usages != null)
                foreach (string u in c.usages)
                {
                    bool found = false;
                    foreach (string cu in control.m_Usages)
                        if (string.Equals(cu, u, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                    if (!found) return false;
                }
            if (c.name != null && c.name != Wildcard && !c.isDoubleWildcard && !NameMatches(c.name, control)) return false;
            if (c.displayName != null && !string.Equals(c.displayName, control.displayName, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        static void MatchChildren(InputControl control, List<Component> comps, int index, List<InputControl> results, bool firstOnly)
        {
            if (firstOnly && results.Count > 0) return;
            if (index >= comps.Count)
            {
                if (!results.Contains(control)) results.Add(control);
                return;
            }
            var comp = comps[index];
            if (comp.isDoubleWildcard)
            {
                // ** = 0 단계 이상 아무 깊이
                MatchChildren(control, comps, index + 1, results, firstOnly);
                foreach (var ch in control.m_Children) MatchChildren(ch, comps, index, results, firstOnly);
                return;
            }
            foreach (var ch in control.m_Children)
                if (ComponentMatches(comp, ch))
                    MatchChildren(ch, comps, index + 1, results, firstOnly);
        }

        // 모든 장치에서 경로에 맞는 컨트롤
        internal static void FindControls(string path, IReadOnlyList<InputDevice> devices, List<InputControl> results)
        {
            var comps = Parse(path);
            if (comps.Count == 0) return;
            foreach (var d in devices)
                if (ComponentMatches(comps[0], d))
                    MatchChildren(d, comps, 1, results, false);
        }

        /// <summary>control (보통 장치) 아래에서 path 에 맞는 첫 컨트롤. path 가 장치 조각부터면 (/Keyboard/w, &lt;Keyboard&gt;/w) 장치도 맞춘다</summary>
        public static InputControl TryFindControl(InputControl control, string path, int indexInPath = 0)
        {
            var results = new List<InputControl>();
            FindFrom(control, path, results, true);
            return results.Count > 0 ? results[0] : null;
        }

        public static InputControl[] TryFindControls(InputControl control, string path, int indexInPath = 0)
        {
            var results = new List<InputControl>();
            FindFrom(control, path, results, false);
            return results.ToArray();
        }

        static void FindFrom(InputControl control, string path, List<InputControl> results, bool firstOnly)
        {
            if (control == null || string.IsNullOrEmpty(path)) return;
            var comps = Parse(path);
            if (comps.Count == 0) return;
            bool absolute = path[0] == Separator || path[0] == '<' || path[0] == '*';
            if (absolute)
            {
                if (ComponentMatches(comps[0], control.device ?? control) && control is InputDevice)
                    MatchChildren(control, comps, 1, results, firstOnly);
                return;
            }
            MatchChildren(control, comps, 0, results, firstOnly);
        }

        // 자식 경로 ("leftStick/x")
        internal static InputControl TryFindChild(InputControl control, string path)
        {
            if (control == null || string.IsNullOrEmpty(path)) return null;
            var comps = Parse(path);
            var results = new List<InputControl>();
            MatchChildren(control, comps, 0, results, true);
            return results.Count > 0 ? results[0] : null;
        }

        public static bool Matches(string expected, InputControl control)
        {
            if (control == null || string.IsNullOrEmpty(expected)) return false;
            var chain = new List<InputControl>();
            for (var c = control; c != null; c = c.m_Parent) chain.Insert(0, c);
            var comps = Parse(expected);
            return MatchChain(comps, 0, chain, 0);
        }

        static bool MatchChain(List<Component> comps, int ci, List<InputControl> chain, int hi)
        {
            if (ci == comps.Count) return hi == chain.Count;
            if (hi == chain.Count) return comps[ci].isDoubleWildcard && ci + 1 == comps.Count;
            if (comps[ci].isDoubleWildcard)
                return MatchChain(comps, ci + 1, chain, hi) || MatchChain(comps, ci, chain, hi + 1);
            return ComponentMatches(comps[ci], chain[hi]) && MatchChain(comps, ci + 1, chain, hi + 1);
        }

        public static bool MatchesPrefix(string expected, InputControl control)
        {
            for (var c = control; c != null; c = c.m_Parent)
                if (Matches(expected, c)) return true;
            return false;
        }

        // 컨트롤 스킴의 장치 요구 ("<Gamepad>", "<Keyboard>")
        internal static bool MatchesDevice(string devicePath, InputDevice device)
        {
            if (string.IsNullOrEmpty(devicePath) || device == null) return false;
            var comps = Parse(devicePath);
            return comps.Count > 0 && ComponentMatches(comps[0], device);
        }

        public static string TryGetDeviceLayout(string path)
        {
            var comps = Parse(path);
            if (comps.Count == 0) return null;
            if (comps[0].layout != null) return comps[0].layout;
            if (comps[0].name == Wildcard) return Wildcard;
            return null;
        }

        public static string TryGetControlLayout(string path)
        {
            var comps = Parse(path);
            if (comps.Count < 2) return null;
            var proto = InputSystem.Prototype(comps[0].layout);
            var c = proto != null ? TryFindChild(proto, JoinControls(comps, 1)) : null;
            return c?.layout;
        }

        public static string Combine(InputControl parent, string path)
        {
            if (parent == null) return path;
            if (string.IsNullOrEmpty(path)) return parent.path;
            return path[0] == Separator ? parent.path + path : parent.path + Separator + path;
        }

        public static string ToHumanReadableString(string path, HumanReadableStringOptions options = HumanReadableStringOptions.None, InputControl control = null)
            => ToHumanReadableString(path, options, control, (options & HumanReadableStringOptions.UseShortNames) != 0);

        internal static string ToHumanReadableString(string path, HumanReadableStringOptions options, InputControl control, bool useShort)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var comps = Parse(path);
            if (comps.Count == 0) return path;
            string device = DeviceDisplayName(comps[0]);
            string controlText;
            InputControl found = null;
            if (control != null && Matches(path, control)) found = control;
            else if (comps.Count > 1)
            {
                var proto = comps[0].layout != null ? InputSystem.Prototype(comps[0].layout) : null;
                bool wild = false;
                for (int i = 1; i < comps.Count; i++) wild |= comps[i].name != null && comps[i].name.Contains('*');
                if (proto != null && !wild) found = TryFindChild(proto, JoinControls(comps, 1));
            }
            if (found != null && !(found is InputDevice))
            {
                var parts = new List<string>();
                for (var c = found; c != null && !(c is InputDevice); c = c.m_Parent)
                {
                    string t = c == found && useShort && !string.IsNullOrEmpty(c.shortDisplayName) ? c.shortDisplayName : c.displayName;
                    parts.Insert(0, t);
                }
                controlText = string.Join("/", parts);
            }
            else
            {
                var parts = new List<string>();
                for (int i = 1; i < comps.Count; i++)
                {
                    var c = comps[i];
                    if (c.name != null && c.name != Wildcard) parts.Add(Humanize(c.name));
                    else if (c.usages != null) parts.Add(string.Join(" ", c.usages));
                    else if (c.layout != null) parts.Add(c.layout);
                    else parts.Add("Any");
                }
                controlText = string.Join("/", parts);
            }
            if (comps.Count == 1) return device;
            if ((options & HumanReadableStringOptions.OmitDevice) != 0 || string.IsNullOrEmpty(device)) return controlText;
            return controlText + " [" + device + "]";
        }

        static string DeviceDisplayName(in Component c)
        {
            if (c.layout != null)
            {
                switch (c.layout.ToLowerInvariant())
                {
                    case "xinputcontroller": case "xinputcontrollerwindows": return "Xbox Controller";
                    case "dualshockgamepad": return "PlayStation Controller";
                    case "xrcontroller": return "XR Controller";
                    default: return c.layout;
                }
            }
            if (c.usages != null) return string.Join(" ", c.usages);
            if (c.name == Wildcard) return "Any";
            return c.name ?? string.Empty;
        }

        // "leftStick" → "Left Stick", "buttonSouth" → "Button South"
        internal static string Humanize(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if (i == 0) { sb.Append(char.ToUpperInvariant(ch)); continue; }
                if (char.IsUpper(ch) && !char.IsUpper(name[i - 1])) sb.Append(' ');
                else if (char.IsDigit(ch) && !char.IsDigit(name[i - 1])) sb.Append(' ');
                sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
