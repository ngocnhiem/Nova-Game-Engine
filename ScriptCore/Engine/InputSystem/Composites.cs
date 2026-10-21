using System;
using System.Collections.Generic;
using NovaEngine.InputSystem.Controls;
using NovaEngine.InputSystem.Layouts;

namespace NovaEngine.InputSystem.Layouts
{
    /// <summary>
    /// Unity 의 InputControlAttribute — 여기서는 합성 바인딩의 부분 (part) 표시: [InputControl(layout = "Button")] public int up;
    /// (필드 값은 부분 번호 — InputBindingCompositeContext.ReadValue 에 넘긴다)
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true)]
    public sealed class InputControlAttribute : Attribute
    {
        public string layout { get; set; }
        public string name { get; set; }
        public string displayName { get; set; }
        public string shortDisplayName { get; set; }
        public string usage { get; set; }
        public string[] usages { get; set; }
        public string processors { get; set; }
        public string parameters { get; set; }
        public bool synthetic { get; set; }
        public bool noisy { get; set; }
    }
}

namespace NovaEngine.InputSystem
{
    /// <summary>
    /// Unity 의 InputBindingComposite — 여러 컨트롤을 값 하나로 (WASD → Vector2, 수정 키 + 키).
    /// 직접 만들기: class MyComposite : InputBindingComposite&lt;float&gt; { [InputControl(layout = "Button")] public int first; … } + InputSystem.RegisterBindingComposite
    /// </summary>
    public abstract class InputBindingComposite
    {
        public abstract Type valueType { get; }
        public abstract int valueSizeInBytes { get; }
        public abstract object ReadValueAsObject(ref InputBindingCompositeContext context);
        public virtual float EvaluateMagnitude(ref InputBindingCompositeContext context) => -1f;
        protected virtual void FinishSetup(ref InputBindingCompositeContext context) { }
        internal void CallFinishSetup(ref InputBindingCompositeContext context) => FinishSetup(ref context);
        internal abstract InputValueData ReadValueData(ref InputBindingCompositeContext context);

        // 부분 이름 → 번호 (필드 순서, 1 부터)
        static readonly Dictionary<Type, List<(string name, System.Reflection.FieldInfo field)>> s_Parts = new Dictionary<Type, List<(string, System.Reflection.FieldInfo)>>();
        internal static List<(string name, System.Reflection.FieldInfo field)> PartsOf(Type t)
        {
            if (s_Parts.TryGetValue(t, out var l)) return l;
            l = new List<(string, System.Reflection.FieldInfo)>();
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                if (f.FieldType == typeof(int) && Attribute.IsDefined(f, typeof(InputControlAttribute)))
                    l.Add((f.Name, f));
            s_Parts[t] = l;
            return l;
        }

        internal int AssignPartNumber(string partName)
        {
            var parts = PartsOf(GetType());
            for (int i = 0; i < parts.Count; i++)
                if (string.Equals(parts[i].name, partName, StringComparison.OrdinalIgnoreCase))
                {
                    parts[i].field.SetValue(this, i + 1);
                    return i + 1;
                }
            return -1;
        }
    }

    public abstract class InputBindingComposite<TValue> : InputBindingComposite where TValue : struct
    {
        public override Type valueType => typeof(TValue);
        public override int valueSizeInBytes => System.Runtime.CompilerServices.Unsafe.SizeOf<TValue>();
        public abstract TValue ReadValue(ref InputBindingCompositeContext context);
        public override object ReadValueAsObject(ref InputBindingCompositeContext context) => ReadValue(ref context);
        internal override InputValueData ReadValueData(ref InputBindingCompositeContext context) => InputValueData.From(ReadValue(ref context));
    }

    /// <summary>Unity 의 InputBindingCompositeContext — 부분 번호로 값 읽기</summary>
    public struct InputBindingCompositeContext
    {
        internal BindingState m_Composite;

        public struct PartBinding
        {
            public int part { get; set; }
            public InputControl control { get; set; }
        }

        public IEnumerable<PartBinding> controls
        {
            get
            {
                foreach (var p in m_Composite.parts)
                    foreach (var c in p.controls)
                        yield return new PartBinding { part = p.partNumber, control = c };
            }
        }

