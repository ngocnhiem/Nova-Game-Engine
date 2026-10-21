using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NovaEngine.Collections;
using NovaEngine.Jobs.LowLevel.Unsafe;
using NovaEngine.Burst.Compiler;
using SysUnsafe = System.Runtime.CompilerServices.Unsafe;

namespace NovaEngine.Jobs
{
    // Unity.Jobs 와 같은 이름 · 같은 규칙: 잡은 struct (참조형 필드 없음 — NativeArray 등으로), Schedule 이 돌려준 JobHandle 을 Complete 해야
    // 메인 스레드가 그 컨테이너를 다시 쓸 수 있다.

    /// <summary>작업 스레드에서 한 번 (Unity.Jobs.IJob)</summary>
    public interface IJob { void Execute(); }

    /// <summary>
    /// index 0 … n-1 마다 (Unity.Jobs.IJobFor) — <c>Schedule</c> 은 작업 스레드 하나에서 차례로, <c>ScheduleParallel</c> 은 여러 스레드에 묶음으로
    /// </summary>
    public interface IJobFor { void Execute(int index); }

    /// <summary>index 마다 여러 작업 스레드에 나누어 (Unity.Jobs.IJobParallelFor) — 원소마다 독립인 일</summary>
    public interface IJobParallelFor { void Execute(int index); }

    /// <summary>묶음 (startIndex 부터 count 개) 마다 (Unity.Jobs.IJobParallelForBatch)</summary>
    public interface IJobParallelForBatch { void Execute(int startIndex, int count); }

    /// <summary>
    /// 예약한 잡 (Unity.Jobs.JobHandle). 기본값 = 끝난 것. 의존성으로 넘기면 그 잡이 끝난 뒤에 시작한다.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct JobHandle : IEquatable<JobHandle>
    {
        internal int m_Index;
        internal int m_Version;

        /// <summary>끝날 때까지 기다린다 (메인 스레드도 다른 잡을 도우며) — 그 뒤 잡이 쓰던 컨테이너를 다시 쓸 수 있다</summary>
        public void Complete() => JobScheduler.Complete(this);
        /// <summary>다 돌았는지 (Complete 를 대신하지 않는다 — 컨테이너를 쓰려면 Complete)</summary>
        public bool IsCompleted => JobScheduler.IsCompleted(this);

        /// <summary>NOVA 는 Schedule 하자마자 시작한다 (호환용)</summary>
        public static void ScheduleBatchedJobs() { }

        public static void CompleteAll(ref JobHandle job0, ref JobHandle job1) { job0.Complete(); job1.Complete(); job0 = default; job1 = default; }
        public static void CompleteAll(ref JobHandle job0, ref JobHandle job1, ref JobHandle job2) { job0.Complete(); job1.Complete(); job2.Complete(); job0 = default; job1 = default; job2 = default; }
        public static void CompleteAll(NativeArray<JobHandle> jobs) { for (int i = 0; i < jobs.Length; i++) jobs[i].Complete(); }

        public static JobHandle CombineDependencies(JobHandle job0, JobHandle job1) => JobScheduler.Combine(stackalloc JobHandle[] { job0, job1 });
        public static JobHandle CombineDependencies(JobHandle job0, JobHandle job1, JobHandle job2) => JobScheduler.Combine(stackalloc JobHandle[] { job0, job1, job2 });
        public static JobHandle CombineDependencies(NativeArray<JobHandle> jobs) => JobScheduler.Combine(jobs.AsReadOnlySpan());
        /// <summary>(NOVA) Span 으로</summary>
        public static JobHandle CombineDependencies(ReadOnlySpan<JobHandle> jobs) => JobScheduler.Combine(jobs);

        /// <summary>jobHandle 이 dependsOn 을 (거쳐서라도) 기다리는지, 또는 dependsOn 이 이미 Complete 되었는지</summary>
        public static bool CheckFenceIsDependencyOrDidSyncFence(JobHandle jobHandle, JobHandle dependsOn) => JobScheduler.IsDependencyOrSynced(jobHandle, dependsOn);

        public bool Equals(JobHandle other) => m_Index == other.m_Index && m_Version == other.m_Version;
        public override bool Equals(object obj) => obj is JobHandle h && Equals(h);
        public override int GetHashCode() => HashCode.Combine(m_Index, m_Version);
        public static bool operator ==(JobHandle a, JobHandle b) => a.Equals(b);
        public static bool operator !=(JobHandle a, JobHandle b) => !a.Equals(b);
    }

