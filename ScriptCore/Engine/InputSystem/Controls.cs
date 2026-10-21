using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NovaEngine.InputSystem.Controls;
using NovaEngine.InputSystem.LowLevel;
using NovaEngine.InputSystem.Processors;
using NovaEngine.InputSystem.Utilities;

namespace NovaEngine.InputSystem
{
    /// <summary>
    /// Unity 의 InputControl — 장치 안의 단추 · 축 · 스틱 하나 ("/Keyboard/space", "/XInputControllerWindows/leftStick/x").
    /// 값은 장치의 상태 칸 (프레임마다 엔진이 채운다) 에서 읽고, 레이아웃의 기본 프로세서 (스틱 데드존 …) 를 거친다.
    /// </summary>
    public abstract class InputControl
    {
        internal string m_Name = string.Empty;
        internal string m_DisplayName;
        internal string m_ShortDisplayName;
        internal string m_Layout = string.Empty;
        internal string[] m_Aliases = Array.Empty<string>();
        internal string[] m_Usages = Array.Empty<string>();
        internal InputDevice m_Device;
        internal InputControl m_Parent;
        internal InputControl[] m_Children = Array.Empty<InputControl>();
        internal bool m_Noisy;
        internal bool m_Synthetic;
        internal string m_Path;

        public string name => m_Name;
        public string displayName => m_DisplayName ?? m_Name;
        public string shortDisplayName => m_ShortDisplayName;
        /// <summary>"/Keyboard/space" (장치 이름부터)</summary>
        public string path => m_Path ??= m_Parent == null ? "/" + m_Name : m_Parent.path + "/" + m_Name;
        /// <summary>컨트롤 레이아웃 ("Button", "Key", "Axis", "Stick", "Dpad", "Vector2" …), 장치는 장치 레이아웃</summary>
        public string layout => m_Layout;
        public InputDevice device => m_Device;
        public InputControl parent => m_Parent;
        public ReadOnlyArray<InputControl> children => m_Children;
        public ReadOnlyArray<string> usages => m_Usages;
        public ReadOnlyArray<string> aliases => m_Aliases;
        /// <summary>손을 대지 않아도 값이 바뀌는 컨트롤 (마우스 위치 · 이동량) — 다시 묶기 · 장치 전환에서 뺀다</summary>
        public bool noisy => m_Noisy;
        /// <summary>다른 컨트롤에서 만든 값 (anyKey · shift · 스틱 up/down …)</summary>
        public bool synthetic => m_Synthetic;
        public float magnitude => EvaluateMagnitude();

        public abstract Type valueType { get; }
        public abstract int valueSizeInBytes { get; }
        public abstract object ReadValueAsObject();
        public virtual object ReadDefaultValueAsObject() => null;

        /// <summary>얼마나 눌렸나 (0 ~ 1, 스틱은 길이). 모르는 컨트롤은 -1</summary>
        public virtual float EvaluateMagnitude() => -1f;

        public bool IsActuated(float threshold = 0f)
        {
            float m = EvaluateMagnitude();
            if (m < 0f) return false;
            return threshold <= 0f ? m > 0f : m >= threshold;
        }

        public InputControl this[string path]
        {
            get
            {
                var c = TryGetChildControl(path);
                if (c == null) throw new KeyNotFoundException($"Cannot find control '{path}' as child of '{this}'");
                return c;
            }
        }

        public InputControl TryGetChildControl(string path) => InputControlPath.TryFindChild(this, path);
        public TControl TryGetChildControl<TControl>(string path) where TControl : InputControl => TryGetChildControl(path) as TControl;
        public TControl GetChildControl<TControl>(string path) where TControl : InputControl
        {
            var c = TryGetChildControl(path);
            if (c is TControl t) return t;
            throw new KeyNotFoundException($"Cannot find control '{path}' of type '{typeof(TControl).Name}' as child of '{this}'");
        }

        public override string ToString() => layout + ":" + path;

