using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#pragma warning disable CS8981 // 소문자 형식 이름 (Unity.Mathematics 와 같게)

namespace NovaEngine.Mathematics
{
    /// <summary>오일러 각을 합치는 순서 (Unity.Mathematics.math.RotationOrder) — ZXY 가 Unity 기본 (Transform 과 같음)</summary>
    public enum RotationOrder : byte { XYZ, XZY, YXZ, YZX, ZXY, ZYX, Default = ZXY }

    /// <summary>Unity.Mathematics.quaternion — 각은 라디안, value = (x, y, z, w)</summary>
    [StructLayout(LayoutKind.Sequential)]
    public partial struct quaternion : IEquatable<quaternion>, IFormattable
    {
        public float4 value;

        public static readonly quaternion identity = new quaternion(0f, 0f, 0f, 1f);

        [MethodImpl(MethodImplOptions.AggressiveInlining)] public quaternion(float x, float y, float z, float w) { value = new float4(x, y, z, w); }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public quaternion(float4 value) { this.value = value; }

        /// <summary>회전 행렬 (열 = 오른쪽 · 위 · 앞, 정규 직교) 에서</summary>
        public quaternion(float3x3 m)
        {
            float3 u = m.c0, v = m.c1, w = m.c2;
            float trace = u.x + v.y + w.z;
            if (trace > 0f)
            {
                float s = MathF.Sqrt(trace + 1f) * 2f;
                value = new float4((v.z - w.y) / s, (w.x - u.z) / s, (u.y - v.x) / s, 0.25f * s);
            }
            else if (u.x > v.y && u.x > w.z)
            {
                float s = MathF.Sqrt(1f + u.x - v.y - w.z) * 2f;
                value = new float4(0.25f * s, (v.x + u.y) / s, (w.x + u.z) / s, (v.z - w.y) / s);
            }
            else if (v.y > w.z)
            {
                float s = MathF.Sqrt(1f + v.y - u.x - w.z) * 2f;
                value = new float4((v.x + u.y) / s, 0.25f * s, (w.y + v.z) / s, (w.x - u.z) / s);
            }
            else
            {
                float s = MathF.Sqrt(1f + w.z - u.x - v.y) * 2f;
                value = new float4((w.x + u.z) / s, (w.y + v.z) / s, 0.25f * s, (u.y - v.x) / s);
            }
            value = math.normalize(value);
        }
        public quaternion(float4x4 m) : this(new float3x3(m.c0.xyz, m.c1.xyz, m.c2.xyz)) { }

        // NovaEngine.Quaternion 과 (Unity 처럼 암묵)
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static implicit operator quaternion(global::NovaEngine.Quaternion q) => new quaternion(q.x, q.y, q.z, q.w);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static implicit operator global::NovaEngine.Quaternion(quaternion q) => new global::NovaEngine.Quaternion(q.value.x, q.value.y, q.value.z, q.value.w);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static implicit operator quaternion(float4 v) => new quaternion(v);

        /// <summary>축 (정규화된) 둘레로 angle 라디안</summary>
        public static quaternion AxisAngle(float3 axis, float angle)
        {
            float h = 0.5f * angle, s = MathF.Sin(h);
            return new quaternion(new float4(axis * s, MathF.Cos(h)));
        }
        public static quaternion RotateX(float angle) { float h = 0.5f * angle; return new quaternion(MathF.Sin(h), 0f, 0f, MathF.Cos(h)); }
        public static quaternion RotateY(float angle) { float h = 0.5f * angle; return new quaternion(0f, MathF.Sin(h), 0f, MathF.Cos(h)); }
        public static quaternion RotateZ(float angle) { float h = 0.5f * angle; return new quaternion(0f, 0f, MathF.Sin(h), MathF.Cos(h)); }

        // 오일러 (라디안): 이름의 순서대로 돌린다 — EulerZXY = Z 먼저, 그다음 X, 마지막 Y (q = qy * qx * qz)
        public static quaternion EulerXYZ(float3 xyz) => Compose(xyz, RotationOrder.XYZ);
        public static quaternion EulerXZY(float3 xyz) => Compose(xyz, RotationOrder.XZY);
        public static quaternion EulerYXZ(float3 xyz) => Compose(xyz, RotationOrder.YXZ);
        public static quaternion EulerYZX(float3 xyz) => Compose(xyz, RotationOrder.YZX);
        public static quaternion EulerZXY(float3 xyz) => Compose(xyz, RotationOrder.ZXY);
        public static quaternion EulerZYX(float3 xyz) => Compose(xyz, RotationOrder.ZYX);
        public static quaternion EulerXYZ(float x, float y, float z) => EulerXYZ(new float3(x, y, z));
        public static quaternion EulerZXY(float x, float y, float z) => EulerZXY(new float3(x, y, z));
        public static quaternion Euler(float3 xyz, RotationOrder order = RotationOrder.ZXY) => Compose(xyz, order);
        public static quaternion Euler(float x, float y, float z, RotationOrder order = RotationOrder.ZXY) => Compose(new float3(x, y, z), order);

        static quaternion Compose(float3 xyz, RotationOrder order)
        {
            quaternion qx = RotateX(xyz.x), qy = RotateY(xyz.y), qz = RotateZ(xyz.z);
            // 먼저 돌리는 것이 오른쪽
            switch (order)
            {
                case RotationOrder.XYZ: return math.mul(qz, math.mul(qy, qx));
                case RotationOrder.XZY: return math.mul(qy, math.mul(qz, qx));
                case RotationOrder.YXZ: return math.mul(qz, math.mul(qx, qy));
                case RotationOrder.YZX: return math.mul(qx, math.mul(qz, qy));
                case RotationOrder.ZYX: return math.mul(qx, math.mul(qy, qz));
                default: return math.mul(qy, math.mul(qx, qz));
            }
        }

