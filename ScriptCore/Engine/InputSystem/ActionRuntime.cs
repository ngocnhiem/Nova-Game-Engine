using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NovaEngine.InputSystem.Controls;
using NovaEngine.InputSystem.LowLevel;
using NovaEngine.InputSystem.Utilities;

namespace NovaEngine.InputSystem
{
    // 동작 값 (상자 없이): float · Vector2 · Vector3 · Quaternion · int · double · TouchState
    internal enum ValueKind : byte { None, Float, Vector2, Vector3, Quaternion, Int, Double, Touch, Object }

    internal struct InputValueData
    {
        public ValueKind kind;
        public float x, y, z, w;
        public double d;
        public TouchState touch;
        public object obj;

        public static InputValueData Float(float v) => new InputValueData { kind = ValueKind.Float, x = v };

        public static InputValueData From<T>(T value) where T : struct
        {
            var r = new InputValueData();
            if (typeof(T) == typeof(float)) { r.kind = ValueKind.Float; r.x = Unsafe.As<T, float>(ref value); }
            else if (typeof(T) == typeof(Vector2)) { var v = Unsafe.As<T, Vector2>(ref value); r.kind = ValueKind.Vector2; r.x = v.x; r.y = v.y; }
            else if (typeof(T) == typeof(Vector3)) { var v = Unsafe.As<T, Vector3>(ref value); r.kind = ValueKind.Vector3; r.x = v.x; r.y = v.y; r.z = v.z; }
            else if (typeof(T) == typeof(Quaternion)) { var v = Unsafe.As<T, Quaternion>(ref value); r.kind = ValueKind.Quaternion; r.x = v.x; r.y = v.y; r.z = v.z; r.w = v.w; }
            else if (typeof(T) == typeof(int)) { r.kind = ValueKind.Int; r.x = Unsafe.As<T, int>(ref value); }
            else if (typeof(T) == typeof(double)) { r.kind = ValueKind.Double; r.d = Unsafe.As<T, double>(ref value); }
            else if (typeof(T) == typeof(TouchState)) { r.kind = ValueKind.Touch; r.touch = Unsafe.As<T, TouchState>(ref value); }
            else if (typeof(T) == typeof(TouchPhase)) { r.kind = ValueKind.Int; r.x = (int)Unsafe.As<T, TouchPhase>(ref value); }
            else { r.kind = ValueKind.Object; r.obj = value; }
            return r;
        }

        // 같은 종류의 0 (canceled 콜백 값)
        public InputValueData Zero() => new InputValueData { kind = kind };

        public void Clear()
        {
            x = y = z = w = 0f;
            d = 0;
            touch = default;
            obj = null;
        }

        public float Magnitude()
        {
            switch (kind)
            {
                case ValueKind.Float: return Math.Abs(x);
                case ValueKind.Vector2: return MathF.Sqrt(x * x + y * y);
                case ValueKind.Vector3: return MathF.Sqrt(x * x + y * y + z * z);
                case ValueKind.Int: return x != 0 ? 1f : 0f;
                case ValueKind.Double: return (float)Math.Abs(d);
                case ValueKind.Touch: return touch.isInProgress ? 1f : 0f;
                case ValueKind.Quaternion: return 1f;
                case ValueKind.Object: return obj != null ? 1f : 0f;
            }
            return 0f;
        }

        public bool SameAs(in InputValueData o)
        {
            if (kind != o.kind || x != o.x || y != o.y || z != o.z || w != o.w || d != o.d) return false;
            if (kind == ValueKind.Touch) return touch.position == o.touch.position && touch.phase == o.touch.phase && touch.delta == o.touch.delta && touch.touchId == o.touch.touchId;
            if (kind == ValueKind.Object) return Equals(obj, o.obj);
            return true;
        }

        public object ToObject()
        {
            switch (kind)
            {
                case ValueKind.Float: return x;
                case ValueKind.Vector2: return new Vector2(x, y);
                case ValueKind.Vector3: return new Vector3(x, y, z);
                case ValueKind.Quaternion: return new Quaternion(x, y, z, w);
                case ValueKind.Int: return (int)x;
                case ValueKind.Double: return d;
                case ValueKind.Touch: return touch;
                case ValueKind.Object: return obj;
            }
            return null;
        }

        public Type ValueType
        {
            get
            {
                switch (kind)
                {
                    case ValueKind.Float: return typeof(float);
                    case ValueKind.Vector2: return typeof(Vector2);
                    case ValueKind.Vector3: return typeof(Vector3);
                    case ValueKind.Quaternion: return typeof(Quaternion);
                    case ValueKind.Int: return typeof(int);
                    case ValueKind.Double: return typeof(double);
                    case ValueKind.Touch: return typeof(TouchState);
                    case ValueKind.Object: return obj?.GetType();
                }
                return null;
            }
        }

        public int ValueSize
        {
            get
            {
                switch (kind)
                {
                    case ValueKind.Float: case ValueKind.Int: return 4;
                    case ValueKind.Vector2: case ValueKind.Double: return 8;
                    case ValueKind.Vector3: return 12;
                    case ValueKind.Quaternion: return 16;
                    case ValueKind.Touch: return Unsafe.SizeOf<TouchState>();
                }
                return 0;
            }
        }

        public string TypeName => ValueType?.Name ?? "none";
    }