        // ---- 내부: 장치 상태 · 레이아웃
        internal bool IsBasedOnLayout(string layoutName)
        {
            if (string.Equals(m_Layout, layoutName, StringComparison.OrdinalIgnoreCase)) return true;
            if (this is InputDevice d) return d.HasLayout(layoutName);
            foreach (string b in ControlLayoutBases(m_Layout))
                if (string.Equals(b, layoutName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static IEnumerable<string> ControlLayoutBases(string layout)
        {
            switch (layout)
            {
                case "Key": case "AnyKey": case "TouchPress": case "DiscreteButton": yield return "Button"; yield return "Axis"; break;
                case "Button": yield return "Axis"; break;
                case "Stick": case "Dpad": case "Delta": yield return "Vector2"; break;
            }
        }

        // 값 종류 이름 (expectedControlType 맞추기 · 오류 문구)
        internal string valueTypeName
        {
            get
            {
                Type t = valueType;
                if (t == typeof(float)) return "float";
                if (t == typeof(Vector2)) return "Vector2";
                if (t == typeof(Vector3)) return "Vector3";
                if (t == typeof(int)) return "int";
                if (t == typeof(double)) return "double";
                return t.Name;
            }
        }

        internal void AddChild(InputControl c)
        {
            var arr = new InputControl[m_Children.Length + 1];
            Array.Copy(m_Children, arr, m_Children.Length);
            arr[m_Children.Length] = c;
            m_Children = arr;
        }

        internal void InvalidatePath()
        {
            m_Path = null;
            foreach (var c in m_Children) c.InvalidatePath();
        }
    }

    /// <summary>값 형식이 정해진 컨트롤 (Unity 의 InputControl&lt;TValue&gt;)</summary>
    public abstract class InputControl<TValue> : InputControl where TValue : struct
    {
        internal InputProcessor<TValue>[] m_Processors;

        public override Type valueType => typeof(TValue);
        public override int valueSizeInBytes => Unsafe.SizeOf<TValue>();
        public TValue value => ReadValue();

        /// <summary>이번 프레임 값 (레이아웃 프로세서 — 스틱 데드존 등 — 를 거친 값)</summary>
        public TValue ReadValue() => ProcessValue(ReadUnprocessedValue());
        public TValue ReadValueFromPreviousFrame() => ProcessValue(ReadUnprocessedValueFromPreviousFrame());
        public TValue ReadUnprocessedValue() => ReadRaw(false);
        public TValue ReadUnprocessedValueFromPreviousFrame() => ReadRaw(true);
        public TValue ReadDefaultValue() => default;

        public TValue ProcessValue(TValue value)
        {
            var ps = m_Processors;
            if (ps != null)
                for (int i = 0; i < ps.Length; i++) value = ps[i].Process(value, this);
            return value;
        }

        public override object ReadValueAsObject() => ReadValue();
        public override object ReadDefaultValueAsObject() => default(TValue);

        internal abstract TValue ReadRaw(bool previous);
        internal abstract void WriteRaw(TValue value);   // InputState.Change · 시험 장치

        internal void AddProcessor(InputProcessor<TValue> p)
        {
            var old = m_Processors ?? Array.Empty<InputProcessor<TValue>>();
            var arr = new InputProcessor<TValue>[old.Length + 1];
            Array.Copy(old, arr, old.Length);
            arr[old.Length] = p;
            m_Processors = arr;
        }
    }
}

namespace NovaEngine.InputSystem.Controls
{
    /// <summary>Unity 의 AxisControl — 실수 하나 (트리거 · 스틱 한 축). 매개변수 (clamp · normalize · invert · scale) 는 프로세서 전에</summary>
    public class AxisControl : InputControl<float>
    {
        public enum Clamp { None = 0, BeforeNormalize = 1, AfterNormalize = 2, ToConstantBeforeNormalize = 3 }

        public Clamp clamp;
        public float clampMin, clampMax, clampConstant;
        public bool invert;
        public bool normalize;
        public float normalizeMin, normalizeMax, normalizeZero;
        public bool scale;
        public float scaleFactor = 1f;

        internal int m_Slot = -1;
        internal Func<bool, float> m_Reader;   // 합성 컨트롤 (anyKey · shift · dpad x …)

        internal float ReadSlot(bool previous) => m_Reader != null ? m_Reader(previous) : (m_Slot >= 0 ? m_Device.Slot(m_Slot, previous) : 0f);

        internal override float ReadRaw(bool previous) => Preprocess(ReadSlot(previous));

        internal float Preprocess(float value)
        {
            if (scale) value *= scaleFactor;
            if (clamp == Clamp.ToConstantBeforeNormalize) { if (value < clampMin || value > clampMax) value = clampConstant; }
            else if (clamp == Clamp.BeforeNormalize) value = Math.Clamp(value, clampMin, clampMax);
            if (normalize) value = NormalizeProcessor.Normalize(value, normalizeMin, normalizeMax, normalizeZero);
            if (clamp == Clamp.AfterNormalize) value = Math.Clamp(value, clampMin, clampMax);
            if (invert) value = -value;
            return value;
        }

        internal override void WriteRaw(float value)
        {
            if (m_Slot < 0) return;
            if (invert) value = -value;
            if (scale && scaleFactor != 0f) value /= scaleFactor;
            m_Device.SetSlot(m_Slot, value);
        }

        public override float EvaluateMagnitude() => Math.Min(1f, Math.Abs(ReadValue()));
    }

    /// <summary>Unity 의 ButtonControl — 누름 값 0 ~ 1 (pressPoint 넘으면 눌림)</summary>
    public class ButtonControl : AxisControl
    {
        /// <summary>눌림 문턱 (음수 = InputSettings.defaultButtonPressPoint)</summary>
        public float pressPoint = -1f;
        public float pressPointOrDefault => pressPoint > 0f ? pressPoint : InputSystem.settings.defaultButtonPressPoint;

        public bool isPressed => IsValueConsideredPressed(ReadValue());
        public bool wasPressedThisFrame => m_Device != null && IsValueConsideredPressed(ReadValue()) && !IsValueConsideredPressed(ReadValueFromPreviousFrame());
        public bool wasReleasedThisFrame => m_Device != null && !IsValueConsideredPressed(ReadValue()) && IsValueConsideredPressed(ReadValueFromPreviousFrame());
        public bool IsValueConsideredPressed(float value) => value >= pressPointOrDefault;
    }

    /// <summary>Unity 의 KeyControl — 자판 키 하나</summary>
    public class KeyControl : ButtonControl
    {
        internal Key m_Key;
        internal int m_VirtualKey;
        public Key keyCode => m_Key;
        /// <summary>Windows 가상 키 코드 (Unity 의 scanCode 자리)</summary>
        public int scanCode => m_VirtualKey;
    }

    /// <summary>아무 키나 (Unity 의 AnyKeyControl)</summary>
    public class AnyKeyControl : ButtonControl { }

    /// <summary>Unity 의 IntegerControl (터치 id · 클릭 수)</summary>
    public class IntegerControl : InputControl<int>
    {
        internal int m_Slot = -1;
        internal override int ReadRaw(bool previous) => m_Slot >= 0 ? (int)m_Device.Slot(m_Slot, previous) : 0;
        internal override void WriteRaw(int value) { if (m_Slot >= 0) m_Device.SetSlot(m_Slot, value); }
        public override float EvaluateMagnitude() => -1f;
    }

    /// <summary>Unity 의 DoubleControl (터치 시작 시각)</summary>
    public class DoubleControl : InputControl<double>
    {
        internal int m_Slot = -1;
        internal override double ReadRaw(bool previous) => m_Slot >= 0 ? m_Device.Slot(m_Slot, previous) : 0.0;
        internal override void WriteRaw(double value) { if (m_Slot >= 0) m_Device.SetSlot(m_Slot, (float)value); }
    }

    /// <summary>Unity 의 Vector2Control — x · y 두 축 (자식은 처리 전 값으로 읽고, 이 컨트롤의 프로세서를 거친다)</summary>
    public class Vector2Control : InputControl<Vector2>
    {
        public AxisControl x { get; internal set; }
        public AxisControl y { get; internal set; }
        internal override Vector2 ReadRaw(bool previous) => new Vector2(x.ReadRaw(previous), y.ReadRaw(previous));
        internal override void WriteRaw(Vector2 value) { x.WriteRaw(value.x); y.WriteRaw(value.y); }
        public override float EvaluateMagnitude() => ReadValue().magnitude;
    }

    /// <summary>마우스 이동량 · 휠 (Unity 의 DeltaControl: up · down · left · right 단추)</summary>
    public class DeltaControl : Vector2Control
    {
        public ButtonControl up { get; internal set; }
        public ButtonControl down { get; internal set; }
        public ButtonControl left { get; internal set; }
        public ButtonControl right { get; internal set; }
    }

    /// <summary>Unity 의 StickControl — 아날로그 스틱 (기본 stickDeadzone), up · down · left · right 는 한쪽 축</summary>
    public class StickControl : Vector2Control
    {
        public ButtonControl up { get; internal set; }
        public ButtonControl down { get; internal set; }
        public ButtonControl left { get; internal set; }
        public ButtonControl right { get; internal set; }
    }

    /// <summary>Unity 의 DpadControl — 네 단추, 값은 (오른 - 왼, 위 - 아래) 정규화</summary>
    public class DpadControl : Vector2Control
    {
        public ButtonControl up { get; internal set; }
        public ButtonControl down { get; internal set; }
        public ButtonControl left { get; internal set; }
        public ButtonControl right { get; internal set; }

        internal override Vector2 ReadRaw(bool previous)
        {
            float press = InputSystem.settings.defaultButtonPressPoint;
            bool u = up.ReadRaw(previous) >= press, d = down.ReadRaw(previous) >= press, l = left.ReadRaw(previous) >= press, r = right.ReadRaw(previous) >= press;
            return MakeDpadVector(u, d, l, r);
        }

        internal override void WriteRaw(Vector2 value)
        {
            up.WriteRaw(value.y > 0 ? 1 : 0); down.WriteRaw(value.y < 0 ? 1 : 0);
            right.WriteRaw(value.x > 0 ? 1 : 0); left.WriteRaw(value.x < 0 ? 1 : 0);
        }

        public static Vector2 MakeDpadVector(bool up, bool down, bool left, bool right, bool normalize = true)
        {
            float x = (right ? 1f : 0f) - (left ? 1f : 0f), y = (up ? 1f : 0f) - (down ? 1f : 0f);
            if (normalize && x != 0f && y != 0f) { x *= 0.70710678f; y *= 0.70710678f; }
            return new Vector2(x, y);
        }
    }

    /// <summary>Unity 의 TouchPhaseControl</summary>
    public class TouchPhaseControl : InputControl<TouchPhase>
    {
        internal int m_Slot = -1;
        internal override TouchPhase ReadRaw(bool previous) => m_Slot >= 0 ? (TouchPhase)(int)m_Device.Slot(m_Slot, previous) : TouchPhase.None;
        internal override void WriteRaw(TouchPhase value) { if (m_Slot >= 0) m_Device.SetSlot(m_Slot, (int)value); }
    }

    /// <summary>Unity 의 TouchPressControl (터치 누름 단추)</summary>
    public class TouchPressControl : ButtonControl { }

    /// <summary>Unity 의 TouchControl — 손가락 하나 (press · position · delta · phase · tap …)</summary>
    public class TouchControl : InputControl<TouchState>
    {
        public TouchPressControl press { get; internal set; }
        public IntegerControl touchId { get; internal set; }
        public Vector2Control position { get; internal set; }
        public DeltaControl delta { get; internal set; }
        public AxisControl pressure { get; internal set; }
        public Vector2Control radius { get; internal set; }
        public TouchPhaseControl phase { get; internal set; }
        public ButtonControl tap { get; internal set; }
        public IntegerControl tapCount { get; internal set; }
        public Vector2Control startPosition { get; internal set; }
        public DoubleControl startTime { get; internal set; }
        public bool isInProgress { get { var p = phase.ReadValue(); return p == TouchPhase.Began || p == TouchPhase.Moved || p == TouchPhase.Stationary; } }

        internal override TouchState ReadRaw(bool previous) => new TouchState
        {
            touchId = touchId.ReadRaw(previous),
            position = position.ReadRaw(previous),
            delta = delta.ReadRaw(previous),
            pressure = pressure.ReadRaw(previous),
            radius = radius.ReadRaw(previous),
            phase = phase.ReadRaw(previous),
            tapCount = (byte)tapCount.ReadRaw(previous),
            startPosition = startPosition.ReadRaw(previous),
            startTime = startTime.ReadRaw(previous),
            isTap = tap.ReadRaw(previous) >= 0.5f,
        };

        internal override void WriteRaw(TouchState v)
        {
            touchId.WriteRaw(v.touchId); position.WriteRaw(v.position); delta.WriteRaw(v.delta); pressure.WriteRaw(v.pressure);
            radius.WriteRaw(v.radius); phase.WriteRaw(v.phase); tapCount.WriteRaw(v.tapCount); startPosition.WriteRaw(v.startPosition);
            startTime.WriteRaw(v.startTime); tap.WriteRaw(v.isTap ? 1f : 0f);
            bool down = v.phase == TouchPhase.Began || v.phase == TouchPhase.Moved || v.phase == TouchPhase.Stationary;
            press.WriteRaw(down ? 1f : 0f);
        }

        public override float EvaluateMagnitude() => press.EvaluateMagnitude();
    }
}