        /// <summary>앞 (+Z) 이 forward, 위가 up 쪽이 되는 회전 (둘 다 정규화 · 평행 아님 가정)</summary>
        public static quaternion LookRotation(float3 forward, float3 up)
        {
            float3 t = math.normalize(math.cross(up, forward));
            return new quaternion(new float3x3(t, math.cross(forward, t), forward));
        }
        /// <summary>LookRotation — 길이 0 · 평행이면 identity</summary>
        public static quaternion LookRotationSafe(float3 forward, float3 up)
        {
            float fl = math.lengthsq(forward), ul = math.lengthsq(up);
            if (fl < 1e-12f || ul < 1e-12f) return identity;
            float3 f = forward * (1f / MathF.Sqrt(fl)), u = up * (1f / MathF.Sqrt(ul));
            float3 t = math.cross(u, f);
            float tl = math.lengthsq(t);
            if (tl < 1e-12f) return identity;
            t *= 1f / MathF.Sqrt(tl);
            return new quaternion(new float3x3(t, math.cross(f, t), f));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] public bool Equals(quaternion o) => value.Equals(o.value);
        public override bool Equals(object o) => o is quaternion q && Equals(q);
        public override int GetHashCode() => value.GetHashCode();
        public override string ToString() => string.Format("quaternion({0}f, {1}f, {2}f, {3}f)", value.x, value.y, value.z, value.w);
        public string ToString(string format, IFormatProvider provider) => string.Format("quaternion({0}f, {1}f, {2}f, {3}f)", value.x.ToString(format, provider), value.y.ToString(format, provider), value.z.ToString(format, provider), value.w.ToString(format, provider));
    }

    public static partial class math
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static quaternion quaternion(float x, float y, float z, float w) => new quaternion(x, y, z, w);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static quaternion quaternion(float4 v) => new quaternion(v);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static quaternion quaternion(float3x3 m) => new quaternion(m);

        /// <summary>b 를 먼저 돌리고 그다음 a (Quaternion 의 a * b 와 같음)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static quaternion mul(quaternion a, quaternion b)
        {
            float4 p = a.value, q = b.value;
            return new quaternion(
                p.w * q.x + p.x * q.w + p.y * q.z - p.z * q.y,
                p.w * q.y - p.x * q.z + p.y * q.w + p.z * q.x,
                p.w * q.z + p.x * q.y - p.y * q.x + p.z * q.w,
                p.w * q.w - p.x * q.x - p.y * q.y - p.z * q.z);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3 mul(quaternion q, float3 v) => rotate(q, v);

        /// <summary>v 를 q 로 돌린다 (q 는 정규화)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 rotate(quaternion q, float3 v)
        {
            float3 u = q.value.xyz;
            float3 t = 2f * cross(u, v);
            return v + q.value.w * t + cross(u, t);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static quaternion conjugate(quaternion q) => new quaternion(-q.value.x, -q.value.y, -q.value.z, q.value.w);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static quaternion inverse(quaternion q) { float4 x = q.value; return new quaternion(new float4(-x.x, -x.y, -x.z, x.w) * (1f / dot(x, x))); }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float dot(quaternion a, quaternion b) => dot(a.value, b.value);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float length(quaternion q) => length(q.value);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float lengthsq(quaternion q) => lengthsq(q.value);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static quaternion normalize(quaternion q) => new quaternion(normalize(q.value));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static quaternion normalizesafe(quaternion q) => normalizesafe(q, Mathematics.quaternion.identity);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static quaternion normalizesafe(quaternion q, quaternion defaultvalue)
        {
            float l = dot(q.value, q.value);
            return l > FLT_MIN_NORMAL ? new quaternion(q.value * (1f / MathF.Sqrt(l))) : defaultvalue;
        }
        /// <summary>짧은 쪽으로 선형 보간 후 정규화</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static quaternion nlerp(quaternion q1, quaternion q2, float t)
        {
            float d = dot(q1.value, q2.value);
            float4 b = d < 0f ? -q2.value : q2.value;
            return normalize(new quaternion(q1.value + (b - q1.value) * t));
        }
        /// <summary>구면 보간 (짧은 쪽)</summary>
        public static quaternion slerp(quaternion q1, quaternion q2, float t)
        {
            float d = dot(q1.value, q2.value);
            float4 b = q2.value;
            if (d < 0f) { d = -d; b = -b; }
            if (d > 0.9995f) return nlerp(q1, new quaternion(b), t);
            float angle = MathF.Acos(d), s = 1f / MathF.Sin(angle);
            float w1 = MathF.Sin((1f - t) * angle) * s, w2 = MathF.Sin(t * angle) * s;
            return new quaternion(q1.value * w1 + b * w2);
        }
        /// <summary>두 회전 사이 각 (라디안)</summary>
        public static float angle(quaternion q1, quaternion q2)
        {
            float d = MathF.Abs(dot(q1.value, q2.value));
            return 2f * MathF.Acos(MathF.Min(d, 1f));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3 forward(quaternion q) => rotate(q, new float3(0f, 0f, 1f));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3 up(quaternion q) => rotate(q, new float3(0f, 1f, 0f));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3 right(quaternion q) => rotate(q, new float3(1f, 0f, 0f));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint hash(quaternion q) => hash(q.value);
        // 축 방향 (Unity 와 같이 왼손 · +Y 위 · +Z 앞)
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3 up() => new float3(0f, 1f, 0f);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3 down() => new float3(0f, -1f, 0f);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3 forward() => new float3(0f, 0f, 1f);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3 back() => new float3(0f, 0f, -1f);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3 left() => new float3(-1f, 0f, 0f);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3 right() => new float3(1f, 0f, 0f);
    }
}