    internal static class ValueUtil
    {
        // 컨트롤의 값 (레이아웃 프로세서를 거친)
        internal static InputValueData Read(InputControl c)
        {
            switch (c)
            {
                case InputControl<float> f: return InputValueData.Float(f.ReadValue());
                case InputControl<Vector2> v2: { var v = v2.ReadValue(); return new InputValueData { kind = ValueKind.Vector2, x = v.x, y = v.y }; }
                case InputControl<int> i: return new InputValueData { kind = ValueKind.Int, x = i.ReadValue() };
                case InputControl<double> dd: return new InputValueData { kind = ValueKind.Double, d = dd.ReadValue() };
                case InputControl<TouchState> t: return new InputValueData { kind = ValueKind.Touch, touch = t.ReadValue() };
                case InputControl<TouchPhase> p: return new InputValueData { kind = ValueKind.Int, x = (int)p.ReadValue() };
                case InputControl<Vector3> v3: { var v = v3.ReadValue(); return new InputValueData { kind = ValueKind.Vector3, x = v.x, y = v.y, z = v.z }; }
                case InputControl<Quaternion> q: { var v = q.ReadValue(); return new InputValueData { kind = ValueKind.Quaternion, x = v.x, y = v.y, z = v.z, w = v.w }; }
            }
            return new InputValueData { kind = ValueKind.Object, obj = c.ReadValueAsObject() };
        }

        internal static TValue As<TValue>(in InputValueData d, InputAction action, InputControl control) where TValue : struct
        {
            if (d.kind == ValueKind.None) return default;
            Type t = typeof(TValue);
            if (t == typeof(float) && d.kind == ValueKind.Float) { float v = d.x; return Unsafe.As<float, TValue>(ref v); }
            if (t == typeof(Vector2) && d.kind == ValueKind.Vector2) { var v = new Vector2(d.x, d.y); return Unsafe.As<Vector2, TValue>(ref v); }
            if (t == typeof(Vector3) && d.kind == ValueKind.Vector3) { var v = new Vector3(d.x, d.y, d.z); return Unsafe.As<Vector3, TValue>(ref v); }
            if (t == typeof(Quaternion) && d.kind == ValueKind.Quaternion) { var v = new Quaternion(d.x, d.y, d.z, d.w); return Unsafe.As<Quaternion, TValue>(ref v); }
            if (t == typeof(int) && d.kind == ValueKind.Int) { int v = (int)d.x; return Unsafe.As<int, TValue>(ref v); }
            if (t == typeof(double) && d.kind == ValueKind.Double) { double v = d.d; return Unsafe.As<double, TValue>(ref v); }
            if (t == typeof(double) && d.kind == ValueKind.Float) { double v = d.x; return Unsafe.As<double, TValue>(ref v); }
            if (t == typeof(TouchState) && d.kind == ValueKind.Touch) { var v = d.touch; return Unsafe.As<TouchState, TValue>(ref v); }
            if (t == typeof(TouchPhase) && d.kind == ValueKind.Int) { var v = (TouchPhase)(int)d.x; return Unsafe.As<TouchPhase, TValue>(ref v); }
            if (d.kind == ValueKind.Object && d.obj is TValue tv) return tv;
            string where = action != null ? $" bound to action '{action}'" : string.Empty;
            string ctl = control != null ? $"control '{control.path}'" : "composite";
            throw new InvalidOperationException($"Cannot read value of type '{t.Name}' from {ctl}{where} (value type is '{d.TypeName}')");
        }

        internal static float PressPoint(InputControl c) => c is ButtonControl b ? b.pressPointOrDefault : InputSystem.settings.defaultButtonPressPoint;

        internal static void ApplyProcessors(InputProcessor[] ps, ref InputValueData d, InputControl c)
        {
            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                switch (p)
                {
                    case InputProcessor<float> pf when d.kind == ValueKind.Float: d.x = pf.Process(d.x, c); break;
                    case InputProcessor<Vector2> pv when d.kind == ValueKind.Vector2: { var v = pv.Process(new Vector2(d.x, d.y), c); d.x = v.x; d.y = v.y; break; }
                    case InputProcessor<Vector3> p3 when d.kind == ValueKind.Vector3: { var v = p3.Process(new Vector3(d.x, d.y, d.z), c); d.x = v.x; d.y = v.y; d.z = v.z; break; }
                    case InputProcessor<Quaternion> pq when d.kind == ValueKind.Quaternion: { var v = pq.Process(new Quaternion(d.x, d.y, d.z, d.w), c); d.x = v.x; d.y = v.y; d.z = v.z; d.w = v.w; break; }
                    case InputProcessor<double> pd when d.kind == ValueKind.Double: d.d = pd.Process(d.d, c); break;
                    case InputProcessor<int> pi when d.kind == ValueKind.Int: d.x = pi.Process((int)d.x, c); break;
                }
            }
        }

        // expectedControlType → 값 종류 (None = 아무거나)
        internal static ValueKind KindForControlType(string controlType)
        {
            if (string.IsNullOrEmpty(controlType)) return ValueKind.None;
            switch (controlType.ToLowerInvariant())
            {
                case "button": case "axis": case "key": case "analog": case "digital": case "discretebutton": case "anykey": return ValueKind.Float;
                case "vector2": case "stick": case "dpad": case "delta": return ValueKind.Vector2;
                case "vector3": return ValueKind.Vector3;
                case "quaternion": return ValueKind.Quaternion;
                case "integer": return ValueKind.Int;
                case "double": return ValueKind.Double;
                case "touch": return ValueKind.Touch;
            }
            return ValueKind.None;
        }

