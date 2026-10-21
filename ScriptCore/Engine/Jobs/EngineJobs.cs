using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NovaEngine.Collections;
using NovaEngine.Collections.LowLevel.Unsafe;
using NovaEngine.Interop;
using NovaEngine.Jobs.LowLevel.Unsafe;
using NovaEngine.Burst.Compiler;
using SysUnsafe = System.Runtime.CompilerServices.Unsafe;

namespace NovaEngine.Jobs
{
    // ---------------------------------------------------------------- TransformAccessArray (UnityEngine.Jobs)
    // 엔진 Transform 은 메인 스레드만 만진다: 예약할 때 메인이 값을 모으고 (TR_Gather), 작업 스레드는 그 사본을 읽고 고치고,
    // Complete 때 (또는 다음 프레임 시작) 메인이 바뀐 값만 되쓴다 (TR_Scatter).

    /// <summary>TransformAccess 가 가리키는 칸 (ScriptBindings.cpp TransformAccessData 와 같은 88 바이트)</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TransformAccessData
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
        public Vector3 lossyScale;
        public int flags;
        public int pad;

        internal const int kValid = 1, kPosition = 2, kRotation = 4, kLocalPosition = 8, kLocalRotation = 16, kLocalScale = 32;
    }

    /// <summary>
    /// 잡 안의 Transform 하나 (UnityEngine.Jobs.TransformAccess). 바꾼 값은 Complete 때 엔진 Transform 에 들어간다.
    /// 한 잡 안에서 position 을 바꿔도 localPosition 은 그대로 (되쓸 때 엔진이 맞춘다 — 같은 잡에서 둘을 섞어 쓰지 말 것).
    /// </summary>
    public unsafe struct TransformAccess
    {
        internal TransformAccessData* m_Data;
        internal TransformAccess(TransformAccessData* data) { m_Data = data; }

        public bool isValid => m_Data != null && (m_Data->flags & TransformAccessData.kValid) != 0;
        public Vector3 position { get => m_Data->position; set { m_Data->position = value; m_Data->flags |= TransformAccessData.kPosition; } }
        public Quaternion rotation { get => m_Data->rotation; set { m_Data->rotation = value; m_Data->flags |= TransformAccessData.kRotation; } }
        public Vector3 localPosition { get => m_Data->localPosition; set { m_Data->localPosition = value; m_Data->flags |= TransformAccessData.kLocalPosition; } }
        public Quaternion localRotation { get => m_Data->localRotation; set { m_Data->localRotation = value; m_Data->flags |= TransformAccessData.kLocalRotation; } }
        public Vector3 localScale { get => m_Data->localScale; set { m_Data->localScale = value; m_Data->flags |= TransformAccessData.kLocalScale; } }
        /// <summary>월드 크기 (부모 포함, 읽기만)</summary>
        public Vector3 lossyScale => m_Data->lossyScale;
        public void SetPositionAndRotation(Vector3 position, Quaternion rotation) { this.position = position; this.rotation = rotation; }
        public void SetLocalPositionAndRotation(Vector3 localPosition, Quaternion localRotation) { this.localPosition = localPosition; this.localRotation = localRotation; }
        public void GetPositionAndRotation(out Vector3 position, out Quaternion rotation) { position = m_Data->position; rotation = m_Data->rotation; }
        public void GetLocalPositionAndRotation(out Vector3 localPosition, out Quaternion localRotation) { localPosition = m_Data->localPosition; localRotation = m_Data->localRotation; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct TransformArrayData
    {
        public ulong* Ids;
        public TransformAccessData* Buffer;
        public int Length, Capacity;
    }

    /// <summary>
    /// 잡에 넘길 Transform 목록 (UnityEngine.Jobs.TransformAccessArray) — Persistent 메모리, 다 쓰면 Dispose.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    [NativeContainer, NativeContainerIsAtomicWriteOnly]   // 잡 index 마다 자기 Transform 만 — 병렬로 써도 된다
    public unsafe struct TransformAccessArray : IDisposable
    {
        internal TransformArrayData* m_Data;
        internal AtomicSafetyHandle m_Safety;

        public TransformAccessArray(int capacity, int desiredJobCount = -1)
        {
            m_Data = (TransformArrayData*)UnsafeUtility.Malloc(sizeof(TransformArrayData), 16, Allocator.Persistent);
            m_Data->Ids = null; m_Data->Buffer = null; m_Data->Length = 0; m_Data->Capacity = 0;
            m_Safety = CollectionHelper.CreateSafety(m_Data, Allocator.Persistent, "TransformAccessArray");
            Reserve(Math.Max(capacity, 1));
        }
        public TransformAccessArray(Transform[] transforms, int desiredJobCount = -1) : this(transforms?.Length ?? 0, desiredJobCount) { SetTransforms(transforms); }
        public static void Allocate(int capacity, int desiredJobCount, out TransformAccessArray array) => array = new TransformAccessArray(capacity, desiredJobCount);

        public readonly bool isCreated => m_Data != null;
        public int length { get { CheckRead(); return m_Data->Length; } }
        public int capacity { get { CheckRead(); return m_Data->Capacity; } set { CheckWrite(); Reserve(Math.Max(value, m_Data->Length)); } }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void CheckRead()
        {
            if (m_Data == null) CollectionHelper.ThrowNotCreated();
            if (CollectionChecks.Enabled) AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void CheckWrite()
        {
            if (m_Data == null) CollectionHelper.ThrowNotCreated();
            if (CollectionChecks.Enabled) AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
        }

        void Reserve(int cap)
        {
            if (cap <= m_Data->Capacity) return;
            var ids = (ulong*)UnsafeUtility.Malloc((long)cap * 8, 16, Allocator.Persistent);
            if (m_Data->Ids != null)
            {
                UnsafeUtility.MemCpy(ids, m_Data->Ids, (long)m_Data->Length * 8);
                UnsafeUtility.Free(m_Data->Ids, Allocator.Persistent);
            }
            if (m_Data->Buffer != null) UnsafeUtility.Free(m_Data->Buffer, Allocator.Persistent);
            m_Data->Ids = ids;
            m_Data->Buffer = (TransformAccessData*)UnsafeUtility.Malloc((long)cap * sizeof(TransformAccessData), 16, Allocator.Persistent);
            m_Data->Capacity = cap;
        }

        public Transform this[int index]
        {
            get { CheckRead(); if ((uint)index >= (uint)m_Data->Length) CollectionHelper.ThrowIndex(index, m_Data->Length); ulong id = m_Data->Ids[index]; return id == 0 ? null : new Transform(id); }
            set { CheckWrite(); if ((uint)index >= (uint)m_Data->Length) CollectionHelper.ThrowIndex(index, m_Data->Length); m_Data->Ids[index] = value is null ? 0 : value.m_Id; }
        }
        public void Add(Transform transform)
        {
            CheckWrite();
            if (m_Data->Length == m_Data->Capacity) Reserve(m_Data->Capacity * 2);
            m_Data->Ids[m_Data->Length++] = transform is null ? 0 : transform.m_Id;
        }
        public void RemoveAtSwapBack(int index)
        {
            CheckWrite();
            if ((uint)index >= (uint)m_Data->Length) CollectionHelper.ThrowIndex(index, m_Data->Length);
            m_Data->Ids[index] = m_Data->Ids[--m_Data->Length];
        }
        public void SetTransforms(Transform[] transforms)
        {
            CheckWrite();
            int n = transforms?.Length ?? 0;
            Reserve(n);
            for (int i = 0; i < n; i++) m_Data->Ids[i] = transforms[i] is null ? 0 : transforms[i].m_Id;
            m_Data->Length = n;
        }

        public void Dispose()
        {
            if (CollectionChecks.Enabled && !AtomicSafetyHandle.IsDefaultValue(m_Safety)) AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            if (m_Data == null) return;
            if (CollectionChecks.Enabled)
            {
                AtomicSafetyHandle.CheckDeallocateAndThrow(m_Safety);
                AtomicSafetyHandle.Release(m_Safety);
            }
            if (m_Data->Ids != null) UnsafeUtility.Free(m_Data->Ids, Allocator.Persistent);
            if (m_Data->Buffer != null) UnsafeUtility.Free(m_Data->Buffer, Allocator.Persistent);
            UnsafeUtility.Free(m_Data, Allocator.Persistent);
            m_Data = null;
        }
    }

    /// <summary>Transform 마다 (UnityEngine.Jobs.IJobParallelForTransform)</summary>
    public interface IJobParallelForTransform { void Execute(int index, TransformAccess transform); }

    [JobWrapper]
    internal struct TransformJobData<T> where T : struct, IJobParallelForTransform
    {
        public T Job;
        public TransformAccessArray Transforms;
    }
    [JobWrapper]
    internal struct TransformJobDataReadOnly<T> where T : struct, IJobParallelForTransform
    {
        public T Job;
        [ReadOnly] public TransformAccessArray Transforms;
    }

    internal static unsafe class IJobParallelForTransformProducer<T> where T : struct, IJobParallelForTransform
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void Execute(JobRecord r, int begin, int end)
        {
            TransformJobData<T> d = SysUnsafe.Read<TransformJobData<T>>(r.Data);   // 묶음마다 사본
            if (r.Restrict) JobReflection<TransformJobData<T>>.Restrict(ref d, begin, end - 1);
            IntPtr native = JobReflection<TransformJobData<T>>.s_NativePtr;
            if (native != IntPtr.Zero && BurstRuntime.Enabled) { BurstRuntime.Invoke(native, SysUnsafe.AsPointer(ref d), begin, end); return; }
            TransformAccessData* buffer = d.Transforms.m_Data->Buffer;
            for (int i = begin; i < end; i++) d.Job.Execute(i, new TransformAccess(buffer + i));
        }
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void ExecuteReadOnly(JobRecord r, int begin, int end)
        {
            TransformJobDataReadOnly<T> d = SysUnsafe.Read<TransformJobDataReadOnly<T>>(r.Data);
            if (r.Restrict) JobReflection<TransformJobDataReadOnly<T>>.Restrict(ref d, begin, end - 1);
            IntPtr native = JobReflection<TransformJobDataReadOnly<T>>.s_NativePtr;
            if (native != IntPtr.Zero && BurstRuntime.Enabled) { BurstRuntime.Invoke(native, SysUnsafe.AsPointer(ref d), begin, end); return; }
            TransformAccessData* buffer = d.Transforms.m_Data->Buffer;
            for (int i = begin; i < end; i++) d.Job.Execute(i, new TransformAccess(buffer + i));
        }
    }

    public static unsafe class IJobParallelForTransformExtensions
    {
        /// <summary>Transform 마다 여러 작업 스레드에서 — dependsOn 은 먼저 Complete 된다 (엔진 Transform 을 모으기 전에)</summary>
        public static JobHandle Schedule<T>(this T jobData, TransformAccessArray transforms, JobHandle dependsOn = default) where T : struct, IJobParallelForTransform
            => TransformJobs.Schedule(ref jobData, transforms, 64, false, dependsOn);
        /// <summary>읽기만 (되쓰지 않는다)</summary>
        public static JobHandle ScheduleReadOnly<T>(this T jobData, TransformAccessArray transforms, int batchSize, JobHandle dependsOn = default) where T : struct, IJobParallelForTransform
            => TransformJobs.Schedule(ref jobData, transforms, batchSize, true, dependsOn);
        /// <summary>지금 이 스레드에서 (바꾼 값은 바로 들어간다)</summary>
        public static void Run<T>(this T jobData, TransformAccessArray transforms) where T : struct, IJobParallelForTransform
            => TransformJobs.Run(ref jobData, transforms, false);
        public static void RunReadOnly<T>(this T jobData, TransformAccessArray transforms) where T : struct, IJobParallelForTransform
            => TransformJobs.Run(ref jobData, transforms, true);
    }

    internal static unsafe class TransformJobs
    {
        static readonly List<JobHandle> s_Pending = new List<JobHandle>();

        static int Gather(TransformAccessArray transforms)
        {
            transforms.CheckWrite();   // 다른 Transform 잡이 쓰는 중이면 (Complete 전) 여기서 알린다
            int n = transforms.m_Data->Length;
            if (n > 0) Native.Api.TR_Gather(transforms.m_Data->Ids, n, transforms.m_Data->Buffer);
            return n;
        }

        internal static JobHandle Schedule<T>(ref T job, TransformAccessArray transforms, int batch, bool readOnly, JobHandle dependsOn) where T : struct, IJobParallelForTransform
        {
            if (JobScheduler.t_InJob) throw new InvalidOperationException("Jobs can only be scheduled from the main thread (not from inside a job).");
            if (!transforms.isCreated) throw new ArgumentException("The TransformAccessArray has not been created", nameof(transforms));
            dependsOn.Complete();   // 앞 잡이 같은 Transform 을 바꿨을 수 있다 — 되쓴 뒤에 모은다
            int n = Gather(transforms);
            JobHandle h;
            if (readOnly)
            {
                var d = new TransformJobDataReadOnly<T> { Job = job, Transforms = transforms };
                h = JobScheduler.Schedule(ref d, &IJobParallelForTransformProducer<T>.ExecuteReadOnly, n, Math.Max(batch, 1), true, default);
            }
            else
            {
                var d = new TransformJobData<T> { Job = job, Transforms = transforms };
                h = JobScheduler.Schedule(ref d, &IJobParallelForTransformProducer<T>.Execute, n, Math.Max(batch, 1), true, default);
                JobScheduler.SetSyncCallback(h, &WriteBack, (IntPtr)transforms.m_Data);
                s_Pending.Add(h);
            }
            return h;
        }

        internal static void Run<T>(ref T job, TransformAccessArray transforms, bool readOnly) where T : struct, IJobParallelForTransform
        {
            if (!transforms.isCreated) throw new ArgumentException("The TransformAccessArray has not been created", nameof(transforms));
            int n = Gather(transforms);
            JobScheduler.BeginRun(ref job, out bool was);
            try
            {
                TransformAccessData* buffer = transforms.m_Data->Buffer;
                for (int i = 0; i < n; i++) job.Execute(i, new TransformAccess(buffer + i));
            }
            finally { JobScheduler.EndRun(was); }
            if (!readOnly) WriteBack((IntPtr)transforms.m_Data);
        }

        static void WriteBack(IntPtr data)
        {
            var d = (TransformArrayData*)data;
            if (d->Length > 0 && d->Buffer != null) Native.Api.TR_Scatter(d->Ids, d->Length, d->Buffer);
        }

        /// <summary>프레임 시작: Complete 하지 않은 Transform 잡을 마무리 (바뀐 값이 화면에 — Unity 는 렌더링 전에 맞춘다)</summary>
        internal static void CompletePending()
        {
            if (s_Pending.Count == 0) return;
            JobHandle[] list = s_Pending.ToArray();
            s_Pending.Clear();
            foreach (JobHandle h in list) h.Complete();
        }
    }
}

namespace NovaEngine
{
    using NovaEngine.Jobs;

    /// <summary>질의 설정 (UnityEngine.QueryParameters)</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct QueryParameters
    {
        public int layerMask;
        public bool hitMultipleFaces;
        public QueryTriggerInteraction hitTriggers;
        public bool hitBackfaces;
        public QueryParameters(int layerMask = -5, bool hitMultipleFaces = false, QueryTriggerInteraction hitTriggers = QueryTriggerInteraction.UseGlobal, bool hitBackfaces = false)
        {
            this.layerMask = layerMask; this.hitMultipleFaces = hitMultipleFaces; this.hitTriggers = hitTriggers; this.hitBackfaces = hitBackfaces;
        }
        /// <summary>Ignore Raycast 레이어를 뺀 모두, 트리거 빼고</summary>
        public static QueryParameters Default => new QueryParameters(-5);
    }

    /// <summary>물리 씬 (NOVA 는 하나 — 호환용)</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PhysicsScene { internal int m_Handle; }

    /// <summary>
    /// 묶음 레이캐스트 (UnityEngine.RaycastCommand) — 수천 개를 한 번에. 결과는 NativeArray&lt;RaycastHit&gt; (맞지 않은 칸은 collider == null).
    /// NOVA: ScheduleBatch 가 dependsOn 을 Complete 한 뒤 엔진 Job System (네이티브) 으로 나눠 쏜다 — 돌려준 핸들은 이미 끝난 것.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct RaycastCommand
    {
        public Vector3 from;
        public Vector3 direction;
        public QueryParameters queryParameters;
        public float distance;
        public PhysicsScene physicsScene;

        public RaycastCommand(Vector3 from, Vector3 direction, QueryParameters queryParameters, float distance = float.MaxValue)
        {
            this.from = from; this.direction = direction; this.queryParameters = queryParameters; this.distance = distance; physicsScene = default;
        }
        public RaycastCommand(Vector3 from, Vector3 direction, float distance = float.MaxValue, int layerMask = -5, int maxHits = 1)
            : this(from, direction, new QueryParameters(layerMask), distance) { }

        public int layerMask { get => queryParameters.layerMask; set => queryParameters.layerMask = value; }

        public static JobHandle ScheduleBatch(NativeArray<RaycastCommand> commands, NativeArray<RaycastHit> results, int minCommandsPerJob, JobHandle dependsOn = default)
            => ScheduleBatch(commands, results, minCommandsPerJob, 1, dependsOn);

        /// <summary>results 는 commands.Length × maxHits 칸 (명령 i 의 맞음은 i × maxHits 부터, 가까운 순, 남은 칸은 비움)</summary>
        public static JobHandle ScheduleBatch(NativeArray<RaycastCommand> commands, NativeArray<RaycastHit> results, int minCommandsPerJob, int maxHits, JobHandle dependsOn = default)
        {
            if (JobScheduler.t_InJob) throw new InvalidOperationException("RaycastCommand.ScheduleBatch can only be called from the main thread.");
            maxHits = Math.Max(1, maxHits);
            if ((long)results.Length < (long)commands.Length * maxHits)
                throw new ArgumentException("results must have commands.Length * maxHits (" + ((long)commands.Length * maxHits) + ") elements, not " + results.Length, nameof(results));
            dependsOn.Complete();   // 명령을 채운 잡이 끝나야 쏠 수 있다
            commands.CheckRead();
            results.CheckWrite();
            if (commands.Length > 0) Native.Api.PH_RaycastBatch((RaycastCommand*)commands.m_Buffer, commands.Length, (RaycastHit*)results.m_Buffer, maxHits);
            return default;
        }
    }
}