        // 부분의 컨트롤 중 가장 큰 값 (Unity 와 같이 — 버튼이면 가장 많이 눌린 것)
        public TValue ReadValue<TValue>(int partNumber) where TValue : struct, IComparable<TValue> => ReadValue<TValue>(partNumber, out _);

        public TValue ReadValue<TValue>(int partNumber, out InputControl sourceControl) where TValue : struct, IComparable<TValue>
        {
            sourceControl = null;
            TValue best = default;
            bool any = false;
            if (m_Composite == null) return best;
            foreach (var p in m_Composite.parts)
            {
                if (p.partNumber != partNumber) continue;
                for (int i = 0; i < p.controls.Length; i++)
                {
                    if (p.IsIgnored(i)) continue;
                    var v = ValueUtil.As<TValue>(p.ReadControl(i), null, p.controls[i]);
                    if (!any || v.CompareTo(best) > 0) { best = v; sourceControl = p.controls[i]; any = true; }
                }
            }
            return best;
        }

        public bool ReadValueAsButton(int partNumber)
        {
            if (m_Composite == null) return false;
            foreach (var p in m_Composite.parts)
            {
                if (p.partNumber != partNumber) continue;
                for (int i = 0; i < p.controls.Length; i++)
                {
                    if (p.IsIgnored(i)) continue;
                    var d = p.ReadControl(i);
                    if (d.Magnitude() >= ValueUtil.PressPoint(p.controls[i])) return true;
                }
            }
            return false;
        }

        public float EvaluateMagnitude(int partNumber)
        {
            float best = 0f;
            if (m_Composite == null) return best;
            foreach (var p in m_Composite.parts)
            {
                if (p.partNumber != partNumber) continue;
                for (int i = 0; i < p.controls.Length; i++)
                    if (!p.IsIgnored(i)) best = Math.Max(best, p.ReadControl(i).Magnitude());
            }
            return best;
        }

        public object ReadValueAsObject(int partNumber)
        {
            var d = ReadPart(partNumber, out _);
            return d.ToObject();
        }

        // 형식이 정해지지 않은 부분 (OneModifier 의 binding): 가장 크게 움직인 컨트롤의 값
        internal InputValueData ReadPart(int partNumber, out InputControl source)
        {
            source = null;
            InputValueData best = default;
            float bestMag = -1f;
            if (m_Composite == null) return best;
            foreach (var p in m_Composite.parts)
            {
                if (p.partNumber != partNumber) continue;
                for (int i = 0; i < p.controls.Length; i++)
                {
                    if (p.IsIgnored(i)) continue;
                    var d = p.ReadControl(i);
                    float m = d.Magnitude();
                    if (m > bestMag) { bestMag = m; best = d; source = p.controls[i]; }
                }
            }
            return best;
        }

        // 부분이 처음 눌린 시각 (수정 키를 먼저 눌렀는가)
        public double GetPressTime(int partNumber)
        {
            double t = double.MaxValue;
            if (m_Composite == null) return t;
            foreach (var p in m_Composite.parts)
                if (p.partNumber == partNumber && p.pressTime >= 0 && p.pressTime < t) t = p.pressTime;
            return t;
        }
    }
}

namespace NovaEngine.InputSystem.Composites
{
    /// <summary>1DAxis (별칭 Axis): negative · positive → minValue ~ maxValue</summary>
    public class AxisComposite : InputBindingComposite<float>
    {
        public enum WhichSideWins { Neither = 0, Positive = 1, Negative = 2 }

        [InputControl(layout = "Axis")] public int negative;
        [InputControl(layout = "Axis")] public int positive;
        public float minValue = -1f;
        public float maxValue = 1f;
        public WhichSideWins whichSideWins = WhichSideWins.Neither;
        public float midPoint => (maxValue + minValue) / 2f;