        internal static ValueKind KindOfControl(InputControl c)
        {
            Type t = c.valueType;
            if (t == typeof(float)) return ValueKind.Float;
            if (t == typeof(Vector2)) return ValueKind.Vector2;
            if (t == typeof(Vector3)) return ValueKind.Vector3;
            if (t == typeof(Quaternion)) return ValueKind.Quaternion;
            if (t == typeof(int) || t == typeof(TouchPhase)) return ValueKind.Int;
            if (t == typeof(double)) return ValueKind.Double;
            if (t == typeof(TouchState)) return ValueKind.Touch;
            return ValueKind.Object;
        }
    }

    internal struct InteractionState
    {
        public InputActionPhase phase;
        public double startTime;
        public double timeout;      // < 0 = 없음
        public float totalTimeout;
    }

    // 바인딩 하나의 실행 상태 (맵이 Resolve 에서 만든다)
    internal sealed class BindingState
    {
        public InputActionMap map;
        public int bindingIndex = -1;          // -1 = 기본 묶음 (상호작용 없는 바인딩들)
        public InputAction action;
        public bool isComposite, isPart, isDefaultGroup;
        public BindingState composite;
        public readonly List<BindingState> parts = new List<BindingState>();
        public readonly List<BindingState> members = new List<BindingState>();   // 기본 묶음
        public int partNumber = -1;
        public InputBindingComposite compositeObject;
        public InputControl[] controls = Array.Empty<InputControl>();
        public InputProcessor[] processors = Array.Empty<InputProcessor>();
        public IInputInteraction[] interactions = Array.Empty<IInputInteraction>();
        public InteractionState[] states = Array.Empty<InteractionState>();
        public bool[] ignore;                         // 켤 때 이미 눌린 컨트롤 (initial state check 가 없으면 바뀔 때까지 무시)
        public InputValueData[] ignoreSnapshot;
        public InputValueData[] lastControlValue;     // PassThrough: 컨트롤마다 마지막 값

        // 이번 프레임 / 지난 프레임
        public InputValueData value, prevValue;
        public float magnitude, prevMagnitude;
        public InputControl control, prevControl;
        public double pressTime = -1;                 // 부분: 처음 눌린 시각 (수정 키 순서)

        public InputValueData ReadControl(int i)
        {
            var d = ValueUtil.Read(controls[i]);
            if (processors.Length > 0) ValueUtil.ApplyProcessors(processors, ref d, controls[i]);
            return d;
        }

        public bool IsIgnored(int i) => ignore != null && ignore[i];

        public bool Changed => !value.SameAs(prevValue) || magnitude != prevMagnitude || !ReferenceEquals(control, prevControl);

        public void ResetInteractions()
        {
            for (int i = 0; i < states.Length; i++)
            {
                states[i] = new InteractionState { phase = InputActionPhase.Waiting, timeout = -1 };
                interactions[i].Reset();
            }
        }

        public void Snapshot(bool ignoreActuated)
        {
            ignore = ignoreActuated && controls.Length > 0 ? new bool[controls.Length] : null;
            ignoreSnapshot = ignore != null ? new InputValueData[controls.Length] : null;
            lastControlValue = new InputValueData[controls.Length];
            for (int i = 0; i < controls.Length; i++)
            {
                var d = ValueUtil.Read(controls[i]);
                lastControlValue[i] = d;
                if (ignore != null && d.Magnitude() > 0f)
                {
                    ignore[i] = true;
                    ignoreSnapshot[i] = d;
                }
            }
        }
    }

    public sealed partial class InputActionMap
    {
        internal bool m_Resolved;
        internal int m_ResolvedDeviceVersion = -1;
        internal bool m_NoIgnoreOnce;   // 스킴 전환 직후: 전환을 일으킨 누름도 동작에 (무시하지 않는다)
        internal BindingState[] m_States = Array.Empty<BindingState>();
        internal readonly List<InputControl> m_ResolvedControls = new List<InputControl>();

        internal void MarkDirty()
        {
            m_Resolved = false;
        }

        internal void EnsureResolved()
        {
            if (m_Resolved && m_ResolvedDeviceVersion == InputSystem.s_DeviceVersion) return;
            Resolve();
        }

        bool PassesMasks(ref InputBinding b)
        {
            var m1 = m_Asset?.m_BindingMask;
            if (m1.HasValue && !m1.Value.Matches(ref b)) return false;
            if (m_BindingMask.HasValue && !m_BindingMask.Value.Matches(ref b)) return false;
            return true;
        }

        IReadOnlyList<InputDevice> DevicePool()
        {
            var restrict = m_Devices ?? m_Asset?.m_Devices;
            if (!restrict.HasValue) return InputSystem.s_Devices;
            var list = new List<InputDevice>();
            foreach (var d in restrict.Value)
                if (d != null && d.m_Added) list.Add(d);
            return list;
        }

        internal void Resolve()
        {
            InputSystem.EnsureDevices();
            // 진행 중인 동작은 취소 (스킴 · 장치 · 바인딩이 바뀜)
            foreach (var a in m_Actions)
                if (a.m_Enabled && (a.m_Phase == InputActionPhase.Started || a.m_Phase == InputActionPhase.Performed))
                    a.CancelForReresolve();

            m_Resolved = true;
            m_ResolvedDeviceVersion = InputSystem.s_DeviceVersion;
            m_ResolvedControls.Clear();
            var pool = DevicePool();
            var states = new BindingState[m_Bindings.Length];
            foreach (var a in m_Actions) a.ClearResolved();

            BindingState composite = null;
            var tmp = new List<InputControl>();
            for (int i = 0; i < m_Bindings.Length; i++)
            {
                ref InputBinding b = ref m_Bindings[i];
                if (b.isPartOfComposite)
                {
                    if (composite == null) continue;
                    var ps = new BindingState { map = this, bindingIndex = i, action = composite.action, isPart = true, composite = composite };
                    ps.partNumber = composite.compositeObject.AssignPartNumber(b.name);
                    if (ps.partNumber < 0)
                    {
                        InputSystem.WarnOnce($"Binding part '{b.name}' is not a part of composite '{m_Bindings[composite.bindingIndex].effectivePath}' (action '{composite.action}')");
                        continue;
                    }
                    if (PassesMasks(ref b))
                    {
                        tmp.Clear();
                        InputControlPath.FindControls(b.effectivePath, pool, tmp);
                        ps.controls = tmp.ToArray();
                    }
                    ps.processors = InputSystem.ParseProcessors(b.effectiveProcessors);
                    composite.parts.Add(ps);
                    states[i] = ps;
                    continue;
                }
                composite = null;
                var action = FindActionForBinding(i);
                if (action == null) continue;
                var st = new BindingState { map = this, bindingIndex = i, action = action };
                if (b.isComposite)
                {
                    var np = NameAndParameters.Parse(b.effectivePath);
                    var obj = InputSystem.CreateComposite(np.name);
                    if (obj == null)
                    {
                        InputSystem.WarnOnce($"No composite called '{np.name}' (action '{action}')");
                        continue;
                    }
                    ParamUtil.Apply(obj, np.parameters);
                    st.isComposite = true;
                    st.compositeObject = obj;
                    composite = st;
                }
                else if (PassesMasks(ref b))
                {
                    tmp.Clear();
                    InputControlPath.FindControls(b.effectivePath, pool, tmp);
                    // 동작이 바라는 값 형식과 다른 컨트롤은 묶지 않는다 (Vector2 동작에 키 하나 …)
                    var want = ValueUtil.KindForControlType(action.m_ExpectedControlType);
                    if (want != ValueKind.None) tmp.RemoveAll(c => ValueUtil.KindOfControl(c) != want);
                    st.controls = tmp.ToArray();
                }
                var procs = new List<InputProcessor>(InputSystem.ParseProcessors(b.effectiveProcessors));
                procs.AddRange(InputSystem.ParseProcessors(action.m_Processors));
                st.processors = procs.ToArray();
                var inters = new List<IInputInteraction>(InputSystem.ParseInteractions(b.effectiveInteractions));
                inters.AddRange(InputSystem.ParseInteractions(action.m_Interactions));
                st.interactions = inters.ToArray();
                st.states = new InteractionState[st.interactions.Length];
                st.ResetInteractions();
                states[i] = st;
            }

            // 합성: 부분이 하나라도 마스크를 지나야 쓴다
            for (int i = 0; i < states.Length; i++)
            {
                var st = states[i];
                if (st == null || st.isPart) continue;
                if (st.isComposite)
                {
                    bool any = false;
                    foreach (var p in st.parts)
                        any |= PassesMasks(ref m_Bindings[p.bindingIndex]);
                    if (!any) { states[i] = null; foreach (var p in st.parts) states[p.bindingIndex] = null; continue; }
                    var want = ValueUtil.KindForControlType(st.action.m_ExpectedControlType);
                    var got = KindOfComposite(st.compositeObject);
                    if (want != ValueKind.None && got != ValueKind.None && want != got) { states[i] = null; foreach (var p in st.parts) states[p.bindingIndex] = null; continue; }
                }
                else if (!PassesMasks(ref m_Bindings[i])) { states[i] = null; continue; }
                st.action.AddResolved(st);
            }
            m_States = states;
            foreach (var st in states)
                if (st != null)
                    foreach (var c in st.controls)
                        if (!m_ResolvedControls.Contains(c)) m_ResolvedControls.Add(c);
            foreach (var a in m_Actions) a.FinishResolve();
            m_NoIgnoreOnce = false;
        }

        static ValueKind KindOfComposite(InputBindingComposite c)
        {
            if (c is Composites.OneModifierComposite || c is Composites.TwoModifiersComposite) return ValueKind.None;
            Type t = c.valueType;
            if (t == typeof(float)) return ValueKind.Float;
            if (t == typeof(Vector2)) return ValueKind.Vector2;
            if (t == typeof(Vector3)) return ValueKind.Vector3;
            return ValueKind.None;
        }

        // 프레임마다 (InputSystem.Update): 바인딩 값 → 동작
        internal void UpdateFrame(double now)
        {
            EnsureResolved();
            var states = m_States;
            for (int i = 0; i < states.Length; i++)
            {
                var st = states[i];
                if (st == null || !st.action.m_Enabled || st.isComposite) continue;
                ComputeControls(st, now);
            }
            for (int i = 0; i < states.Length; i++)
            {
                var st = states[i];
                if (st == null || !st.action.m_Enabled || !st.isComposite) continue;
                ComputeComposite(st);
            }
            var acts = m_Actions;
            for (int i = 0; i < acts.Length; i++)
                if (acts[i].m_Enabled) acts[i].UpdateFrame(now);
        }

        static void ComputeControls(BindingState st, double now)
        {
            st.prevValue = st.value;
            st.prevMagnitude = st.magnitude;
            st.prevControl = st.control;
            float best = -1f;
            InputValueData bestValue = default;
            InputControl bestControl = null;
            var cs = st.controls;
            for (int i = 0; i < cs.Length; i++)
            {
                var d = st.ReadControl(i);
                if (st.ignore != null && st.ignore[i])
                {
                    if (!d.SameAs(st.ignoreSnapshot[i])) st.ignore[i] = false;
                    else continue;
                }
                float m = d.Magnitude();
                if (m > best || (m == best && ReferenceEquals(cs[i], st.prevControl)))
                {
                    best = m;
                    bestValue = d;
                    bestControl = cs[i];
                }
            }
            if (bestControl == null)
            {
                st.value = default;
                st.magnitude = 0f;
                st.control = null;
            }
            else
            {
                st.value = bestValue;
                st.magnitude = best;
                st.control = bestControl;
            }
            if (st.isPart)
            {
                bool down = st.control != null && st.magnitude >= ValueUtil.PressPoint(st.control);
                if (down && st.pressTime < 0) st.pressTime = now;
                else if (!down) st.pressTime = -1;
            }
        }

        static void ComputeComposite(BindingState st)
        {
            st.prevValue = st.value;
            st.prevMagnitude = st.magnitude;
            st.prevControl = st.control;
            var ctx = new InputBindingCompositeContext { m_Composite = st };
            var v = st.compositeObject.ReadValueData(ref ctx);
            if (st.processors.Length > 0) ValueUtil.ApplyProcessors(st.processors, ref v, null);
            float m = st.compositeObject.EvaluateMagnitude(ref ctx);
            if (m < 0f) m = v.Magnitude();
            st.value = v;
            st.magnitude = m;
            InputControl best = null;
            float bm = 0f;
            foreach (var p in st.parts)
                if (p.control != null && p.magnitude > bm) { bm = p.magnitude; best = p.control; }
            st.control = m > 0f ? (best ?? st.prevControl) : null;
        }
    }

    public sealed partial class InputAction
    {
        internal bool m_Enabled;
        internal InputActionPhase m_Phase = InputActionPhase.Disabled;
        internal readonly List<BindingState> m_Resolved = new List<BindingState>();   // 맨 위 바인딩 (합성 포함)
        internal readonly List<BindingState> m_WithInteractions = new List<BindingState>();
        internal BindingState m_Default;                                            // 상호작용 없는 바인딩들
        internal BindingState m_Driver;
        internal int m_DriverIndex;
        internal InputValueData m_Value;
        internal InputControl m_ActiveControl;
        internal double m_StartTime, m_LastTriggerTime;
        internal bool m_NeedsInitialCheck;
        internal bool m_Pressed;
        internal int m_PressedFrame = -1, m_ReleasedFrame = -1, m_PerformedFrame = -1, m_StartedFrame = -1, m_CanceledFrame = -1, m_CompletedFrame = -1;
        internal int m_StartedCount, m_PerformedCount, m_CanceledCount;   // nova input action (검사 · 진단)
        readonly List<InputControl> m_Controls = new List<InputControl>();

        public bool enabled => m_Enabled;
        public InputActionPhase phase => m_Phase;
        public bool inProgress => IsInProgress();
        /// <summary>이번 프레임에 performed 가 있었나 (Unity 의 triggered = WasPerformedThisFrame)</summary>
        public bool triggered => WasPerformedThisFrame();
        public InputControl activeControl => m_Phase == InputActionPhase.Started || m_Phase == InputActionPhase.Performed ? m_ActiveControl : null;
        public Type activeValueType => activeControl != null ? m_Value.ValueType : null;

        /// <summary>바인딩이 가리키는 컨트롤 (지금 있는 장치에서)</summary>
        public ReadOnlyArray<InputControl> controls
        {
            get
            {
                GetOrCreateActionMap().EnsureResolved();
                return m_Controls.ToArray();
            }
        }

        public void Enable()
        {
            if (m_Enabled) return;
            var map = GetOrCreateActionMap();
            map.EnsureResolved();
            m_Enabled = true;
            m_Phase = InputActionPhase.Waiting;
            m_Driver = null;
            m_Pressed = false;
            m_Value = default;
            m_ActiveControl = null;
            PrepareForEnable();
            InputSystem.OnMapEnabled(map);
            InputSystem.NotifyActionChange(this, InputActionChange.ActionEnabled);
        }

        public void Disable()
        {
            if (!m_Enabled) return;
            if (m_Phase == InputActionPhase.Started || m_Phase == InputActionPhase.Performed)
            {
                var src = m_Driver;
                m_Phase = InputActionPhase.Canceled;
                Fire(InputActionPhase.Canceled, src?.control ?? m_ActiveControl, src != null && m_DriverIndex < src.interactions.Length ? src.interactions[m_DriverIndex] : null, m_Value.Zero());
            }
            m_Enabled = false;
            m_Phase = InputActionPhase.Disabled;
            m_Driver = null;
            m_Pressed = false;
            m_Value = default;
            m_ActiveControl = null;
            foreach (var b in m_Resolved) b.ResetInteractions();
            m_Default?.ResetInteractions();
            InputSystem.OnMapMaybeDisabled(m_ActionMap);
            InputSystem.NotifyActionChange(this, InputActionChange.ActionDisabled);
        }

        /// <summary>진행 중이면 끝내고 처음 상태로 (Unity 의 Reset)</summary>
        public void Reset()
        {
            if (!m_Enabled) return;
            Disable();
            Enable();
        }

        public bool IsPressed() => m_Enabled && m_Pressed;
        public bool IsInProgress() => m_Phase == InputActionPhase.Started || m_Phase == InputActionPhase.Performed;
        public bool WasPressedThisFrame() => m_Enabled && m_PressedFrame == InputSystem.s_UpdateCount;
        public bool WasReleasedThisFrame() => m_Enabled && m_ReleasedFrame == InputSystem.s_UpdateCount;
        public bool WasPerformedThisFrame() => m_Enabled && m_PerformedFrame == InputSystem.s_UpdateCount;
        public bool WasCompletedThisFrame() => m_Enabled && m_CompletedFrame == InputSystem.s_UpdateCount;

        /// <summary>지금 값. 진행 중이 아니면 기본값 (0, Vector2.zero)</summary>
        public TValue ReadValue<TValue>() where TValue : struct
        {
            if (!IsInProgress()) return default;
            return ValueUtil.As<TValue>(m_Value, this, m_ActiveControl);
        }

        public object ReadValueAsObject() => IsInProgress() ? m_Value.ToObject() : null;

        /// <summary>시간 제한이 있는 상호작용 (Hold …) 이 얼마나 지났나 0 ~ 1</summary>
        public float GetTimeoutCompletionPercentage()
        {
            if (m_Phase == InputActionPhase.Performed) return 1f;
            if (m_Phase != InputActionPhase.Started || m_Driver == null || m_DriverIndex >= m_Driver.states.Length) return 0f;
            var s = m_Driver.states[m_DriverIndex];
            if (s.totalTimeout <= 0f) return 0f;
            return (float)Math.Clamp((InputSystem.CurrentTime - s.startTime) / s.totalTimeout, 0.0, 1.0);
        }

        // ---- Resolve
        internal void ClearResolved()
        {
            m_Resolved.Clear();
            m_WithInteractions.Clear();
            m_Controls.Clear();
            m_Default = null;
            m_Driver = null;
        }

        internal void AddResolved(BindingState st)
        {
            m_Resolved.Add(st);
            if (st.isComposite)
                foreach (var p in st.parts) foreach (var c in p.controls) { if (!m_Controls.Contains(c)) m_Controls.Add(c); }
            else
                foreach (var c in st.controls) if (!m_Controls.Contains(c)) m_Controls.Add(c);
        }

        internal void FinishResolve()
        {
            m_Default = new BindingState { map = m_ActionMap, action = this, isDefaultGroup = true };
            foreach (var st in m_Resolved)
            {
                if (st.interactions.Length > 0 && m_Type != InputActionType.PassThrough) m_WithInteractions.Add(st);
                else m_Default.members.Add(st);
            }
            if (m_Type != InputActionType.PassThrough)
            {
                m_Default.interactions = new IInputInteraction[] { new DefaultInteraction(m_Type) };
                m_Default.states = new InteractionState[1];
                m_Default.ResetInteractions();
            }
            if (m_Enabled) PrepareForEnable();
        }

        void PrepareForEnable()
        {
            m_NeedsInitialCheck = wantsInitialStateCheck;
            bool ignore = !m_NeedsInitialCheck && !(m_ActionMap != null && m_ActionMap.m_NoIgnoreOnce);
            foreach (var st in m_Resolved)
            {
                st.ResetInteractions();
                if (st.isComposite) foreach (var p in st.parts) { p.Snapshot(ignore); p.value = default; p.magnitude = 0; p.control = null; }
                else st.Snapshot(ignore);
                st.value = default;
                st.magnitude = 0f;
                st.control = null;
            }
            if (m_Default != null)
            {
                m_Default.ResetInteractions();
                m_Default.value = default;
                m_Default.magnitude = 0f;
                m_Default.control = null;
            }
        }

        internal void CancelForReresolve()
        {
            var src = m_Driver;
            m_Phase = InputActionPhase.Canceled;
            Fire(InputActionPhase.Canceled, src?.control ?? m_ActiveControl, src != null && m_DriverIndex < src.interactions.Length ? src.interactions[m_DriverIndex] : null, m_Value.Zero());
            if (m_Enabled) m_Phase = InputActionPhase.Waiting;
            m_Driver = null;
            m_Pressed = false;
        }

        // ---- 프레임
        internal void UpdateFrame(double now)
        {
            int frame = InputSystem.s_UpdateCount;
            bool initial = m_NeedsInitialCheck;
            m_NeedsInitialCheck = false;

            // 누름 (WasPressedThisFrame · IsPressed): 모든 바인딩 중 가장 큰 움직임
            float act = 0f;
            InputControl actControl = null;
            foreach (var st in m_Resolved)
                if (st.magnitude > act) { act = st.magnitude; actControl = st.control; }
            float press = ValueUtil.PressPoint(actControl);
            if (!m_Pressed && act >= press && act > 0f) { m_Pressed = true; m_PressedFrame = frame; }
            else if (m_Pressed && act <= press * InputSystem.settings.buttonReleaseThreshold) { m_Pressed = false; m_ReleasedFrame = frame; }

            if (m_Type == InputActionType.PassThrough)
            {
                UpdatePassThrough(initial);
                return;
            }

            // 상호작용 없는 바인딩들: 가장 많이 움직인 것 하나 (Unity 의 충돌 해결)
            var g = m_Default;
            if (g != null && g.members.Count > 0)
            {
                g.prevValue = g.value;
                g.prevMagnitude = g.magnitude;
                g.prevControl = g.control;
                BindingState best = null;
                foreach (var st in g.members)
                {
                    if (best == null || st.magnitude > best.magnitude || (st.magnitude == best.magnitude && st.magnitude > 0f && ReferenceEquals(st.control, g.prevControl)))
                        best = st;
                }
                if (best != null && best.magnitude > 0f)
                {
                    g.value = best.value;
                    g.magnitude = best.magnitude;
                    g.control = best.control;
                }
                else
                {
                    g.value = best != null ? best.value : default;
                    g.magnitude = 0f;
                    g.control = null;
                }
                if (g.Changed || (initial && g.magnitude > 0f))
                    ProcessInteractions(g, now, true);
                else
                    ProcessInteractions(g, now, false);
                if (!m_Enabled) return;
            }

            foreach (var st in m_WithInteractions)
            {
                ProcessInteractions(st, now, st.Changed || (initial && st.magnitude > 0f));
                if (!m_Enabled) return;
            }

            // 진행 중이면 값은 이끄는 바인딩의 지금 값
            if ((m_Phase == InputActionPhase.Started || m_Phase == InputActionPhase.Performed) && m_Driver != null)
            {
                m_Value = m_Driver.value;
                if (m_Driver.control != null) m_ActiveControl = m_Driver.control;
            }
        }

        void ProcessInteractions(BindingState st, double now, bool changed)
        {
            for (int k = 0; k < st.interactions.Length; k++)
            {
                bool expired = st.states[k].timeout >= 0 && now >= st.states[k].timeout;
                if (!changed && !expired) continue;
                if (expired) st.states[k].timeout = -1;
                var ctx = new InputInteractionContext { m_Action = this, m_Binding = st, m_Index = k, m_TimerExpired = expired };
                try { st.interactions[k].Process(ref ctx); }
                catch (Exception e) { Debug.LogException(e); }
                if (!m_Enabled) return;
            }
        }

        void UpdatePassThrough(bool initial)
        {
            foreach (var st in m_Resolved)
            {
                if (st.isComposite)
                {
                    if (st.Changed || (initial && st.magnitude > 0f)) PassThroughPerform(st.value, st.control);
                }
                else
                {
                    for (int i = 0; i < st.controls.Length; i++)
                    {
                        if (st.IsIgnored(i)) continue;
                        var d = st.ReadControl(i);
                        bool changed = !d.SameAs(st.lastControlValue[i]);
                        st.lastControlValue[i] = d;
                        if (changed || (initial && d.Magnitude() > 0f)) PassThroughPerform(d, st.controls[i]);
                        if (!m_Enabled) return;
                    }
                }
                if (!m_Enabled) return;
            }
            if (m_Phase == InputActionPhase.Performed && m_Value.Magnitude() <= 0f)
                m_Phase = InputActionPhase.Waiting;
        }

        void PassThroughPerform(in InputValueData v, InputControl control)
        {
            m_Value = v;
            m_ActiveControl = control;
            m_StartTime = InputSystem.CurrentTime;
            m_Phase = InputActionPhase.Performed;
            Fire(InputActionPhase.Performed, control, null, v);
        }

        // 상호작용의 단계 바꾸기 (InputInteractionContext 가 부른다)
        internal void InteractionChanged(BindingState b, int index, InputActionPhase newPhase, InputActionPhase after)
        {
            if (!m_Enabled) return;
            ref var st = ref b.states[index];
            var interaction = b.interactions[index];
            bool isDriver = ReferenceEquals(m_Driver, b) && m_DriverIndex == index;
            double now = InputSystem.CurrentTime;
            switch (newPhase)
            {
                case InputActionPhase.Started:
                    if (st.phase == InputActionPhase.Waiting) st.startTime = now;
                    st.phase = InputActionPhase.Started;
                    if (m_Phase == InputActionPhase.Waiting)
                    {
                        m_Driver = b;
                        m_DriverIndex = index;
                        m_Phase = InputActionPhase.Started;
                        m_StartTime = now;
                        m_Value = b.value;
                        m_ActiveControl = b.control;
                        Fire(InputActionPhase.Started, b.control, interaction, b.value);
                    }
                    break;

                case InputActionPhase.Performed:
                {
                    if (st.phase == InputActionPhase.Waiting) st.startTime = now;
                    if (m_Phase == InputActionPhase.Waiting)
                    {
                        m_Driver = b;
                        m_DriverIndex = index;
                        m_Phase = InputActionPhase.Started;
                        m_StartTime = now;
                        m_Value = b.value;
                        m_ActiveControl = b.control;
                        Fire(InputActionPhase.Started, b.control, interaction, b.value);
                        if (!m_Enabled) return;
                    }
                    else if (!isDriver)
                    {
                        m_Driver = b;
                        m_DriverIndex = index;
                    }
                    m_Value = b.value;
                    if (b.control != null) m_ActiveControl = b.control;
                    m_Phase = InputActionPhase.Performed;
                    m_LastTriggerTime = now;
                    Fire(InputActionPhase.Performed, m_ActiveControl, interaction, b.value);
                    if (!m_Enabled) return;
                    ResetOthers(b, index);
                    ref var st2 = ref b.states[index];
                    if (after == InputActionPhase.Waiting)
                    {
                        st2.phase = InputActionPhase.Waiting;
                        st2.timeout = -1;
                        st2.totalTimeout = 0;
                        interaction.Reset();
                        m_Phase = InputActionPhase.Waiting;
                        m_Driver = null;
                        m_CompletedFrame = InputSystem.s_UpdateCount;
                    }
                    else if (after == InputActionPhase.Started)
                    {
                        st2.phase = InputActionPhase.Started;
                        m_Phase = InputActionPhase.Started;
                    }
                    else
                    {
                        st2.phase = InputActionPhase.Performed;
                        m_Phase = InputActionPhase.Performed;
                    }
                    break;
                }

                case InputActionPhase.Canceled:
                {
                    st.phase = InputActionPhase.Waiting;
                    st.timeout = -1;
                    st.totalTimeout = 0;
                    interaction.Reset();
                    if (isDriver && m_Phase != InputActionPhase.Waiting)
                    {
                        bool wasPerformed = m_Phase == InputActionPhase.Performed;
                        var control = b.control ?? m_ActiveControl;
                        m_Phase = InputActionPhase.Canceled;
                        Fire(InputActionPhase.Canceled, control, interaction, m_Value.Zero());
                        if (!m_Enabled) return;
                        m_Phase = InputActionPhase.Waiting;
                        m_Driver = null;
                        if (wasPerformed) m_CompletedFrame = InputSystem.s_UpdateCount;
                        TakeOver();
                    }
                    break;
                }

                case InputActionPhase.Waiting:
                    st.phase = InputActionPhase.Waiting;
                    st.timeout = -1;
                    st.totalTimeout = 0;
                    interaction.Reset();
                    if (isDriver)
                    {
                        m_Phase = InputActionPhase.Waiting;
                        m_Driver = null;
                    }
                    break;
            }
        }

        // 하나가 performed 하면 나머지 상호작용은 처음부터 (Unity 와 같이)
        void ResetOthers(BindingState b, int index)
        {
            void Reset(BindingState s)
            {
                for (int k = 0; k < s.states.Length; k++)
                {
                    if (ReferenceEquals(s, b) && k == index) continue;
                    if (s.states[k].phase == InputActionPhase.Waiting) continue;
                    s.states[k] = new InteractionState { phase = InputActionPhase.Waiting, timeout = -1 };
                    s.interactions[k].Reset();
                }
            }
            if (m_Default != null) Reset(m_Default);
            foreach (var s in m_WithInteractions) Reset(s);
        }

        // 이끌던 상호작용이 끝났는데 다른 것이 진행 중이면 그것이 이어받는다
        void TakeOver()
        {
            BindingState found = null;
            int fi = -1;
            void Look(BindingState s)
            {
                if (found != null) return;
                for (int k = 0; k < s.states.Length; k++)
                    if (s.states[k].phase == InputActionPhase.Started || s.states[k].phase == InputActionPhase.Performed) { found = s; fi = k; return; }
            }
            if (m_Default != null) Look(m_Default);
            foreach (var s in m_WithInteractions) Look(s);
            if (found == null) return;
            m_Driver = found;
            m_DriverIndex = fi;
            m_Phase = InputActionPhase.Started;
            m_StartTime = found.states[fi].startTime;
            m_Value = found.value;
            m_ActiveControl = found.control;
            Fire(InputActionPhase.Started, found.control, found.interactions[fi], found.value);
        }

        internal void Fire(InputActionPhase phase, InputControl control, IInputInteraction interaction, InputValueData value)
        {
            int frame = InputSystem.s_UpdateCount;
            double now = InputSystem.CurrentTime;
            if (phase == InputActionPhase.Started) m_StartedFrame = frame;
            else if (phase == InputActionPhase.Performed) m_PerformedFrame = frame;
            else if (phase == InputActionPhase.Canceled) m_CanceledFrame = frame;
            var ctx = new CallbackContext
            {
                m_Action = this,
                m_Phase = phase,
                m_Control = control,
                m_Interaction = interaction is DefaultInteraction ? null : interaction,
                m_Time = now,
                m_StartTime = m_StartTime,
                m_Value = value,
            };
            var list = phase == InputActionPhase.Started ? m_OnStarted : phase == InputActionPhase.Performed ? m_OnPerformed : m_OnCanceled;
            if (list.Count > 0)
                foreach (var cb in list.ToArray())
                {
                    try { cb(ctx); }
                    catch (Exception e) { Debug.LogException(e); }
                }
            var map = m_ActionMap;
            if (map != null && map.triggeredListeners.Count > 0)
                foreach (var cb in map.triggeredListeners.ToArray())
                {
                    try { cb(ctx); }
                    catch (Exception e) { Debug.LogException(e); }
                }
            InputSystem.NotifyActionTriggered(ctx);
        }
    }
}