    // 잡 형식마다 실행 함수 (JobRecord.Data = 잡 사본) — 제약 호출이라 JIT 가 Execute 를 바로 부른다
    internal static unsafe class IJobProducer<T> where T : struct, IJob
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // 처음부터 최적화 JIT (Tier 0 을 건너뛴다)
        internal static void Execute(JobRecord r, int begin, int end)
        {
            IntPtr native = JobReflection<T>.s_NativePtr;
            if (native != IntPtr.Zero && BurstRuntime.Enabled) { BurstRuntime.Invoke(native, r.Data, 0, 1); return; }
            SysUnsafe.AsRef<T>(r.Data).Execute();
        }
    }
    internal static unsafe class IJobForProducer<T> where T : struct, IJobFor
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // 처음부터 최적화 JIT (Tier 0 을 건너뛴다)
        internal static void Execute(JobRecord r, int begin, int end)
        {
            T job = SysUnsafe.Read<T>(r.Data);   // 묶음마다 자기 사본 (Unity 와 같다)
            if (r.Restrict) JobReflection<T>.Restrict(ref job, begin, end - 1);
            IntPtr native = JobReflection<T>.s_NativePtr;
            if (native != IntPtr.Zero && BurstRuntime.Enabled) { BurstRuntime.Invoke(native, SysUnsafe.AsPointer(ref job), begin, end); return; }
            for (int i = begin; i < end; i++) job.Execute(i);
        }
    }
    internal static unsafe class IJobParallelForProducer<T> where T : struct, IJobParallelFor
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // 처음부터 최적화 JIT (Tier 0 을 건너뛴다)
        internal static void Execute(JobRecord r, int begin, int end)
        {
            T job = SysUnsafe.Read<T>(r.Data);
            if (r.Restrict) JobReflection<T>.Restrict(ref job, begin, end - 1);
            IntPtr native = JobReflection<T>.s_NativePtr;
            if (native != IntPtr.Zero && BurstRuntime.Enabled) { BurstRuntime.Invoke(native, SysUnsafe.AsPointer(ref job), begin, end); return; }
            for (int i = begin; i < end; i++) job.Execute(i);
        }
    }
    internal static unsafe class IJobParallelForBatchProducer<T> where T : struct, IJobParallelForBatch
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // 처음부터 최적화 JIT (Tier 0 을 건너뛴다)
        internal static void Execute(JobRecord r, int begin, int end)
        {
            T job = SysUnsafe.Read<T>(r.Data);
            if (r.Restrict) JobReflection<T>.Restrict(ref job, begin, end - 1);
            IntPtr native = JobReflection<T>.s_NativePtr;
            if (native != IntPtr.Zero && BurstRuntime.Enabled) { BurstRuntime.Invoke(native, SysUnsafe.AsPointer(ref job), begin, end); return; }
            job.Execute(begin, end - begin);
        }
    }

    public static unsafe class IJobExtensions
    {
        /// <summary>작업 스레드에서 한 번 — dependsOn 이 끝난 뒤</summary>
        public static JobHandle Schedule<T>(this T jobData, JobHandle dependsOn = default) where T : struct, IJob
            => JobScheduler.Schedule(ref jobData, &IJobProducer<T>.Execute, 1, 1, false, dependsOn);
        /// <summary>지금 이 스레드에서 바로</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Run<T>(this T jobData) where T : struct, IJob
        {
            JobScheduler.BeginRun(ref jobData, out bool was);
            IntPtr native = JobReflection<T>.s_NativePtr;
            try { if (native != IntPtr.Zero && BurstRuntime.Enabled) BurstRuntime.Invoke(native, SysUnsafe.AsPointer(ref jobData), 0, 1); else jobData.Execute(); }
            finally { JobScheduler.EndRun(was); }
        }
    }

    public static unsafe class IJobForExtensions
    {
        /// <summary>작업 스레드 하나에서 0 … arrayLength-1 차례로</summary>
        public static JobHandle Schedule<T>(this T jobData, int arrayLength, JobHandle dependency) where T : struct, IJobFor
            => JobScheduler.Schedule(ref jobData, &IJobForProducer<T>.Execute, arrayLength, Math.Max(arrayLength, 1), false, dependency);
        /// <summary>여러 작업 스레드에 innerloopBatchCount 개씩</summary>
        public static JobHandle ScheduleParallel<T>(this T jobData, int arrayLength, int innerloopBatchCount, JobHandle dependency) where T : struct, IJobFor
            => JobScheduler.Schedule(ref jobData, &IJobForProducer<T>.Execute, arrayLength, innerloopBatchCount, true, dependency);
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Run<T>(this T jobData, int arrayLength) where T : struct, IJobFor
        {
            JobScheduler.BeginRun(ref jobData, out bool was);
            IntPtr native = JobReflection<T>.s_NativePtr;
            try { if (native != IntPtr.Zero && BurstRuntime.Enabled) BurstRuntime.Invoke(native, SysUnsafe.AsPointer(ref jobData), 0, arrayLength); else for (int i = 0; i < arrayLength; i++) jobData.Execute(i); }
            finally { JobScheduler.EndRun(was); }
        }
    }

    public static unsafe class IJobParallelForExtensions
    {
        /// <summary>여러 작업 스레드에 innerloopBatchCount 개씩 (작은 일이면 크게 — 32 ~ 1024)</summary>
        public static JobHandle Schedule<T>(this T jobData, int arrayLength, int innerloopBatchCount, JobHandle dependsOn = default) where T : struct, IJobParallelFor
            => JobScheduler.Schedule(ref jobData, &IJobParallelForProducer<T>.Execute, arrayLength, innerloopBatchCount, true, dependsOn);
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Run<T>(this T jobData, int arrayLength) where T : struct, IJobParallelFor
        {
            JobScheduler.BeginRun(ref jobData, out bool was);
            IntPtr native = JobReflection<T>.s_NativePtr;
            try { if (native != IntPtr.Zero && BurstRuntime.Enabled) BurstRuntime.Invoke(native, SysUnsafe.AsPointer(ref jobData), 0, arrayLength); else for (int i = 0; i < arrayLength; i++) jobData.Execute(i); }
            finally { JobScheduler.EndRun(was); }
        }
    }

    public static unsafe class IJobParallelForBatchExtensions
    {
        public static JobHandle ScheduleBatch<T>(this T jobData, int arrayLength, int indicesPerJobCount, JobHandle dependsOn = default) where T : struct, IJobParallelForBatch
            => JobScheduler.Schedule(ref jobData, &IJobParallelForBatchProducer<T>.Execute, arrayLength, indicesPerJobCount, true, dependsOn);
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void RunBatch<T>(this T jobData, int arrayLength) where T : struct, IJobParallelForBatch
        {
            JobScheduler.BeginRun(ref jobData, out bool was);
            IntPtr native = JobReflection<T>.s_NativePtr;
            try { if (native != IntPtr.Zero && BurstRuntime.Enabled) BurstRuntime.Invoke(native, SysUnsafe.AsPointer(ref jobData), 0, arrayLength); else jobData.Execute(0, arrayLength); }
            finally { JobScheduler.EndRun(was); }
        }
    }

    // 컨테이너의 Dispose(JobHandle): 의존성이 끝난 뒤 작업 스레드가 푼다
    internal struct DisposeJob<C> : IJob where C : struct, IDisposable
    {
        public C Container;
        public void Execute() => Container.Dispose();
    }
}

