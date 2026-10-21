using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NovaEngine.Burst
{
    // Unity.Burst 와 같은 이름. 1 단계 (지금): [BurstCompile] 이 붙은 잡 · 함수는 컴파일 때 HPC# 규칙 검사 (NovaBurstAnalyzer — 관리 객체 ·
    //  관리 배열 · 박싱 · 람다 · 정적 필드 · try/catch …) 를 받고, 게임 스크립트 Release 빌드 + 최적화된 잡 실행 함수로 돈다 (RyuJIT).
    //  2 단계 (로드맵 B-4): HPC# 코드만 네이티브 (C++ · SIMD) 로 옮겨 빌드.

    public enum FloatMode { Default = 0, Strict = 1, Deterministic = 2, Fast = 3 }
    public enum FloatPrecision { Standard = 0, High = 1, Medium = 2, Low = 3 }
    public enum OptimizeFor { Default = 0, Performance = 1, Size = 2, FastCompilation = 3, Balanced = 4 }

    /// <summary>
    /// 잡 struct · 정적 함수 (와 그것을 담은 클래스) 를 Burst 로 (Unity.Burst.BurstCompileAttribute).
    /// NOVA 1 단계: HPC# 규칙을 컴파일 때 검사 (Console 에 NB1xxx 경고), Profiler 에 "(Burst)".
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Assembly)]
    public sealed class BurstCompileAttribute : Attribute
    {
        public BurstCompileAttribute() { }
        public BurstCompileAttribute(FloatPrecision floatPrecision, FloatMode floatMode) { FloatPrecision = floatPrecision; FloatMode = floatMode; }
        public FloatMode FloatMode { get; set; }
        public FloatPrecision FloatPrecision { get; set; }
        public bool CompileSynchronously { get; set; }
        public bool Debug { get; set; }
        public bool DisableSafetyChecks { get; set; }
        public bool DisableDirectCall { get; set; }
        public OptimizeFor OptimizeFor { get; set; }
    }

    /// <summary>Burst 코드에서는 빼는 메서드 (관리 코드에서만 — 로그 · 검사). NOVA 1 단계는 늘 관리 코드라 그대로 돈다</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class BurstDiscardAttribute : Attribute { }

    /// <summary>포인터 · 컨테이너 인자가 서로 겹치지 않는다는 약속 (Unity.Burst.NoAliasAttribute) — 2 단계 벡터화가 쓴다</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Struct)]
    public sealed class NoAliasAttribute : Attribute { }

    /// <summary>Burst 컴파일러 상태 (Unity.Burst.BurstCompiler)</summary>
    public static class BurstCompiler
    {
        /// <summary>NOVA 1 단계: 네이티브 Burst 컴파일러는 아직 없다 (HPC# 검사 + 최적화 JIT) — 2 단계에서 true</summary>
        public static bool IsEnabled => false;
        public static readonly BurstCompilerOptions Options = new BurstCompilerOptions();

        /// <summary>정적 [BurstCompile] 함수를 함수 포인터로 (Unity 와 같은 사용법 — NOVA 1 단계는 델리게이트를 그대로 부른다)</summary>
        public static FunctionPointer<T> CompileFunctionPointer<T>(T delegateMethod) where T : class
        {
            if (delegateMethod == null) throw new ArgumentNullException(nameof(delegateMethod));
            if (!(delegateMethod is Delegate d)) throw new ArgumentException("T must be a delegate type", nameof(delegateMethod));
            if (d.Target != null) throw new InvalidOperationException("Burst function pointers must point to static methods (" + d.Method.Name + " is an instance method).");
            return new FunctionPointer<T>(GCHandle.ToIntPtr(GCHandle.Alloc(delegateMethod)));
        }
    }

    public sealed class BurstCompilerOptions
    {
        public bool EnableBurstCompilation { get; set; } = true;
        public bool EnableBurstSafetyChecks { get; set; } = true;
        public bool ForceEnableBurstSafetyChecks { get; set; }
        public bool EnableBurstCompileSynchronously { get; set; }
        public bool EnableBurstDebug { get; set; }
        public bool IsEnabled => EnableBurstCompilation && BurstCompiler.IsEnabled;
    }

    /// <summary>Burst 함수 포인터 (Unity.Burst.FunctionPointer) — <c>Invoke</c> 로 부른다</summary>
    public readonly struct FunctionPointer<T> where T : class
    {
        readonly IntPtr m_Handle;
        public FunctionPointer(IntPtr ptr) { m_Handle = ptr; }
        public IntPtr Value => m_Handle;
        public bool IsCreated => m_Handle != IntPtr.Zero;
        public T Invoke => m_Handle == IntPtr.Zero ? null : (T)GCHandle.FromIntPtr(m_Handle).Target;
    }

    /// <summary>
    /// 잡 · Burst 코드에서도 읽고 쓰는 정적 값 (Unity.Burst.SharedStatic) — 비관리 메모리, 키 형식마다 하나.
    /// <c>static readonly SharedStatic&lt;int&gt; s_Count = SharedStatic&lt;int&gt;.GetOrCreate&lt;MyKey&gt;();</c> → <c>s_Count.Data++</c>
    /// </summary>
    public readonly unsafe struct SharedStatic<T> where T : struct
    {
        readonly void* m_Buffer;
        SharedStatic(void* buffer) { m_Buffer = buffer; }
        public ref T Data => ref Unsafe.AsRef<T>(m_Buffer);
        public void* UnsafeDataPointer => m_Buffer;

        public static SharedStatic<T> GetOrCreate<TContext>(uint alignment = 0) => new SharedStatic<T>(SharedStaticStore.Get(typeof(TContext), null, Unsafe.SizeOf<T>(), alignment));
        public static SharedStatic<T> GetOrCreate<TContext, TSubContext>(uint alignment = 0) => new SharedStatic<T>(SharedStaticStore.Get(typeof(TContext), typeof(TSubContext), Unsafe.SizeOf<T>(), alignment));
        public static SharedStatic<T> GetOrCreate(Type contextType, uint alignment = 0) => new SharedStatic<T>(SharedStaticStore.Get(contextType, null, Unsafe.SizeOf<T>(), alignment));
    }

    internal static unsafe class SharedStaticStore
    {
        static readonly ConcurrentDictionary<string, IntPtr> s_Buffers = new ConcurrentDictionary<string, IntPtr>();
        internal static void* Get(Type context, Type sub, int size, uint alignment)
        {
            // 키는 이름 (게임 어셈블리를 다시 읽어도 같은 칸 — Unity 와 같이 도메인을 넘어 남는다)
            string key = context.AssemblyQualifiedName + "|" + sub?.AssemblyQualifiedName + "|" + size;
            return (void*)s_Buffers.GetOrAdd(key, _ =>
            {
                nuint align = (nuint)Math.Max(16u, alignment);
                void* p = NativeMemory.AlignedAlloc((nuint)Math.Max(size, 1), align);
                NativeMemory.Clear(p, (nuint)Math.Max(size, 1));
                return (IntPtr)p;
            });
        }
    }
}

namespace NovaEngine.Burst.CompilerServices
{
    /// <summary>분기 힌트 (Unity.Burst.CompilerServices.Hint) — 1 단계는 그대로 값</summary>
    public static class Hint
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool Likely(bool condition) => condition;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool Unlikely(bool condition) => condition;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Assume(bool condition) { Debug.Assert(condition); }
    }

    /// <summary>벡터화 기대 표시 (Unity.Burst.CompilerServices.Loop) — 2 단계 네이티브 컴파일이 검사한다</summary>
    public static class Loop
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void ExpectVectorized() { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void ExpectNotVectorized() { }
    }

    /// <summary>지역 변수를 0 으로 채우지 않는다 (Unity.Burst.CompilerServices.SkipLocalsInitAttribute)</summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Struct | AttributeTargets.Class)]
    public sealed class SkipLocalsInitAttribute : Attribute { }
}
