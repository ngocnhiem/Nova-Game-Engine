using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#pragma warning disable CS8981 // 소문자 형식 이름 (Unity.Mathematics 와 같게)

namespace NovaEngine.Mathematics
{
    // Unity.Mathematics 와 같이 열 우선 (c0, c1, ... = 열). * 는 성분 곱, 행렬 곱은 math.mul.
    // 스칼라 생성자는 행 순서로 적는다 (float3x3(m00, m01, m02, m10, ...)).

    /// <summary>Unity.Mathematics.float2x2</summary>
    [StructLayout(LayoutKind.Sequential)]
    public partial struct float2x2 : IEquatable<float2x2>
    {
        public float2 c0, c1;
        public static readonly float2x2 identity = new float2x2(1f, 0f, 0f, 1f);
        public static readonly float2x2 zero = default;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float2x2(float2 c0, float2 c1) { this.c0 = c0; this.c1 = c1; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float2x2(float m00, float m01, float m10, float m11) { c0 = new float2(m00, m10); c1 = new float2(m01, m11); }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float2x2(float v) { c0 = v; c1 = v; }
        public static float2x2 Rotate(float angle) { float s = MathF.Sin(angle), c = MathF.Cos(angle); return new float2x2(c, -s, s, c); }
        public static float2x2 Scale(float2 v) => new float2x2(v.x, 0f, 0f, v.y);
        public unsafe ref float2 this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { if ((uint)index >= 2u) throw new ArgumentOutOfRangeException(nameof(index)); fixed (float2* p = &c0) return ref p[index]; }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float2x2 operator +(float2x2 a, float2x2 b) => new float2x2(a.c0 + b.c0, a.c1 + b.c1);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float2x2 operator -(float2x2 a, float2x2 b) => new float2x2(a.c0 - b.c0, a.c1 - b.c1);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float2x2 operator *(float2x2 a, float2x2 b) => new float2x2(a.c0 * b.c0, a.c1 * b.c1);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float2x2 operator *(float2x2 a, float b) => new float2x2(a.c0 * b, a.c1 * b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float2x2 operator *(float b, float2x2 a) => new float2x2(a.c0 * b, a.c1 * b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float2x2 operator /(float2x2 a, float b) => new float2x2(a.c0 / b, a.c1 / b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float2x2 operator -(float2x2 a) => new float2x2(-a.c0, -a.c1);
        public bool Equals(float2x2 o) => c0.Equals(o.c0) && c1.Equals(o.c1);
        public override bool Equals(object o) => o is float2x2 m && Equals(m);
        public override int GetHashCode() => HashCode.Combine(c0, c1);
        public override string ToString() => string.Format("float2x2({0}f, {1}f, {2}f, {3}f)", c0.x, c1.x, c0.y, c1.y);
    }

    /// <summary>Unity.Mathematics.float3x3</summary>
    [StructLayout(LayoutKind.Sequential)]
    public partial struct float3x3 : IEquatable<float3x3>
    {
        public float3 c0, c1, c2;
        public static readonly float3x3 identity = new float3x3(1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f);
        public static readonly float3x3 zero = default;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float3x3(float3 c0, float3 c1, float3 c2) { this.c0 = c0; this.c1 = c1; this.c2 = c2; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float3x3(float m00, float m01, float m02, float m10, float m11, float m12, float m20, float m21, float m22)
        {
            c0 = new float3(m00, m10, m20); c1 = new float3(m01, m11, m21); c2 = new float3(m02, m12, m22);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float3x3(float v) { c0 = v; c1 = v; c2 = v; }
        /// <summary>회전 행렬 (q 는 정규화)</summary>
        public float3x3(quaternion q)
        {
            float4 v = q.value;
            float x2 = v.x + v.x, y2 = v.y + v.y, z2 = v.z + v.z;
            float xx = v.x * x2, yy = v.y * y2, zz = v.z * z2, xy = v.x * y2, xz = v.x * z2, yz = v.y * z2;
            float wx = v.w * x2, wy = v.w * y2, wz = v.w * z2;
            c0 = new float3(1f - (yy + zz), xy + wz, xz - wy);
            c1 = new float3(xy - wz, 1f - (xx + zz), yz + wx);
            c2 = new float3(xz + wy, yz - wx, 1f - (xx + yy));
        }
        public float3x3(float4x4 m) { c0 = m.c0.xyz; c1 = m.c1.xyz; c2 = m.c2.xyz; }

        public static float3x3 AxisAngle(float3 axis, float angle) => new float3x3(quaternion.AxisAngle(axis, angle));
        public static float3x3 RotateX(float angle) { float s = MathF.Sin(angle), c = MathF.Cos(angle); return new float3x3(1f, 0f, 0f, 0f, c, -s, 0f, s, c); }
        public static float3x3 RotateY(float angle) { float s = MathF.Sin(angle), c = MathF.Cos(angle); return new float3x3(c, 0f, s, 0f, 1f, 0f, -s, 0f, c); }
        public static float3x3 RotateZ(float angle) { float s = MathF.Sin(angle), c = MathF.Cos(angle); return new float3x3(c, -s, 0f, s, c, 0f, 0f, 0f, 1f); }
        public static float3x3 Euler(float3 xyz, RotationOrder order = RotationOrder.ZXY) => new float3x3(quaternion.Euler(xyz, order));
        public static float3x3 EulerZXY(float3 xyz) => new float3x3(quaternion.EulerZXY(xyz));
        public static float3x3 EulerXYZ(float3 xyz) => new float3x3(quaternion.EulerXYZ(xyz));
        public static float3x3 Scale(float3 v) => new float3x3(v.x, 0f, 0f, 0f, v.y, 0f, 0f, 0f, v.z);
        public static float3x3 Scale(float s) => Scale(new float3(s));
        public static float3x3 LookRotation(float3 forward, float3 up)
        {
            float3 t = math.normalize(math.cross(up, forward));
            return new float3x3(t, math.cross(forward, t), forward);
        }
        public static float3x3 LookRotationSafe(float3 forward, float3 up) => new float3x3(quaternion.LookRotationSafe(forward, up));

        public unsafe ref float3 this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { if ((uint)index >= 3u) throw new ArgumentOutOfRangeException(nameof(index)); fixed (float3* p = &c0) return ref p[index]; }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3x3 operator +(float3x3 a, float3x3 b) => new float3x3(a.c0 + b.c0, a.c1 + b.c1, a.c2 + b.c2);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3x3 operator -(float3x3 a, float3x3 b) => new float3x3(a.c0 - b.c0, a.c1 - b.c1, a.c2 - b.c2);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3x3 operator *(float3x3 a, float3x3 b) => new float3x3(a.c0 * b.c0, a.c1 * b.c1, a.c2 * b.c2);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3x3 operator *(float3x3 a, float b) => new float3x3(a.c0 * b, a.c1 * b, a.c2 * b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3x3 operator *(float b, float3x3 a) => new float3x3(a.c0 * b, a.c1 * b, a.c2 * b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3x3 operator /(float3x3 a, float b) => new float3x3(a.c0 / b, a.c1 / b, a.c2 / b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3x3 operator -(float3x3 a) => new float3x3(-a.c0, -a.c1, -a.c2);
        public bool Equals(float3x3 o) => c0.Equals(o.c0) && c1.Equals(o.c1) && c2.Equals(o.c2);
        public override bool Equals(object o) => o is float3x3 m && Equals(m);
        public override int GetHashCode() => HashCode.Combine(c0, c1, c2);
        public override string ToString() => string.Format("float3x3({0}f, {1}f, {2}f,  {3}f, {4}f, {5}f,  {6}f, {7}f, {8}f)", c0.x, c1.x, c2.x, c0.y, c1.y, c2.y, c0.z, c1.z, c2.z);
    }

    /// <summary>Unity.Mathematics.float4x4 — 위치 · 회전 · 크기 (TRS) 변환</summary>
    [StructLayout(LayoutKind.Sequential)]
    public partial struct float4x4 : IEquatable<float4x4>
    {
        public float4 c0, c1, c2, c3;
        public static readonly float4x4 identity = new float4x4(new float4(1f, 0f, 0f, 0f), new float4(0f, 1f, 0f, 0f), new float4(0f, 0f, 1f, 0f), new float4(0f, 0f, 0f, 1f));
        public static readonly float4x4 zero = default;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float4x4(float4 c0, float4 c1, float4 c2, float4 c3) { this.c0 = c0; this.c1 = c1; this.c2 = c2; this.c3 = c3; }
        public float4x4(float m00, float m01, float m02, float m03, float m10, float m11, float m12, float m13,
                        float m20, float m21, float m22, float m23, float m30, float m31, float m32, float m33)
        {
            c0 = new float4(m00, m10, m20, m30); c1 = new float4(m01, m11, m21, m31);
            c2 = new float4(m02, m12, m22, m32); c3 = new float4(m03, m13, m23, m33);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float4x4(float v) { c0 = v; c1 = v; c2 = v; c3 = v; }
        public float4x4(float3x3 rotation, float3 translation)
        {
            c0 = new float4(rotation.c0, 0f); c1 = new float4(rotation.c1, 0f); c2 = new float4(rotation.c2, 0f); c3 = new float4(translation, 1f);
        }
        public float4x4(quaternion rotation, float3 translation) : this(new float3x3(rotation), translation) { }

        /// <summary>위치 · 회전 · 크기 (Transform 의 localToWorld 와 같은 순서: 크기 → 회전 → 위치)</summary>
        public static float4x4 TRS(float3 translation, quaternion rotation, float3 scale)
        {
            float3x3 r = new float3x3(rotation);
            return new float4x4(new float4(r.c0 * scale.x, 0f), new float4(r.c1 * scale.y, 0f), new float4(r.c2 * scale.z, 0f), new float4(translation, 1f));
        }
        public static float4x4 Translate(float3 v) => new float4x4(float3x3.identity, v);
        public static float4x4 Scale(float3 v) => new float4x4(new float4(v.x, 0f, 0f, 0f), new float4(0f, v.y, 0f, 0f), new float4(0f, 0f, v.z, 0f), new float4(0f, 0f, 0f, 1f));
        public static float4x4 Scale(float s) => Scale(new float3(s));
        public static float4x4 AxisAngle(float3 axis, float angle) => new float4x4(float3x3.AxisAngle(axis, angle), float3.zero);
        public static float4x4 RotateX(float angle) => new float4x4(float3x3.RotateX(angle), float3.zero);
        public static float4x4 RotateY(float angle) => new float4x4(float3x3.RotateY(angle), float3.zero);
        public static float4x4 RotateZ(float angle) => new float4x4(float3x3.RotateZ(angle), float3.zero);
        public static float4x4 Euler(float3 xyz, RotationOrder order = RotationOrder.ZXY) => new float4x4(float3x3.Euler(xyz, order), float3.zero);
        /// <summary>eye 에 서서 target 을 보는 물체의 변환 (월드 ← 로컬, +Z 가 target 쪽)</summary>
        public static float4x4 LookAt(float3 eye, float3 target, float3 up) => new float4x4(float3x3.LookRotation(math.normalize(target - eye), up), eye);

        public unsafe ref float4 this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { if ((uint)index >= 4u) throw new ArgumentOutOfRangeException(nameof(index)); fixed (float4* p = &c0) return ref p[index]; }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float4x4 operator +(float4x4 a, float4x4 b) => new float4x4(a.c0 + b.c0, a.c1 + b.c1, a.c2 + b.c2, a.c3 + b.c3);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float4x4 operator -(float4x4 a, float4x4 b) => new float4x4(a.c0 - b.c0, a.c1 - b.c1, a.c2 - b.c2, a.c3 - b.c3);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float4x4 operator *(float4x4 a, float4x4 b) => new float4x4(a.c0 * b.c0, a.c1 * b.c1, a.c2 * b.c2, a.c3 * b.c3);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float4x4 operator *(float4x4 a, float b) => new float4x4(a.c0 * b, a.c1 * b, a.c2 * b, a.c3 * b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float4x4 operator *(float b, float4x4 a) => new float4x4(a.c0 * b, a.c1 * b, a.c2 * b, a.c3 * b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float4x4 operator /(float4x4 a, float b) => new float4x4(a.c0 / b, a.c1 / b, a.c2 / b, a.c3 / b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float4x4 operator -(float4x4 a) => new float4x4(-a.c0, -a.c1, -a.c2, -a.c3);
        public bool Equals(float4x4 o) => c0.Equals(o.c0) && c1.Equals(o.c1) && c2.Equals(o.c2) && c3.Equals(o.c3);
        public override bool Equals(object o) => o is float4x4 m && Equals(m);
        public override int GetHashCode() => HashCode.Combine(c0, c1, c2, c3);
        public override string ToString() => string.Format("float4x4({0}, {1}, {2}, {3})", c0, c1, c2, c3);
    }

    public static partial class math
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float2x2 float2x2(float2 c0, float2 c1) => new float2x2(c0, c1);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3x3 float3x3(float3 c0, float3 c1, float3 c2) => new float3x3(c0, c1, c2);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3x3 float3x3(quaternion q) => new float3x3(q);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float4x4 float4x4(float4 c0, float4 c1, float4 c2, float4 c3) => new float4x4(c0, c1, c2, c3);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float4x4 float4x4(quaternion rotation, float3 translation) => new float4x4(rotation, translation);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float4x4 float4x4(float3x3 rotation, float3 translation) => new float4x4(rotation, translation);

        // 행렬 × 열 벡터 · 행 벡터 × 행렬 · 행렬 × 행렬
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float2 mul(float2x2 a, float2 b) => a.c0 * b.x + a.c1 * b.y;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3 mul(float3x3 a, float3 b) => a.c0 * b.x + a.c1 * b.y + a.c2 * b.z;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float4 mul(float4x4 a, float4 b) => a.c0 * b.x + a.c1 * b.y + a.c2 * b.z + a.c3 * b.w;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float2 mul(float2 a, float2x2 b) => new float2(dot(a, b.c0), dot(a, b.c1));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3 mul(float3 a, float3x3 b) => new float3(dot(a, b.c0), dot(a, b.c1), dot(a, b.c2));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float4 mul(float4 a, float4x4 b) => new float4(dot(a, b.c0), dot(a, b.c1), dot(a, b.c2), dot(a, b.c3));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float2x2 mul(float2x2 a, float2x2 b) => new float2x2(mul(a, b.c0), mul(a, b.c1));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3x3 mul(float3x3 a, float3x3 b) => new float3x3(mul(a, b.c0), mul(a, b.c1), mul(a, b.c2));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float4x4 mul(float4x4 a, float4x4 b) => new float4x4(mul(a, b.c0), mul(a, b.c1), mul(a, b.c2), mul(a, b.c3));

        /// <summary>점 변환 (w = 1, 원근 나눗셈 없음)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3 transform(float4x4 a, float3 b) => (a.c0 * b.x + a.c1 * b.y + a.c2 * b.z + a.c3).xyz;
        /// <summary>방향 변환 (w = 0)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float3 rotate(float4x4 a, float3 b) => (a.c0 * b.x + a.c1 * b.y + a.c2 * b.z).xyz;

        public static float2x2 transpose(float2x2 v) => new float2x2(v.c0.x, v.c0.y, v.c1.x, v.c1.y);
        public static float3x3 transpose(float3x3 v) => new float3x3(v.c0.x, v.c0.y, v.c0.z, v.c1.x, v.c1.y, v.c1.z, v.c2.x, v.c2.y, v.c2.z);
        public static float4x4 transpose(float4x4 v) => new float4x4(
            v.c0.x, v.c0.y, v.c0.z, v.c0.w, v.c1.x, v.c1.y, v.c1.z, v.c1.w,
            v.c2.x, v.c2.y, v.c2.z, v.c2.w, v.c3.x, v.c3.y, v.c3.z, v.c3.w);

        public static float determinant(float2x2 m) => m.c0.x * m.c1.y - m.c1.x * m.c0.y;
        public static float determinant(float3x3 m) => dot(m.c0, cross(m.c1, m.c2));
        public static float determinant(float4x4 m)
        {
            float4 c0 = m.c0, c1 = m.c1, c2 = m.c2, c3 = m.c3;
            float s0 = c0.x * c1.y - c1.x * c0.y, s1 = c0.x * c2.y - c2.x * c0.y, s2 = c0.x * c3.y - c3.x * c0.y;
            float s3 = c1.x * c2.y - c2.x * c1.y, s4 = c1.x * c3.y - c3.x * c1.y, s5 = c2.x * c3.y - c3.x * c2.y;
            float t5 = c2.z * c3.w - c3.z * c2.w, t4 = c1.z * c3.w - c3.z * c1.w, t3 = c1.z * c2.w - c2.z * c1.w;
            float t2 = c0.z * c3.w - c3.z * c0.w, t1 = c0.z * c2.w - c2.z * c0.w, t0 = c0.z * c1.w - c1.z * c0.w;
            return s0 * t5 - s1 * t4 + s2 * t3 + s3 * t2 - s4 * t1 + s5 * t0;
        }

        public static float2x2 inverse(float2x2 m)
        {
            float d = 1f / determinant(m);
            return new float2x2(m.c1.y * d, -m.c1.x * d, -m.c0.y * d, m.c0.x * d);
        }
        public static float3x3 inverse(float3x3 m)
        {
            float3 r0 = cross(m.c1, m.c2), r1 = cross(m.c2, m.c0), r2 = cross(m.c0, m.c1);
            float d = 1f / dot(m.c0, r0);
            return transpose(new float3x3(r0 * d, r1 * d, r2 * d));
        }
        public static float4x4 inverse(float4x4 m)
        {
            // 열 우선 c[col][row] — 여인수 전개
            float a00 = m.c0.x, a10 = m.c0.y, a20 = m.c0.z, a30 = m.c0.w;
            float a01 = m.c1.x, a11 = m.c1.y, a21 = m.c1.z, a31 = m.c1.w;
            float a02 = m.c2.x, a12 = m.c2.y, a22 = m.c2.z, a32 = m.c2.w;
            float a03 = m.c3.x, a13 = m.c3.y, a23 = m.c3.z, a33 = m.c3.w;
            float b00 = a00 * a11 - a01 * a10, b01 = a00 * a12 - a02 * a10, b02 = a00 * a13 - a03 * a10;
            float b03 = a01 * a12 - a02 * a11, b04 = a01 * a13 - a03 * a11, b05 = a02 * a13 - a03 * a12;
            float b06 = a20 * a31 - a21 * a30, b07 = a20 * a32 - a22 * a30, b08 = a20 * a33 - a23 * a30;
            float b09 = a21 * a32 - a22 * a31, b10 = a21 * a33 - a23 * a31, b11 = a22 * a33 - a23 * a32;
            float det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
            float id = 1f / det;
            return new float4x4(
                (a11 * b11 - a12 * b10 + a13 * b09) * id, (-a01 * b11 + a02 * b10 - a03 * b09) * id, (a31 * b05 - a32 * b04 + a33 * b03) * id, (-a21 * b05 + a22 * b04 - a23 * b03) * id,
                (-a10 * b11 + a12 * b08 - a13 * b07) * id, (a00 * b11 - a02 * b08 + a03 * b07) * id, (-a30 * b05 + a32 * b02 - a33 * b01) * id, (a20 * b05 - a22 * b02 + a23 * b01) * id,
                (a10 * b10 - a11 * b08 + a13 * b06) * id, (-a00 * b10 + a01 * b08 - a03 * b06) * id, (a30 * b04 - a31 * b02 + a33 * b00) * id, (-a20 * b04 + a21 * b02 - a23 * b00) * id,
                (-a10 * b09 + a11 * b07 - a12 * b06) * id, (a00 * b09 - a01 * b07 + a02 * b06) * id, (-a30 * b03 + a31 * b01 - a32 * b00) * id, (a20 * b03 - a21 * b01 + a22 * b00) * id);
        }
        /// <summary>회전 + 위치뿐인 (크기 1) 변환의 빠른 역</summary>
        public static float4x4 fastinverse(float4x4 m)
        {
            float3x3 r = transpose(new float3x3(m.c0.xyz, m.c1.xyz, m.c2.xyz));
            return new float4x4(r, -mul(r, m.c3.xyz));
        }
        /// <summary>회전 행렬 → quaternion</summary>
        public static quaternion rotation(float3x3 m) => new quaternion(m);
    }
}
