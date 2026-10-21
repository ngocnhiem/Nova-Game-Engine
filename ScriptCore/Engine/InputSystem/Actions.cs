using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using NovaEngine.InputSystem.Utilities;

namespace NovaEngine.InputSystem
{
    /// <summary>Unity 의 InputActionType</summary>
    public enum InputActionType
    {
        /// <summary>값 (스틱 · WASD): 여러 컨트롤 중 가장 많이 움직인 것, 값이 바뀔 때마다 performed</summary>
        Value = 0,
        /// <summary>단추: 누름 문턱을 넘으면 performed, 놓으면 canceled</summary>
        Button = 1,
        /// <summary>그대로: 묶인 컨트롤 값이 바뀔 때마다 performed (started · canceled 없음)</summary>
        PassThrough = 2,
    }

    /// <summary>Unity 의 InputActionPhase</summary>
    public enum InputActionPhase { Disabled = 0, Waiting = 1, Started = 2, Performed = 3, Canceled = 4 }

    /// <summary>Unity 의 InputBinding — 동작과 컨트롤 경로 ("&lt;Keyboard&gt;/space"), 합성 ("2DVector") 과 그 부분, 그룹 (컨트롤 스킴)</summary>
    [Serializable]
    public struct InputBinding : IEquatable<InputBinding>
    {
        public const char Separator = ';';

        [Flags]
        internal enum Flags { None = 0, Composite = 1 << 2, PartOfComposite = 1 << 3 }

        [Flags]
        public enum DisplayStringOptions
        {
            DontUseShortDisplayNames = 1 << 0,
            DontOmitDevice = 1 << 1,
            DontIncludeInteractions = 1 << 2,
            IgnoreBindingOverrides = 1 << 3,
        }

        [Flags]
        public enum MatchOptions { EmptyGroupMatchesAny = 1 << 0 }

        internal string m_Name;
        internal string m_Id;
        internal string m_Path;
        internal string m_Interactions;
        internal string m_Processors;
        internal string m_Groups;
        internal string m_Action;
        internal Flags m_Flags;
        internal string m_OverridePath;
        internal string m_OverrideInteractions;
        internal string m_OverrideProcessors;

        public InputBinding(string path, string action = null, string groups = null, string processors = null, string interactions = null, string name = null)
        {
            m_Path = path;
            m_Action = action;
            m_Groups = groups;
            m_Processors = processors;
            m_Interactions = interactions;
            m_Name = name;
            m_Id = null;
            m_Flags = Flags.None;
            m_OverridePath = m_OverrideInteractions = m_OverrideProcessors = null;
        }

        public string name { get => m_Name; set => m_Name = value; }
        public Guid id
        {
            get => Guid.TryParse(m_Id, out var g) ? g : default;
            set => m_Id = value.ToString();
        }
        public string path { get => m_Path; set => m_Path = value; }
        public string overridePath { get => m_OverridePath; set => m_OverridePath = value; }
        public string interactions { get => m_Interactions; set => m_Interactions = value; }
        public string overrideInteractions { get => m_OverrideInteractions; set => m_OverrideInteractions = value; }
        public string processors { get => m_Processors; set => m_Processors = value; }
        public string overrideProcessors { get => m_OverrideProcessors; set => m_OverrideProcessors = value; }
        public string groups { get => m_Groups; set => m_Groups = value; }
        public string action { get => m_Action; set => m_Action = value; }
        public bool isComposite
        {
            get => (m_Flags & Flags.Composite) != 0;
            set => m_Flags = value ? m_Flags | Flags.Composite : m_Flags & ~Flags.Composite;
        }
        public bool isPartOfComposite
        {
            get => (m_Flags & Flags.PartOfComposite) != 0;
            set => m_Flags = value ? m_Flags | Flags.PartOfComposite : m_Flags & ~Flags.PartOfComposite;
        }
        public bool hasOverrides => m_OverridePath != null || m_OverrideInteractions != null || m_OverrideProcessors != null;
        public string effectivePath => m_OverridePath ?? m_Path;
        public string effectiveInteractions => m_OverrideInteractions ?? m_Interactions;
        public string effectiveProcessors => m_OverrideProcessors ?? m_Processors;

        internal void EnsureId()
        {
            if (string.IsNullOrEmpty(m_Id)) m_Id = Guid.NewGuid().ToString();
        }

        public string GetNameOfComposite() => isComposite ? NameAndParameters.Parse(effectivePath).name : null;

        public static InputBinding MaskByGroup(string group) => new InputBinding { m_Groups = group };
        public static InputBinding MaskByGroups(params string[] groups) => new InputBinding { m_Groups = string.Join(Separator.ToString(), groups ?? Array.Empty<string>()) };