        public override float ReadValue(ref InputBindingCompositeContext context)
        {
            float neg = Math.Abs(context.ReadValue<float>(negative));
            float pos = Math.Abs(context.ReadValue<float>(positive));
            bool negDown = neg > float.Epsilon, posDown = pos > float.Epsilon;
            if (negDown == posDown)
            {
                if (!negDown) return midPoint;
                switch (whichSideWins)
                {
                    case WhichSideWins.Negative: posDown = false; break;
                    case WhichSideWins.Positive: negDown = false; break;
                    default: return midPoint;
                }
            }
            float mid = midPoint;
            if (negDown) return mid - neg * (mid - minValue);
            return mid + pos * (maxValue - mid);
        }

        public override float EvaluateMagnitude(ref InputBindingCompositeContext context)
        {
            float value = ReadValue(ref context);
            if (value < midPoint) return Math.Abs((value - midPoint) / (minValue - midPoint));
            return Math.Abs((value - midPoint) / (maxValue - midPoint));
        }
    }

    /// <summary>2DVector (별칭 Dpad): up · down · left · right → Vector2. mode: DigitalNormalized (기본 — 대각선 길이 1) · Digital · Analog</summary>
    public class Vector2Composite : InputBindingComposite<Vector2>
    {
        public enum Mode { Analog = 2, DigitalNormalized = 0, Digital = 1 }

        [InputControl(layout = "Axis")] public int up;
        [InputControl(layout = "Axis")] public int down;
        [InputControl(layout = "Axis")] public int left;
        [InputControl(layout = "Axis")] public int right;
        public Mode mode;

        public override Vector2 ReadValue(ref InputBindingCompositeContext context)
        {
            if (mode == Mode.Analog)
            {
                float u = context.ReadValue<float>(up), d = context.ReadValue<float>(down), l = context.ReadValue<float>(left), r = context.ReadValue<float>(right);
                return new Vector2(r - l, u - d);
            }
            bool uu = context.ReadValueAsButton(up), dd = context.ReadValueAsButton(down), ll = context.ReadValueAsButton(left), rr = context.ReadValueAsButton(right);
            return DpadControl.MakeDpadVector(uu, dd, ll, rr, mode == Mode.DigitalNormalized);
        }

        public override float EvaluateMagnitude(ref InputBindingCompositeContext context) => ReadValue(ref context).magnitude;
    }

    /// <summary>3DVector: up · down · left · right · forward · backward → Vector3</summary>
    public class Vector3Composite : InputBindingComposite<Vector3>
    {
        public enum Mode { Analog = 0, DigitalNormalized = 1, Digital = 2 }

        [InputControl(layout = "Axis")] public int up;
        [InputControl(layout = "Axis")] public int down;
        [InputControl(layout = "Axis")] public int left;
        [InputControl(layout = "Axis")] public int right;
        [InputControl(layout = "Axis")] public int forward;
        [InputControl(layout = "Axis")] public int backward;
        public Mode mode;

        public override Vector3 ReadValue(ref InputBindingCompositeContext context)
        {
            if (mode == Mode.Analog)
            {
                return new Vector3(context.ReadValue<float>(right) - context.ReadValue<float>(left),
                    context.ReadValue<float>(up) - context.ReadValue<float>(down),
                    context.ReadValue<float>(forward) - context.ReadValue<float>(backward));
            }
            var v = new Vector3((context.ReadValueAsButton(right) ? 1f : 0f) - (context.ReadValueAsButton(left) ? 1f : 0f),
                (context.ReadValueAsButton(up) ? 1f : 0f) - (context.ReadValueAsButton(down) ? 1f : 0f),
                (context.ReadValueAsButton(forward) ? 1f : 0f) - (context.ReadValueAsButton(backward) ? 1f : 0f));
            return mode == Mode.DigitalNormalized ? v.normalized : v;
        }

        public override float EvaluateMagnitude(ref InputBindingCompositeContext context) => ReadValue(ref context).magnitude;
    }

    /// <summary>ButtonWithOneModifier: modifier 를 누른 채 button (Ctrl+S)</summary>
    public class ButtonWithOneModifier : InputBindingComposite<float>
    {
        [InputControl(layout = "Button")] public int modifier;
        [InputControl(layout = "Button")] public int button;
        public bool overrideModifiersNeedToBePressedFirst;

