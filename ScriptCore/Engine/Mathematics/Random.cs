using System;
using System.Runtime.CompilerServices;

#pragma warning disable CS8981 // 소문자 형식 이름 (Unity.Mathematics 와 같게)

namespace NovaEngine.Mathematics
{
    /// <summary>
    /// Unity.Mathematics.Random — 값 형식 xorshift32 난수. 잡 (Job) 안에서도 쓸 수 있다 (공유 상태 없음).
    /// 일마다 다른 수가 필요하면 Random.CreateFromIndex(index) 로 만든다. 씨앗 0 은 쓸 수 없다.
    /// </summary>
    public struct Random
    {
        public uint state;

        public Random(uint seed)
        {
            if (seed == 0) throw new ArgumentException("Random seed must be non-zero", nameof(seed));
            state = seed;
            NextState();
        }
        public void InitState(uint seed = 0x6E624EB7u)
        {
            if (seed == 0) throw new ArgumentException("Random seed must be non-zero", nameof(seed));
            state = seed;
            NextState();
        }
        /// <summary>순번 (0, 1, 2, ...) 마다 서로 다른 난수 생성기 — IJobParallelFor 의 index 로</summary>
        public static Random CreateFromIndex(uint index)
        {
            uint h = math.hash(index ^ 0x6E624EB7u);
            return new Random(h == 0 ? 0x9E3779B9u : h);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        uint NextState()
        {
            uint t = state;
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return t;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] public bool NextBool() => (NextState() & 1u) == 1u;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public uint NextUInt() => NextState() - 1u;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public uint NextUInt(uint max) => (uint)(((ulong)NextState() * max) >> 32);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public uint NextUInt(uint min, uint max) => min + (uint)(((ulong)NextState() * (max - min)) >> 32);
        /// <summary>[int.MinValue, int.MaxValue)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public int NextInt() => (int)NextState() ^ int.MinValue;
        /// <summary>[0, max)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public int NextInt(int max) => (int)(((ulong)NextState() * (uint)max) >> 32);
        /// <summary>[min, max)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public int NextInt(int min, int max) => min + (int)(((ulong)NextState() * (uint)(max - min)) >> 32);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public int2 NextInt2(int2 min, int2 max) => new int2(NextInt(min.x, max.x), NextInt(min.y, max.y));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public int3 NextInt3(int3 min, int3 max) => new int3(NextInt(min.x, max.x), NextInt(min.y, max.y), NextInt(min.z, max.z));

        /// <summary>[0, 1)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float NextFloat() => BitConverter.UInt32BitsToSingle(0x3f800000u | (NextState() >> 9)) - 1f;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float NextFloat(float max) => NextFloat() * max;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float NextFloat(float min, float max) => NextFloat() * (max - min) + min;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float2 NextFloat2() => new float2(NextFloat(), NextFloat());
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float3 NextFloat3() => new float3(NextFloat(), NextFloat(), NextFloat());
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float4 NextFloat4() => new float4(NextFloat(), NextFloat(), NextFloat(), NextFloat());
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float2 NextFloat2(float2 min, float2 max) => NextFloat2() * (max - min) + min;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float3 NextFloat3(float3 min, float3 max) => NextFloat3() * (max - min) + min;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float4 NextFloat4(float4 min, float4 max) => NextFloat4() * (max - min) + min;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float2 NextFloat2(float2 max) => NextFloat2() * max;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public float3 NextFloat3(float3 max) => NextFloat3() * max;

        /// <summary>길이 1 인 고른 방향 (원 위)</summary>
        public float2 NextFloat2Direction()
        {
            float a = NextFloat() * math.PI2;
            return new float2(MathF.Cos(a), MathF.Sin(a));
        }
        /// <summary>길이 1 인 고른 방향 (구 위)</summary>
        public float3 NextFloat3Direction()
        {
            float z = NextFloat() * 2f - 1f, r = MathF.Sqrt(MathF.Max(1f - z * z, 0f)), a = NextFloat() * math.PI2;
            return new float3(MathF.Cos(a) * r, MathF.Sin(a) * r, z);
        }
        /// <summary>고르게 퍼진 회전</summary>
        public quaternion NextQuaternionRotation()
        {
            float3 u = NextFloat3();
            float s1 = MathF.Sqrt(1f - u.x), s2 = MathF.Sqrt(u.x);
            float a = math.PI2 * u.y, b = math.PI2 * u.z;
            float4 q = new float4(s1 * MathF.Sin(a), s1 * MathF.Cos(a), s2 * MathF.Sin(b), s2 * MathF.Cos(b));
            return new quaternion(q.w < 0f ? -q : q);
        }
    }
}