namespace NovaEngine.Jobs.LowLevel.Unsafe
{
    /// <summary>잡 실행기 정보 (Unity.Jobs.LowLevel.Unsafe.JobsUtility)</summary>
    public static class JobsUtility
    {
        public const int CacheLineSize = 64;
        /// <summary>작업 스레드 최대 수 (코어 - 1)</summary>
        public static int JobWorkerMaximumCount => JobScheduler.MaxWorkers;
        /// <summary>지금 쓰는 작업 스레드 수 (0 이면 메인 스레드가 Complete 때 모두 돈다)</summary>
        public static int JobWorkerCount { get => JobScheduler.ActiveWorkers; set => JobScheduler.SetActiveWorkers(value); }
        /// <summary>이 스레드가 지금 잡을 돌리는 중인지</summary>
        public static bool IsExecutingJob => JobScheduler.t_InJob;
        /// <summary>잡 안전 검사 (CollectionChecks.Enabled — 에디터)</summary>
        public static bool JobDebuggerEnabled => CollectionChecks.Enabled;
        /// <summary>0 = 메인 · 잡이 아닌 스레드, 1 … = 작업 스레드</summary>
        public static int ThreadIndex => JobScheduler.t_WorkerIndex;
        public static int ThreadIndexCount => JobScheduler.MaxWorkers + 1;
    }
}