        /// <summary>이 값을 마스크로 보고 binding 이 맞는가 (null 이 아닌 칸만 — 경로 · 그룹 · 동작 · 이름 · id)</summary>
        public bool Matches(InputBinding binding) => Matches(ref binding, 0);

        public bool Matches(InputBinding binding, MatchOptions options) => Matches(ref binding, options);

        internal bool Matches(ref InputBinding binding, MatchOptions options = 0)
        {
            if (m_Name != null && !string.Equals(m_Name, binding.m_Name, StringComparison.OrdinalIgnoreCase)) return false;
            if (m_Path != null && !string.Equals(m_Path, binding.m_Path, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(m_Path, binding.m_OverridePath, StringComparison.OrdinalIgnoreCase)) return false;
            if (m_Action != null)
            {
                string want = m_Action, have = binding.m_Action ?? string.Empty;
                int slash = want.IndexOf('/');
                if (slash >= 0 && have.IndexOf('/') < 0) want = want.Substring(slash + 1);
                if (!string.Equals(want, have, StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (m_Groups != null)
            {
                if (string.IsNullOrEmpty(binding.m_Groups))
                {
                    if ((options & MatchOptions.EmptyGroupMatchesAny) == 0) return false;
                }
                else
                {
                    bool any = false;
                    foreach (string g in m_Groups.Split(Separator))
                        if (g.Length > 0 && StringUtil.HasGroup(binding.m_Groups, g)) { any = true; break; }
                    if (!any) return false;
                }
            }
            if (!string.IsNullOrEmpty(m_Id) && !string.Equals(m_Id, binding.m_Id, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        public string ToDisplayString(DisplayStringOptions options = default, InputControl control = null)
            => ToDisplayString(out _, out _, options, control);

        public string ToDisplayString(out string deviceLayoutName, out string controlPath, DisplayStringOptions options = default, InputControl control = null)
        {
            string p = (options & DisplayStringOptions.IgnoreBindingOverrides) != 0 ? m_Path : effectivePath;
            deviceLayoutName = null;
            controlPath = null;
            if (isComposite) return NameAndParameters.Parse(p).name;
            var parsed = InputControlPath.Parse(p);
            if (parsed.Count > 0)
            {
                deviceLayoutName = parsed[0].layout;
                controlPath = InputControlPath.JoinControls(parsed, 1);
            }
            string s = InputControlPath.ToHumanReadableString(p, (options & DisplayStringOptions.DontOmitDevice) != 0 ? InputControlPath.HumanReadableStringOptions.None : InputControlPath.HumanReadableStringOptions.OmitDevice,
                control, (options & DisplayStringOptions.DontUseShortDisplayNames) == 0);
            if ((options & DisplayStringOptions.DontIncludeInteractions) == 0)
            {
                string inter = (options & DisplayStringOptions.IgnoreBindingOverrides) != 0 ? m_Interactions : effectiveInteractions;
                if (!string.IsNullOrEmpty(inter))
                {
                    var names = new List<string>();
                    foreach (var n in NameAndParameters.ParseMultiple(inter)) names.Add(n.name);
                    s = string.Join(" or ", names) + " " + s;
                }
            }
            return s;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(m_Action)) sb.Append(m_Action).Append(':');
            sb.Append(effectivePath);
            if (!string.IsNullOrEmpty(m_Groups)) sb.Append('[').Append(m_Groups).Append(']');
            return sb.ToString();
        }

        public bool Equals(InputBinding other) =>
            string.Equals(effectivePath, other.effectivePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(effectiveInteractions, other.effectiveInteractions, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(effectiveProcessors, other.effectiveProcessors, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m_Groups, other.m_Groups, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m_Action, other.m_Action, StringComparison.OrdinalIgnoreCase);
        public override bool Equals(object obj) => obj is InputBinding b && Equals(b);
        public override int GetHashCode() => (effectivePath ?? string.Empty).ToLowerInvariant().GetHashCode() ^ (m_Action ?? string.Empty).GetHashCode();
        public static bool operator ==(InputBinding a, InputBinding b) => a.Equals(b);
        public static bool operator !=(InputBinding a, InputBinding b) => !a.Equals(b);
    }

    /// <summary>Unity 의 InputControlScheme — 장치 묶음 ("Keyboard&amp;Mouse" = 자판 + 마우스, "Gamepad") 과 바인딩 그룹</summary>
    [Serializable]
    public struct InputControlScheme : IEquatable<InputControlScheme>
    {
        internal string m_Name;
        internal string m_BindingGroup;
        internal DeviceRequirement[] m_DeviceRequirements;

        public InputControlScheme(string name, IEnumerable<DeviceRequirement> devices = null, string bindingGroup = null)
        {
            m_Name = name;
            m_BindingGroup = string.IsNullOrEmpty(bindingGroup) ? name : bindingGroup;
            m_DeviceRequirements = devices != null ? new List<DeviceRequirement>(devices).ToArray() : Array.Empty<DeviceRequirement>();
        }

        public string name => m_Name;
        public string bindingGroup { get => m_BindingGroup; set => m_BindingGroup = value; }
        public ReadOnlyArray<DeviceRequirement> deviceRequirements => m_DeviceRequirements ?? Array.Empty<DeviceRequirement>();

        /// <summary>장치가 이 스킴의 요구 장치 중 하나에 맞는가</summary>
        public bool SupportsDevice(InputDevice device)
        {
            if (device == null) return false;
            foreach (var r in deviceRequirements)
                if (InputControlPath.MatchesDevice(r.controlPath, device)) return true;
            return false;
        }

        // 지금 장치들로 이 스킴을 쓸 수 있으면 쓸 장치 목록 (필수 장치가 없으면 null). favor 를 먼저
        internal List<InputDevice> PickDevices(IReadOnlyList<InputDevice> available, InputDevice favor = null, ICollection<InputDevice> exclude = null)
        {
            var picked = new List<InputDevice>();
            var reqs = deviceRequirements;
            for (int i = 0; i < reqs.Count; i++)
            {
                var r = reqs[i];
                InputDevice found = null;
                if (favor != null && (exclude == null || !exclude.Contains(favor)) && !picked.Contains(favor) && InputControlPath.MatchesDevice(r.controlPath, favor)) found = favor;
                if (found == null)
                    foreach (var d in available)
                    {
                        if (picked.Contains(d) || (exclude != null && exclude.Contains(d))) continue;
                        if (InputControlPath.MatchesDevice(r.controlPath, d)) { found = d; break; }
                    }
                if (found != null) picked.Add(found);
                else if (!r.isOptional && !(r.isOR && i > 0 && picked.Count > 0)) return null;
            }
            return picked;
        }

        public static InputControlScheme? FindControlSchemeForDevice<TSchemes>(InputDevice device, TSchemes schemes) where TSchemes : IEnumerable<InputControlScheme>
        {
            foreach (var s in schemes)
                if (s.SupportsDevice(device)) return s;
            return null;
        }

        public override string ToString()
        {
            var sb = new StringBuilder(m_Name ?? string.Empty);
            if (m_DeviceRequirements != null && m_DeviceRequirements.Length > 0)
            {
                sb.Append('(');
                for (int i = 0; i < m_DeviceRequirements.Length; i++)
                {
                    if (i > 0) sb.Append(m_DeviceRequirements[i].isOR ? " or " : ",");
                    sb.Append(m_DeviceRequirements[i].controlPath);
                    if (m_DeviceRequirements[i].isOptional) sb.Append(" (optional)");
                }
                sb.Append(')');
            }
            return sb.ToString();
        }

        public bool Equals(InputControlScheme other) => string.Equals(m_Name, other.m_Name, StringComparison.OrdinalIgnoreCase) && string.Equals(m_BindingGroup, other.m_BindingGroup, StringComparison.OrdinalIgnoreCase);
        public override bool Equals(object obj) => obj is InputControlScheme s && Equals(s);
        public override int GetHashCode() => (m_Name ?? string.Empty).ToLowerInvariant().GetHashCode();
        public static bool operator ==(InputControlScheme a, InputControlScheme b) => a.Equals(b);
        public static bool operator !=(InputControlScheme a, InputControlScheme b) => !a.Equals(b);

        [Serializable]
        public struct DeviceRequirement : IEquatable<DeviceRequirement>
        {
            internal string m_ControlPath;
            internal bool m_Optional, m_OR;
            public string controlPath { get => m_ControlPath; set => m_ControlPath = value; }
            public bool isOptional { get => m_Optional; set => m_Optional = value; }
            public bool isOR { get => m_OR; set => m_OR = value; }
            public bool isAND { get => !m_OR; set => m_OR = !value; }
            public override string ToString() => m_ControlPath + (m_Optional ? " (optional)" : "");
            public bool Equals(DeviceRequirement other) => string.Equals(m_ControlPath, other.m_ControlPath, StringComparison.OrdinalIgnoreCase) && m_Optional == other.m_Optional && m_OR == other.m_OR;
            public override bool Equals(object obj) => obj is DeviceRequirement r && Equals(r);
            public override int GetHashCode() => (m_ControlPath ?? string.Empty).GetHashCode();
        }
    }

    /// <summary>Unity 의 IInputActionCollection — 동작 묶음 (맵 · 에셋 · 만든 C# 클래스)</summary>
    public interface IInputActionCollection : IEnumerable<InputAction>
    {
        InputBinding? bindingMask { get; set; }
        ReadOnlyArray<InputDevice>? devices { get; set; }
        ReadOnlyArray<InputControlScheme> controlSchemes { get; }
        bool Contains(InputAction action);
        void Enable();
        void Disable();
    }

    public interface IInputActionCollection2 : IInputActionCollection
    {
        IEnumerable<InputBinding> bindings { get; }
        InputAction FindAction(string actionNameOrId, bool throwIfNotFound = false);
        int FindBinding(InputBinding mask, out InputAction action);
    }

    /// <summary>
    /// Unity 의 InputAction — 이름 있는 동작 ("Jump", "Move"). 바인딩으로 컨트롤에 묶고, Enable 한 뒤 ReadValue · WasPressedThisFrame 또는
    /// started · performed · canceled 콜백으로 받는다.
    /// </summary>
    [Serializable]
    public sealed partial class InputAction : ICloneable, IDisposable
    {
        internal string m_Name;
        internal InputActionType m_Type;
        internal string m_ExpectedControlType;
        internal string m_Id;
        internal string m_Processors;
        internal string m_Interactions;
        internal bool m_InitialStateCheck;
        internal InputActionMap m_ActionMap;   // 맵 없이 만든 동작은 숨은 맵 (m_IsSingleton)

        readonly List<Action<CallbackContext>> m_OnStarted = new List<Action<CallbackContext>>();
        readonly List<Action<CallbackContext>> m_OnPerformed = new List<Action<CallbackContext>>();
        readonly List<Action<CallbackContext>> m_OnCanceled = new List<Action<CallbackContext>>();

        public InputAction() : this(null) { }

        public InputAction(string name = null, InputActionType type = default, string binding = null, string interactions = null, string processors = null, string expectedControlType = null)
        {
            m_Name = name;
            m_Type = type;
            m_Interactions = interactions;
            m_Processors = processors;
            m_ExpectedControlType = expectedControlType;
            m_Id = Guid.NewGuid().ToString();
            if (binding != null)
                this.AddBinding(binding);
        }

        public string name => m_Name;
        public InputActionType type => m_Type;
        public Guid id { get { if (string.IsNullOrEmpty(m_Id)) m_Id = Guid.NewGuid().ToString(); return Guid.TryParse(m_Id, out var g) ? g : default; } }
        internal string idString { get { if (string.IsNullOrEmpty(m_Id)) m_Id = Guid.NewGuid().ToString(); return m_Id; } }
        public string expectedControlType { get => m_ExpectedControlType; set { m_ExpectedControlType = value; m_ActionMap?.MarkDirty(); } }
        public string processors => m_Processors;
        public string interactions => m_Interactions;
        /// <summary>맵 안의 동작이면 그 맵, 따로 만든 동작이면 null</summary>
        public InputActionMap actionMap => m_ActionMap != null && !m_ActionMap.m_IsSingleton ? m_ActionMap : null;
        /// <summary>Value 는 늘 켜짐: Enable 할 때 이미 움직인 컨트롤도 곧바로 반영</summary>
        public bool wantsInitialStateCheck
        {
            get => m_Type == InputActionType.Value || m_InitialStateCheck;
            set => m_InitialStateCheck = value;
        }

        public ReadOnlyArray<InputBinding> bindings
        {
            get
            {
                var map = m_ActionMap;
                if (map == null) return new ReadOnlyArray<InputBinding>(Array.Empty<InputBinding>());
                var idx = map.BindingIndicesOf(this);
                var arr = new InputBinding[idx.Count];
                for (int i = 0; i < idx.Count; i++) arr[i] = map.m_Bindings[idx[i]];
                return arr;
            }
        }

        public event Action<CallbackContext> started { add { if (value != null) m_OnStarted.Add(value); } remove => m_OnStarted.Remove(value); }
        public event Action<CallbackContext> performed { add { if (value != null) m_OnPerformed.Add(value); } remove => m_OnPerformed.Remove(value); }
        public event Action<CallbackContext> canceled { add { if (value != null) m_OnCanceled.Add(value); } remove => m_OnCanceled.Remove(value); }

        public InputAction Clone()
        {
            var c = new InputAction(m_Name, m_Type, null, m_Interactions, m_Processors, m_ExpectedControlType) { m_InitialStateCheck = m_InitialStateCheck };
            foreach (var b in bindings)
            {
                var nb = b;
                nb.m_Id = Guid.NewGuid().ToString();
                c.GetOrCreateActionMap().AppendBinding(nb);
            }
            return c;
        }
        object ICloneable.Clone() => Clone();

        public void Dispose()
        {
            if (m_ActionMap != null && m_ActionMap.m_IsSingleton) Disable();
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            if (actionMap != null) sb.Append(actionMap.name).Append('/');
            sb.Append(m_Name ?? "<Unnamed>");
            var cs = controls;
            if (cs.Count > 0)
            {
                sb.Append('[');
                for (int i = 0; i < cs.Count; i++) { if (i > 0) sb.Append(','); sb.Append(cs[i].path); }
                sb.Append(']');
            }
            return sb.ToString();
        }

        internal InputActionMap GetOrCreateActionMap()
        {
            if (m_ActionMap == null)
            {
                m_ActionMap = new InputActionMap(m_Name) { m_IsSingleton = true };
                m_ActionMap.m_Actions = new[] { this };
            }
            return m_ActionMap;
        }

        /// <summary>Unity 의 InputAction.CallbackContext — 콜백이 받는 값 · 컨트롤 · 단계</summary>
        public struct CallbackContext
        {
            internal InputAction m_Action;
            internal InputActionPhase m_Phase;
            internal InputControl m_Control;
            internal IInputInteraction m_Interaction;
            internal double m_Time, m_StartTime;
            internal InputValueData m_Value;

            public InputAction action => m_Action;
            public InputActionPhase phase => m_Phase;
            public bool started => m_Phase == InputActionPhase.Started;
            public bool performed => m_Phase == InputActionPhase.Performed;
            public bool canceled => m_Phase == InputActionPhase.Canceled;
            public InputControl control => m_Control;
            public IInputInteraction interaction => m_Interaction;
            public double time => m_Time;
            public double startTime => m_StartTime;
            public double duration => m_Time - m_StartTime;
            public Type valueType => m_Value.ValueType;
            public int valueSizeInBytes => m_Value.ValueSize;

            public TValue ReadValue<TValue>() where TValue : struct => ValueUtil.As<TValue>(m_Value, m_Action, m_Control);
            public object ReadValueAsObject() => m_Value.ToObject();
            public bool ReadValueAsButton() => m_Value.Magnitude() >= ValueUtil.PressPoint(m_Control);

            public override string ToString() => $"{{ action={m_Action} phase={m_Phase} time={m_Time:F3} control={m_Control} value={ReadValueAsObject()} interaction={m_Interaction} }}";
        }
    }

    /// <summary>Unity 의 InputActionMap — 동작 묶음 ("Player", "UI"). 함께 켜고 끈다</summary>
    [Serializable]
    public sealed partial class InputActionMap : ICloneable, IInputActionCollection2, IDisposable
    {
        internal string m_Name;
        internal string m_Id;
        internal InputActionAsset m_Asset;
        internal InputAction[] m_Actions = Array.Empty<InputAction>();
        internal InputBinding[] m_Bindings = Array.Empty<InputBinding>();
        internal InputBinding? m_BindingMask;
        internal ReadOnlyArray<InputDevice>? m_Devices;
        internal bool m_IsSingleton;

        readonly List<Action<InputAction.CallbackContext>> m_OnTriggered = new List<Action<InputAction.CallbackContext>>();

        public InputActionMap() : this(null) { }
        public InputActionMap(string name)
        {
            m_Name = name;
            m_Id = Guid.NewGuid().ToString();
        }

        public string name => m_Name;
        public Guid id { get { if (string.IsNullOrEmpty(m_Id)) m_Id = Guid.NewGuid().ToString(); return Guid.TryParse(m_Id, out var g) ? g : default; } }
        internal string idString { get { if (string.IsNullOrEmpty(m_Id)) m_Id = Guid.NewGuid().ToString(); return m_Id; } }
        public InputActionAsset asset => m_Asset;
        public ReadOnlyArray<InputAction> actions => m_Actions;
        public ReadOnlyArray<InputBinding> bindings => m_Bindings;
        IEnumerable<InputBinding> IInputActionCollection2.bindings => m_Bindings;
        public ReadOnlyArray<InputControlScheme> controlSchemes => m_Asset != null ? m_Asset.controlSchemes : new ReadOnlyArray<InputControlScheme>(Array.Empty<InputControlScheme>());
        public bool enabled { get { foreach (var a in m_Actions) if (a.enabled) return true; return false; } }

        public InputBinding? bindingMask { get => m_BindingMask; set { m_BindingMask = value; MarkDirty(); } }
        public ReadOnlyArray<InputDevice>? devices
        {
            get => m_Devices ?? m_Asset?.devices;
            set { m_Devices = value; MarkDirty(); }
        }

        public event Action<InputAction.CallbackContext> actionTriggered { add { if (value != null) m_OnTriggered.Add(value); } remove => m_OnTriggered.Remove(value); }
        internal List<Action<InputAction.CallbackContext>> triggeredListeners => m_OnTriggered;

        public InputAction this[string actionNameOrId]
        {
            get
            {
                var a = FindAction(actionNameOrId);
                if (a == null) throw new KeyNotFoundException($"Cannot find action '{actionNameOrId}' in '{this}'");
                return a;
            }
        }

        public InputAction FindAction(string actionNameOrId, bool throwIfNotFound = false)
        {
            if (actionNameOrId == null) throw new ArgumentNullException(nameof(actionNameOrId));
            string s = actionNameOrId.Trim();
            string guid = s.StartsWith("{") && s.EndsWith("}") ? s.Substring(1, s.Length - 2) : s;
            foreach (var a in m_Actions)
                if (string.Equals(a.m_Name, s, StringComparison.OrdinalIgnoreCase) || string.Equals(a.m_Id, guid, StringComparison.OrdinalIgnoreCase)) return a;
            if (throwIfNotFound) throw new ArgumentException($"No action '{actionNameOrId}' in '{this}'", nameof(actionNameOrId));
            return null;
        }

        public InputAction FindAction(Guid id)
        {
            foreach (var a in m_Actions)
                if (a.id == id) return a;
            return null;
        }

        public int FindBinding(InputBinding mask, out InputAction action)
        {
            for (int i = 0; i < m_Bindings.Length; i++)
                if (mask.Matches(ref m_Bindings[i]))
                {
                    action = FindActionForBinding(i);
                    return i;
                }
            action = null;
            return -1;
        }

        public bool Contains(InputAction action) => action != null && action.m_ActionMap == this;
        public bool IsUsableWithDevice(InputDevice device)
        {
            foreach (var b in m_Bindings)
                if (!b.isComposite && InputControlPath.TryFindControl(device, b.effectivePath) != null) return true;
            return false;
        }

        public void Enable()
        {
            foreach (var a in m_Actions) a.Enable();
        }

        public void Disable()
        {
            foreach (var a in m_Actions) a.Disable();
        }

        public InputActionMap Clone()
        {
            var m = new InputActionMap(m_Name) { m_Id = Guid.NewGuid().ToString(), m_BindingMask = m_BindingMask };
            var acts = new InputAction[m_Actions.Length];
            for (int i = 0; i < acts.Length; i++)
            {
                var a = m_Actions[i];
                acts[i] = new InputAction(a.m_Name, a.m_Type, null, a.m_Interactions, a.m_Processors, a.m_ExpectedControlType) { m_InitialStateCheck = a.m_InitialStateCheck, m_Id = a.m_Id, m_ActionMap = m };
            }
            m.m_Actions = acts;
            m.m_Bindings = (InputBinding[])m_Bindings.Clone();
            return m;
        }
        object ICloneable.Clone() => Clone();

        public void Dispose() => Disable();

        public IEnumerator<InputAction> GetEnumerator() => ((IEnumerable<InputAction>)m_Actions).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => m_Asset != null ? m_Asset.name + ":" + m_Name : (m_Name ?? "<Unnamed Action Map>");

        public string ToJson() => InputActionJson.MapsToJson(new[] { this });
        public static InputActionMap[] FromJson(string json) => InputActionJson.ParseMaps(json);

        // ---- 바인딩 · 동작 (내부)
        internal List<int> BindingIndicesOf(InputAction action)
        {
            var list = new List<int>();
            for (int i = 0; i < m_Bindings.Length; i++)
            {
                if (m_Bindings[i].isPartOfComposite)
                {
                    // 부분은 앞 합성의 동작을 따른다
                    int c = i - 1;
                    while (c >= 0 && m_Bindings[c].isPartOfComposite) c--;
                    if (c >= 0 && BindingTargets(c, action)) list.Add(i);
                    continue;
                }
                if (BindingTargets(i, action)) list.Add(i);
            }
            return list;
        }

        bool BindingTargets(int index, InputAction action)
        {
            if (m_IsSingleton) return true;
            string a = m_Bindings[index].m_Action;
            if (string.IsNullOrEmpty(a)) return false;
            if (a.StartsWith("{") && a.EndsWith("}")) a = a.Substring(1, a.Length - 2);
            int slash = a.IndexOf('/');
            if (slash >= 0) a = a.Substring(slash + 1);
            return string.Equals(a, action.m_Name, StringComparison.OrdinalIgnoreCase) || string.Equals(a, action.m_Id, StringComparison.OrdinalIgnoreCase);
        }

        internal InputAction FindActionForBinding(int index)
        {
            int c = index;
            while (c > 0 && m_Bindings[c].isPartOfComposite) c--;
            foreach (var a in m_Actions)
                if (BindingTargets(c, a)) return a;
            return null;
        }

        internal void AppendBinding(InputBinding b)
        {
            b.EnsureId();
            Array.Resize(ref m_Bindings, m_Bindings.Length + 1);
            m_Bindings[m_Bindings.Length - 1] = b;
            MarkDirty();
        }

        internal void InsertBinding(int index, InputBinding b)
        {
            b.EnsureId();
            var list = new List<InputBinding>(m_Bindings);
            list.Insert(Math.Clamp(index, 0, list.Count), b);
            m_Bindings = list.ToArray();
            MarkDirty();
        }
    }

    /// <summary>
    /// Unity 의 InputActionAsset (.inputactions) — 맵 · 동작 · 바인딩 · 컨트롤 스킴. Unity 와 같은 JSON 이라 Unity 프로젝트 파일을 그대로 읽는다.
    /// 같은 에셋을 가리키는 필드 · PlayerInput 은 같은 인스턴스를 받는다.
    /// </summary>
    public sealed class InputActionAsset : ScriptableObject, IInputActionCollection2
    {
        public const string Extension = "inputactions";

        internal InputActionMap[] m_ActionMaps = Array.Empty<InputActionMap>();
        internal InputControlScheme[] m_ControlSchemes = Array.Empty<InputControlScheme>();
        internal InputBinding? m_BindingMask;
        internal ReadOnlyArray<InputDevice>? m_Devices;

        public ReadOnlyArray<InputActionMap> actionMaps => m_ActionMaps;
        public ReadOnlyArray<InputControlScheme> controlSchemes => m_ControlSchemes;
        public bool enabled { get { foreach (var m in m_ActionMaps) if (m.enabled) return true; return false; } }
        public IEnumerable<InputBinding> bindings { get { foreach (var m in m_ActionMaps) foreach (var b in m.m_Bindings) yield return b; } }

        public InputBinding? bindingMask
        {
            get => m_BindingMask;
            set { m_BindingMask = value; foreach (var m in m_ActionMaps) m.MarkDirty(); }
        }

        public ReadOnlyArray<InputDevice>? devices
        {
            get => m_Devices;
            set { m_Devices = value; foreach (var m in m_ActionMaps) m.MarkDirty(); }
        }

        public InputAction this[string actionNameOrId]
        {
            get
            {
                var a = FindAction(actionNameOrId);
                if (a == null) throw new KeyNotFoundException($"Cannot find action '{actionNameOrId}' in '{this}'");
                return a;
            }
        }

        /// <summary>"Move" 또는 "Player/Move" 또는 id</summary>
        public InputAction FindAction(string actionNameOrId, bool throwIfNotFound = false)
        {
            if (actionNameOrId == null) throw new ArgumentNullException(nameof(actionNameOrId));
            int slash = actionNameOrId.IndexOf('/');
            if (slash > 0)
            {
                var map = FindActionMap(actionNameOrId.Substring(0, slash));
                var a = map?.FindAction(actionNameOrId.Substring(slash + 1));
                if (a != null) return a;
            }
            else
                foreach (var m in m_ActionMaps)
                {
                    var a = m.FindAction(actionNameOrId);
                    if (a != null) return a;
                }
            if (throwIfNotFound) throw new ArgumentException($"No action '{actionNameOrId}' in '{this}'", nameof(actionNameOrId));
            return null;
        }

        public InputAction FindAction(Guid id)
        {
            foreach (var m in m_ActionMaps)
            {
                var a = m.FindAction(id);
                if (a != null) return a;
            }
            return null;
        }

        public InputActionMap FindActionMap(string nameOrId, bool throwIfNotFound = false)
        {
            if (nameOrId == null) throw new ArgumentNullException(nameof(nameOrId));
            string s = nameOrId.Trim();
            string guid = s.StartsWith("{") && s.EndsWith("}") ? s.Substring(1, s.Length - 2) : s;
            foreach (var m in m_ActionMaps)
                if (string.Equals(m.m_Name, s, StringComparison.OrdinalIgnoreCase) || string.Equals(m.m_Id, guid, StringComparison.OrdinalIgnoreCase)) return m;
            if (throwIfNotFound) throw new ArgumentException($"Cannot find action map '{nameOrId}' in '{this}'", nameof(nameOrId));
            return null;
        }

        public InputActionMap FindActionMap(Guid id)
        {
            foreach (var m in m_ActionMaps)
                if (m.id == id) return m;
            return null;
        }

        public int FindBinding(InputBinding mask, out InputAction action)
        {
            int offset = 0;
            foreach (var m in m_ActionMaps)
            {
                int i = m.FindBinding(mask, out action);
                if (i >= 0) return offset + i;
                offset += m.m_Bindings.Length;
            }
            action = null;
            return -1;
        }

        public InputControlScheme? FindControlScheme(string name)
        {
            int i = FindControlSchemeIndex(name);
            return i >= 0 ? m_ControlSchemes[i] : (InputControlScheme?)null;
        }

        public int FindControlSchemeIndex(string name)
        {
            for (int i = 0; i < m_ControlSchemes.Length; i++)
                if (string.Equals(m_ControlSchemes[i].name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public bool IsUsableWithDevice(InputDevice device)
        {
            foreach (var s in m_ControlSchemes)
                if (s.SupportsDevice(device)) return true;
            foreach (var m in m_ActionMaps)
                if (m.IsUsableWithDevice(device)) return true;
            return false;
        }

        public bool Contains(InputAction action) => action?.m_ActionMap != null && action.m_ActionMap.m_Asset == this;

        public void Enable()
        {
            foreach (var m in m_ActionMaps) m.Enable();
        }

        public void Disable()
        {
            foreach (var m in m_ActionMaps) m.Disable();
        }

        public IEnumerator<InputAction> GetEnumerator()
        {
            foreach (var m in m_ActionMaps)
                foreach (var a in m.m_Actions) yield return a;
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public string ToJson() => InputActionJson.AssetToJson(this);

        public void LoadFromJson(string json)
        {
            Disable();
            InputActionJson.ParseAsset(json, this);
        }

        public static InputActionAsset FromJson(string json)
        {
            var a = CreateInstance<InputActionAsset>();
            InputActionJson.ParseAsset(json, a);
            return a;
        }

        public override string ToString() => string.IsNullOrEmpty(name) ? "<Unnamed InputActionAsset>" : name;

        // 에셋 파일 경로 (Assets/...inputactions), 만든 것은 null
        internal string assetPath => m_AssetPath;

        // 복제 (PlayerInput 둘째 플레이어부터 — Unity 와 같이 에셋을 복사해 장치를 따로 묶는다)
        internal InputActionAsset CloneAsset()
        {
            var c = CreateInstance<InputActionAsset>();
            c.name = name;
            c.m_ControlSchemes = (InputControlScheme[])m_ControlSchemes.Clone();
            var maps = new InputActionMap[m_ActionMaps.Length];
            for (int i = 0; i < maps.Length; i++)
            {
                maps[i] = m_ActionMaps[i].Clone();
                maps[i].m_Id = m_ActionMaps[i].m_Id;
                maps[i].m_Asset = c;
            }
            c.m_ActionMaps = maps;
            return c;
        }
    }

    /// <summary>Unity 의 InputActionReference — 에셋 안 동작 하나를 가리킨다 (Inspector 필드)</summary>
    public class InputActionReference : ScriptableObject
    {
        internal InputActionAsset m_Asset;
        internal string m_ActionId;
        InputAction m_Action;

        public InputActionAsset asset => m_Asset;
        public InputAction action
        {
            get
            {
                if (m_Action == null && m_Asset != null && !string.IsNullOrEmpty(m_ActionId))
                    m_Action = m_Asset.FindAction(m_ActionId);
                return m_Action;
            }
        }

        public void Set(InputAction action)
        {
            m_Action = action;
            m_Asset = action?.m_ActionMap?.m_Asset;
            m_ActionId = action?.idString;
            name = action != null ? (action.actionMap != null ? action.actionMap.name + "/" : "") + action.name : string.Empty;
        }

        public void Set(InputActionAsset asset, string mapName, string actionName)
        {
            var map = asset?.FindActionMap(mapName, true);
            Set(map?.FindAction(actionName, true));
        }

        public static InputActionReference Create(InputAction action)
        {
            var r = CreateInstance<InputActionReference>();
            r.Set(action);
            return r;
        }

        public InputAction ToInputAction() => action;
        public static implicit operator InputAction(InputActionReference reference) => reference?.action;
        public override string ToString() => m_Asset != null ? m_Asset.name + ":" + name : base.ToString();
    }

    /// <summary>Unity 의 InputActionProperty — 동작 참조 또는 직접 만든 동작</summary>
    [Serializable]
    public struct InputActionProperty : IEquatable<InputActionProperty>
    {
        internal bool m_UseReference;
        internal InputAction m_Action;
        internal InputActionReference m_Reference;

        public InputActionProperty(InputAction action) { m_UseReference = false; m_Action = action; m_Reference = null; }
        public InputActionProperty(InputActionReference reference) { m_UseReference = true; m_Action = null; m_Reference = reference; }

        public InputAction action => m_UseReference ? m_Reference?.action : m_Action;
        public InputActionReference reference => m_UseReference ? m_Reference : null;

        public bool Equals(InputActionProperty other) => ReferenceEquals(action, other.action);
        public override bool Equals(object obj) => obj is InputActionProperty p && Equals(p);
        public override int GetHashCode() => action?.GetHashCode() ?? 0;
    }
}
