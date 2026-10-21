using System;
using NovaEngine.InputSystem.Controls;

namespace NovaEngine.InputSystem
{
    /// <summary>
    /// Unity 의 InputProcessor — 값을 바꾸는 단계 (데드존 · 뒤집기 · 배율 …). 컨트롤 레이아웃 · 바인딩 · 동작에 "이름(인자=값)" 으로 붙는다.
    /// 직접 만들기: class MyProcessor : InputProcessor&lt;float&gt; + InputSystem.RegisterProcessor&lt;MyProcessor&gt;()
    /// </summary>
    public abstract class InputProcessor
    {
        public abstract object ProcessAsObject(object value, InputControl control);
        internal abstract Type valueType { get; }
    }

    public abstract class InputProcessor<TValue> : InputProcessor where TValue : struct
    {
        public abstract TValue Process(TValue value, InputControl control);
        public override object ProcessAsObject(object value, InputControl control) => value is TValue v ? Process(v, control) : value;
        internal override Type valueType => typeof(TValue);
    }
}

namespace NovaEngine.InputSystem.Processors
{
    /// <summary>축 데드존: |값| &lt; min → 0, &gt; max → ±1, 사이는 다시 펼친다 (0 = InputSettings 기본값)</summary>
    public class AxisDeadzoneProcessor : InputProcessor<float>
    {
        public float min;
        public float max;
        public float minOrDefault => min == 0f ? InputSystem.settings.defaultDeadzoneMin : min;
        public float maxOrDefault => max == 0f ? InputSystem.settings.defaultDeadzoneMax : max;

        public override float Process(float value, InputControl control)
        {
            float lo = minOrDefault, hi = maxOrDefault, abs = Math.Abs(value);
            if (abs < lo) return 0f;
            if (abs > hi) return Math.Sign(value);
            return Math.Sign(value) * ((abs - lo) / (hi - lo));
        }
        public override string ToString() => $"AxisDeadzone(min={minOrDefault},max={maxOrDefault})";
    }

    /// <summary>스틱 데드존 (원형): 길이 &lt; min → 0, &gt; max → 1, 방향은 그대로</summary>
    public class StickDeadzoneProcessor : InputProcessor<Vector2>
    {
        public float min;
        public float max;
        public float minOrDefault => min == 0f ? InputSystem.settings.defaultDeadzoneMin : min;
        public float maxOrDefault => max == 0f ? InputSystem.settings.defaultDeadzoneMax : max;

        public override Vector2 Process(Vector2 value, InputControl control)
        {
            float mag = value.magnitude;
            float adjusted = GetDeadZoneAdjustedValue(mag);
            if (adjusted == 0f) return Vector2.zero;
            return value * (adjusted / mag);
        }

        float GetDeadZoneAdjustedValue(float value)
        {
            float lo = minOrDefault, hi = maxOrDefault, abs = Math.Abs(value);
            if (abs < lo) return 0f;
            if (abs > hi) return Math.Sign(value);
            return Math.Sign(value) * ((abs - lo) / (hi - lo));
        }
        public override string ToString() => $"StickDeadzone(min={minOrDefault},max={maxOrDefault})";
    }

    public class ClampProcessor : InputProcessor<float>
    {
        public float min;
        public float max;
        public override float Process(float value, InputControl control) => Math.Clamp(value, min, max);
    }

    public class InvertProcessor : InputProcessor<float>
    {
        public override float Process(float value, InputControl control) => -value;
    }

    public class InvertVector2Processor : InputProcessor<Vector2>
    {
        public bool invertX = true;
        public bool invertY = true;
        public override Vector2 Process(Vector2 value, InputControl control) => new Vector2(invertX ? -value.x : value.x, invertY ? -value.y : value.y);
    }

    public class InvertVector3Processor : InputProcessor<Vector3>
    {
        public bool invertX = true;
        public bool invertY = true;
        public bool invertZ = true;
        public override Vector3 Process(Vector3 value, InputControl control) => new Vector3(invertX ? -value.x : value.x, invertY ? -value.y : value.y, invertZ ? -value.z : value.z);
    }

    /// <summary>min ~ max → 0 ~ 1 (zero 가 min 보다 크면 -1 ~ 1)</summary>
    public class NormalizeProcessor : InputProcessor<float>
    {
        public float min;
        public float max;
        public float zero;
        public override float Process(float value, InputControl control) => Normalize(value, min, max, zero);

        public static float Normalize(float value, float min, float max, float zero)
        {
            if (zero < min) zero = min;
            if (Math.Abs(value - min) < 1e-6f) return min < zero ? -1f : 0f;
            if (max == min) return 0f;
            float percentage = (value - min) / (max - min);
            return min < zero ? 2f * percentage - 1f : percentage;
        }
    }

    public class NormalizeVector2Processor : InputProcessor<Vector2>
    {
        public override Vector2 Process(Vector2 value, InputControl control) => value.normalized;
    }

    public class NormalizeVector3Processor : InputProcessor<Vector3>
    {
        public override Vector3 Process(Vector3 value, InputControl control) => value.normalized;
    }

    public class ScaleProcessor : InputProcessor<float>
    {
        public float factor = 1f;
        public override float Process(float value, InputControl control) => value * factor;
    }

    public class ScaleVector2Processor : InputProcessor<Vector2>
    {
        public float x = 1f;
        public float y = 1f;
        public override Vector2 Process(Vector2 value, InputControl control) => new Vector2(value.x * x, value.y * y);
    }

    public class ScaleVector3Processor : InputProcessor<Vector3>
    {
        public float x = 1f;
        public float y = 1f;
        public float z = 1f;
        public override Vector3 Process(Vector3 value, InputControl control) => new Vector3(value.x * x, value.y * y, value.z * z);
    }

    /// <summary>화면 방향 보정 (Unity: compensateForScreenOrientation 일 때만) — PC 는 그대로</summary>
    public class CompensateDirectionProcessor : InputProcessor<Vector3>
    {
        public override Vector3 Process(Vector3 value, InputControl control) => value;
    }
}
