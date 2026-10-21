using System;
using NovaEngine.Collections.LowLevel.Unsafe;

namespace NovaEngine.Collections
{
    /// <summary>
    /// 메모리를 어디서 얼마 동안 (Unity.Collections.Allocator 와 같은 이름 · 같은 뜻).
    /// Temp = 이 프레임만 (가장 빠름, 다음 프레임 시작에 저절로 풀린다 · 잡에 넘기지 않는다),
    /// TempJob = 잡에 넘길 짧은 것 (4 프레임 안에 Dispose), Persistent = 오래 (직접 Dispose).
    /// </summary>
    public enum Allocator { Invalid = 0, None = 1, Temp = 2, TempJob = 3, Persistent = 4 }

    /// <summary>새 NativeArray 의 메모리를 0 으로 채울지</summary>
    public enum NativeArrayOptions { UninitializedMemory = 0, ClearMemory = 1 }

    /// <summary>잡 (Job) 이 이 컨테이너를 읽기만 한다 — 여러 잡이 함께 읽을 수 있다</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter)]
    public sealed class ReadOnlyAttribute : Attribute { }

    /// <summary>잡이 이 컨테이너에 쓰기만 한다</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter)]
    public sealed class WriteOnlyAttribute : Attribute { }

    /// <summary>IJobParallelFor 에서 자기 index 밖 원소에도 쓰도록 허락 (경쟁은 직접 막는다)</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class NativeDisableParallelForRestrictionAttribute : Attribute { }

    /// <summary>잡이 끝나면 이 컨테이너를 저절로 Dispose</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class DeallocateOnJobCompletionAttribute : Attribute { }

    /// <summary>이 필드의 안전 검사 (해제 뒤 사용 · 잡 사이 경쟁) 를 끈다</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class NativeDisableContainerSafetyRestrictionAttribute : Attribute { }

    /// <summary>
    /// 해제하지 않은 TempJob · Persistent 메모리를 찾는다 (Unity.Collections.NativeLeakDetection).
    /// Play 가 끝날 때 남은 것을 오류로 알리고 풀어 준다. 에디터 기본 Enabled, 플레이어 기본 Disabled.
    /// </summary>
    public static class NativeLeakDetection
    {
        static NativeLeakDetectionMode s_Mode = CollectionChecks.Enabled ? NativeLeakDetectionMode.Enabled : NativeLeakDetectionMode.Disabled;

        public static NativeLeakDetectionMode Mode
        {
            get => s_Mode;
            set => s_Mode = value;
        }

        /// <summary>(NOVA) 지금 살아 있는 TempJob · Persistent 할당 수 (추적 중인 것만)</summary>
        public static int LiveAllocations => AllocationTracker.LiveCount;
        /// <summary>(NOVA) 그 바이트 수</summary>
        public static long LiveBytes => AllocationTracker.LiveBytes;
    }

    public enum NativeLeakDetectionMode
    {
        Disabled = 1,
        Enabled = 2,
        /// <summary>할당한 곳의 호출 스택도 남긴다 (느리다)</summary>
        EnabledWithStackTrace = 3,
    }

    /// <summary>
    /// (NOVA) 컨테이너 안전 검사 — 범위 밖 색인, Dispose 뒤 사용, 읽기 전용에 쓰기, 잡 사이 경쟁.
    /// Unity 의 ENABLE_UNITY_COLLECTIONS_CHECKS 와 같다: 에디터에서 켜고 플레이어 빌드에서 끈다.
    /// 환경 변수 NOVA_COLLECTIONS_CHECKS=0 / 1 로 바꿀 수 있다 (성능 측정). 시작할 때 한 번 정해진다 (JIT 가 검사 자체를 지운다).
    /// </summary>
    public static class CollectionChecks
    {
        public static readonly bool Enabled = Decide();

        static bool Decide()
        {
            string env = Environment.GetEnvironmentVariable("NOVA_COLLECTIONS_CHECKS");
            if (env == "0") return false;
            if (env == "1") return true;
            try { return Application.isEditor; }
            catch { return true; }
        }
    }
}