        public override float ReadValue(ref InputBindingCompositeContext context)
        {
            if (ModifierIsPressed(ref context)) return context.ReadValue<float>(button);
            return 0f;
        }

        bool ModifierIsPressed(ref InputBindingCompositeContext context)
        {
            bool down = context.ReadValueAsButton(modifier);
            if (down && !overrideModifiersNeedToBePressedFirst)
                return context.GetPressTime(button) >= context.GetPressTime(modifier);
            return down;
        }

        public override float EvaluateMagnitude(ref InputBindingCompositeContext context) => ReadValue(ref context);
    }

    /// <summary>ButtonWithTwoModifiers: modifier1 + modifier2 + button (Ctrl+Shift+S)</summary>
    public class ButtonWithTwoModifiers : InputBindingComposite<float>
    {
        [InputControl(layout = "Button")] public int modifier1;
        [InputControl(layout = "Button")] public int modifier2;
        [InputControl(layout = "Button")] public int button;
        public bool overrideModifiersNeedToBePressedFirst;

        public override float ReadValue(ref InputBindingCompositeContext context)
        {
            bool down = context.ReadValueAsButton(modifier1) && context.ReadValueAsButton(modifier2);
            if (down && !overrideModifiersNeedToBePressedFirst)
            {
                double b = context.GetPressTime(button);
                down = b >= context.GetPressTime(modifier1) && b >= context.GetPressTime(modifier2);
            }
            return down ? context.ReadValue<float>(button) : 0f;
        }

        public override float EvaluateMagnitude(ref InputBindingCompositeContext context) => ReadValue(ref context);
    }

    /// <summary>OneModifier: modifier 를 누른 채 binding (아무 값 — Shift + 마우스 이동)</summary>
    public class OneModifierComposite : InputBindingComposite
    {
        [InputControl(layout = "Button")] public int modifier;
        [InputControl] public int binding;
        public bool overrideModifiersNeedToBePressedFirst;

        public override Type valueType => typeof(float);
        public override int valueSizeInBytes => 4;

        bool ModifierDown(ref InputBindingCompositeContext context)
        {
            bool down = context.ReadValueAsButton(modifier);
            if (down && !overrideModifiersNeedToBePressedFirst)
                return context.GetPressTime(binding) >= context.GetPressTime(modifier);
            return down;
        }

        public override object ReadValueAsObject(ref InputBindingCompositeContext context) => ReadValueData(ref context).ToObject();

        internal override InputValueData ReadValueData(ref InputBindingCompositeContext context)
        {
            var v = context.ReadPart(binding, out _);
            if (!ModifierDown(ref context)) v.Clear();
            return v;
        }

        public override float EvaluateMagnitude(ref InputBindingCompositeContext context) => ModifierDown(ref context) ? context.EvaluateMagnitude(binding) : 0f;
    }

    /// <summary>TwoModifiers: modifier1 + modifier2 를 누른 채 binding</summary>
    public class TwoModifiersComposite : InputBindingComposite
    {
        [InputControl(layout = "Button")] public int modifier1;
        [InputControl(layout = "Button")] public int modifier2;
        [InputControl] public int binding;
        public bool overrideModifiersNeedToBePressedFirst;

        public override Type valueType => typeof(float);
        public override int valueSizeInBytes => 4;

        bool ModifiersDown(ref InputBindingCompositeContext context)
        {
            bool down = context.ReadValueAsButton(modifier1) && context.ReadValueAsButton(modifier2);
            if (down && !overrideModifiersNeedToBePressedFirst)
            {
                double b = context.GetPressTime(binding);
                down = b >= context.GetPressTime(modifier1) && b >= context.GetPressTime(modifier2);
            }
            return down;
        }

        public override object ReadValueAsObject(ref InputBindingCompositeContext context) => ReadValueData(ref context).ToObject();

        internal override InputValueData ReadValueData(ref InputBindingCompositeContext context)
        {
            var v = context.ReadPart(binding, out _);
            if (!ModifiersDown(ref context)) v.Clear();
            return v;
        }

        public override float EvaluateMagnitude(ref InputBindingCompositeContext context) => ModifiersDown(ref context) ? context.EvaluateMagnitude(binding) : 0f;
    }
}
