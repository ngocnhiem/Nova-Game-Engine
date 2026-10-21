using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using NovaEngine.Collections;
using NovaEngine.Collections.LowLevel.Unsafe;
using NovaEngine.Interop;
using NovaEngine.Profiling;
using SysUnsafe = System.Runtime.CompilerServices.Unsafe;

namespace NovaEngine.Jobs
{
    // 예약한 잡 하나 (풀에서 되쓴다 — JobHandle 은 번호 + 버전)
    internal sealed unsafe class JobRecord
    {
        internal const int kFree = 0, kWaiting = 1, kQueued = 2, kFinished = 3;

        internal int Index, Version;
        internal int State;
        internal bool Synced;
        internal int PendingDeps;
        internal long[] Deps = new long[4];
        internal int DepCount;
        internal long[] Dependents = new long[4];
        internal int DependentCount;

        internal void* Data;                                  // 잡 struct 사본 (비관리 메모리)
        internal delegate*<JobRecord, int, int, void> Exec;   // JobProducer<T>.Execute
        internal delegate*<JobRecord, void> Finish;           // [DeallocateOnJobCompletion]
        internal int Length, Batch, Next, Done, Tickets;
        internal int Outstanding;          // 아직 큐에서 안 나왔거나 도는 중인 표 — 0 이 되기 전엔 되쓰지 않는다
        internal bool RecyclePending;
        internal bool Restrict;
        internal string Name;
        internal byte* ProfName;

        internal struct Lock { public IntPtr Node; public int Version; public bool Write; public string Field; }
        internal Lock[] Locks = new Lock[4];
        internal int LockCount;

        internal long Key => ((long)Index << 32) | (uint)Version;
    }

    /// <summary>
    /// C# Job System 실행기. 작업 스레드는 C# 전용 (코어 - 1) — 엔진 Job System 은 파이버를 갈아 끼우는데 CLR 은 파이버 위에서 돌 수 없다.
    /// 메인 스레드도 Complete 에서 기다리는 동안 잡을 돕는다. 안전 검사 (에디터): 컨테이너마다 예약된 잡의 읽기 · 쓰기 잠금,
    /// 같은 컨테이너를 쓰는 잡 사이에 의존성이 없으면 예약을 거부, 잡이 쓰는 동안 메인 스레드 접근 · Dispose 를 거부 (Unity 와 같은 규칙 · 문구).
    /// </summary>
    internal static unsafe class JobScheduler
    {
        static readonly object s_Lock = new object();
        static JobRecord[] s_Records = new JobRecord[256];
        static int s_RecordCount = 1;   // 0 = 없음
        static readonly Stack<int> s_FreeRecords = new Stack<int>();
        static int s_LiveRecords;

        static readonly ConcurrentQueue<long> s_Queue = new ConcurrentQueue<long>();
        static readonly SemaphoreSlim s_Signal = new SemaphoreSlim(0);
        static int s_Sleeping;
        static Thread[] s_Workers;
        static int s_ActiveWorkers = -1;
        static readonly ConcurrentQueue<(string job, Exception e)> s_Errors = new ConcurrentQueue<(string, Exception)>();

        [ThreadStatic] internal static bool t_InJob;
        [ThreadStatic] internal static int t_WorkerIndex;

        // 컨테이너 (안전 노드) 마다 지금 잠근 잡 — 예약 때 의존성 검사 · 오류 문구
        sealed class NodeLocks
        {
            public int Version;
            public long Writer;
            public string WriterName;
            public readonly List<(long job, string name)> Readers = new List<(long, string)>();
        }
        static readonly Dictionary<IntPtr, NodeLocks> s_NodeLocks = new Dictionary<IntPtr, NodeLocks>();

        internal static int MaxWorkers { get; } = Math.Max(1, Environment.ProcessorCount - 1);
        internal static int ActiveWorkers => s_ActiveWorkers < 0 ? MaxWorkers : s_ActiveWorkers;
        internal static int LiveRecords => s_LiveRecords;

        internal static void SetActiveWorkers(int count)
        {
            s_ActiveWorkers = Math.Clamp(count, 0, MaxWorkers);
            if (s_Workers != null) s_Signal.Release(s_Workers.Length);   // 쉬는 스레드가 다시 본다
        }

        // ---------------------------------------------------------------- 작업 스레드
        static void EnsureWorkers()
        {
            if (s_Workers != null) return;
            lock (s_Lock)
            {
                if (s_Workers != null) return;
                var workers = new Thread[MaxWorkers];
                for (int i = 0; i < workers.Length; i++)
                {
                    workers[i] = new Thread(WorkerMain, 256 * 1024) { IsBackground = true, Name = "Nova C# Job Worker " + (i + 1) };
                    workers[i].Start(i);
                }
                s_Workers = workers;
            }
        }

        static void WorkerMain(object o)
        {
            int index = (int)o;
            t_WorkerIndex = index + 1;
            try { if (Native.Api.Prof_SetThreadName != null) Native.Api.Prof_SetThreadName(ProfilerNames.Intern("C# Job Worker " + (index + 1))); } catch { }
            while (true)
            {
                if (index < ActiveWorkers)
                {
                    if (s_Queue.TryDequeue(out long item)) { RunTicket(item, true); continue; }
                    for (int s = 0; s < 64 && s_Queue.IsEmpty; s++) Thread.SpinWait(32);
                    if (!s_Queue.IsEmpty) continue;
                }
                Interlocked.Increment(ref s_Sleeping);
                if (s_Queue.IsEmpty || index >= ActiveWorkers) s_Signal.Wait(index >= ActiveWorkers ? 100 : -1);
                Interlocked.Decrement(ref s_Sleeping);
            }
        }

        static void Wake(int tickets)
        {
            int n = Math.Min(tickets, Volatile.Read(ref s_Sleeping));
            if (n > 0) s_Signal.Release(n);
        }

        // 표 하나 = 이 잡의 묶음을 다 떨어질 때까지 가져가 돈다
        static void RunTicket(long item, bool worker)
        {
            JobRecord r = s_Records[(int)(item >> 32)];
            if (r == null || r.Version != (int)item) return;
            bool was = t_InJob;
            t_InJob = true;
            int mode = ProfilerNames.Begin(r.ProfName, out long start, out ushort depth);
            int executed = 0;
            try
            {
                while (true)
                {
                    int begin = Interlocked.Add(ref r.Next, r.Batch) - r.Batch;
                    if (begin >= r.Length) break;
                    int end = Math.Min(begin + r.Batch, r.Length);
                    try { r.Exec(r, begin, end); }
                    catch (Exception e) { s_Errors.Enqueue((r.Name, e)); }
                    executed += end - begin;
                }
            }
            finally
            {
                ProfilerNames.End(mode, r.ProfName, start, depth);
                t_InJob = was;
            }
            if (worker) TempArena.CurrentOrNull?.Reset();   // 잡 안의 Temp 는 잡이 끝나면
            if (executed > 0 && Interlocked.Add(ref r.Done, executed) == r.Length) FinishJob(r);
            if (Interlocked.Decrement(ref r.Outstanding) == 0 && Volatile.Read(ref r.RecyclePending))
                lock (s_Lock) { if (r.RecyclePending && r.Outstanding == 0) RecycleNow(r); }
        }

        static void FinishJob(JobRecord r)
        {
            if (r.Finish != null)
            {
                try { r.Finish(r); }
                catch (Exception e) { s_Errors.Enqueue((r.Name, e)); }
            }
            int woken = 0;
            List<JobRecord> ready = null;
            lock (s_Lock)
            {
                Volatile.Write(ref r.State, JobRecord.kFinished);
                for (int i = 0; i < r.DependentCount; i++)
                {
                    JobRecord d = Get(r.Dependents[i]);
                    if (d != null && --d.PendingDeps == 0) (ready ??= new List<JobRecord>()).Add(d);
                }
                r.DependentCount = 0;
            }
            if (ready == null) return;
            foreach (JobRecord d in ready) woken += Kick(d);
            if (woken > 0) Wake(woken);
        }

        // 의존성이 다 끝난 잡: 표를 큐에 (길이 0 이면 바로 끝)
        static int Kick(JobRecord r)
        {
            if (r.Length <= 0) { FinishJob(r); return 0; }
            Volatile.Write(ref r.State, JobRecord.kQueued);
            r.Outstanding = r.Tickets;
            long key = r.Key;
            for (int i = 0; i < r.Tickets; i++) s_Queue.Enqueue(key);
            return r.Tickets;
        }

        static JobRecord Get(long key)
        {
            int index = (int)(key >> 32);
            if (index <= 0 || index >= s_RecordCount) return null;
            JobRecord r = s_Records[index];
            return r != null && r.Version == (int)key && r.State != JobRecord.kFree ? r : null;
        }
        static JobRecord Get(JobHandle h) => h.m_Index == 0 ? null : Get(((long)h.m_Index << 32) | (uint)h.m_Version);

        // s_Lock 안에서
        static JobRecord NewRecord()
        {
            int index;
            if (s_FreeRecords.Count > 0) index = s_FreeRecords.Pop();
            else
            {
                if (s_RecordCount == s_Records.Length) Array.Resize(ref s_Records, s_Records.Length * 2);
                index = s_RecordCount++;
                s_Records[index] = new JobRecord { Index = index, Version = 1 };
            }
            JobRecord r = s_Records[index];
            r.State = JobRecord.kWaiting;
            r.Synced = false;
            r.PendingDeps = 0;
            r.DepCount = 0;
            r.DependentCount = 0;
            r.LockCount = 0;
            r.Next = 0; r.Done = 0; r.Outstanding = 0;
            r.RecyclePending = false;
            r.Restrict = false;
            r.Finish = null;
            s_LiveRecords++;
            return r;
        }

        static void Recycle(JobRecord r)
        {
            if (Volatile.Read(ref r.Outstanding) > 0) { r.RecyclePending = true; return; }   // 남은 표가 끝나면 (RunTicket)
            RecycleNow(r);
        }
        static void RecycleNow(JobRecord r)
        {
            r.RecyclePending = false;
            if (r.Data != null) { NativeMemory.AlignedFree(r.Data); r.Data = null; }
            r.Exec = null;
            r.Finish = null;
            r.Name = null;
            r.Version++;
            r.State = JobRecord.kFree;
            s_FreeRecords.Push(r.Index);
            s_LiveRecords--;
        }

        static void AddDependency(JobRecord r, JobHandle dep)
        {
            JobRecord d = Get(dep);
            if (d == null) return;
            if (r.DepCount == r.Deps.Length) Array.Resize(ref r.Deps, r.Deps.Length * 2);
            r.Deps[r.DepCount++] = d.Key;
            if (d.State != JobRecord.kFinished)
            {
                if (d.DependentCount == d.Dependents.Length) Array.Resize(ref d.Dependents, d.Dependents.Length * 2);
                d.Dependents[d.DependentCount++] = r.Key;
                r.PendingDeps++;
            }
        }

        // ---------------------------------------------------------------- 예약
        internal static JobHandle Schedule<T>(ref T job, delegate*<JobRecord, int, int, void> exec, int length, int batch, bool parallel, JobHandle dependsOn) where T : struct
        {
            if (t_InJob) throw new InvalidOperationException("Jobs can only be scheduled from the main thread (not from inside a job).");
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "arrayLength must be >= 0");
            JobReflection<T>.CheckValid();
            EnsureWorkers();
            batch = Math.Max(batch, 1);
            JobRecord r;
            bool ready;
            lock (s_Lock)
            {
                r = NewRecord();
                try
                {
                    r.Data = NativeMemory.AlignedAlloc((nuint)Math.Max(SysUnsafe.SizeOf<T>(), 1), 16);
                    SysUnsafe.Write(r.Data, job);
                    r.Exec = exec;
                    r.Length = length;
                    r.Batch = batch;
                    int batches = (length + batch - 1) / batch;
                    r.Tickets = parallel ? Math.Max(1, Math.Min(batches, ActiveWorkers + 1)) : 1;
                    r.Name = JobReflection<T>.Name;
                    r.ProfName = JobReflection<T>.ProfName;
                    if (JobReflection<T>.HasDeallocate) r.Finish = &JobReflection<T>.DeallocateFields;
                    if (CollectionChecks.Enabled)
                    {
                        PatchAndLock(r, (byte*)r.Data, JobReflection<T>.Fields, parallel, dependsOn, true);
                        r.Restrict = parallel && JobReflection<T>.HasRestrict;
                    }
                }
                catch
                {
                    ReleaseLocks(r);
                    Recycle(r);
                    throw;
                }
                AddDependency(r, dependsOn);
                ready = r.PendingDeps == 0;
            }
            var handle = new JobHandle { m_Index = r.Index, m_Version = r.Version };
            if (ready)
            {
                int n = Kick(r);
                if (n > 0) Wake(n);
            }
            return handle;
        }

        /// <summary>여러 핸들을 하나로 (빈 잡 — 모두 끝나면 끝)</summary>
        internal static JobHandle Combine(ReadOnlySpan<JobHandle> jobs)
        {
            JobRecord r;
            bool ready;
            lock (s_Lock)
            {
                r = NewRecord();
                r.Length = 0;
                r.Tickets = 0;
                r.Name = "CombineDependencies";
                foreach (JobHandle h in jobs) AddDependency(r, h);
                ready = r.PendingDeps == 0;
            }
            var handle = new JobHandle { m_Index = r.Index, m_Version = r.Version };
            if (ready) FinishJob(r);
            return handle;
        }

        // 잡 사본의 컨테이너 핸들을 잡 사본으로 바꾸고 (권한 · kJob), 예약이면 잠근다. 같은 컨테이너를 쓰는 앞 잡이 의존성에 없으면 거부
        static void PatchAndLock(JobRecord r, byte* data, ContainerField[] fields, bool parallel, JobHandle dependsOn, bool scheduled)
        {
            if (fields.Length == 0) return;
            HashSet<long> ancestors = null;
            for (int i = 0; i < fields.Length; i++)
            {
                ContainerField f = fields[i];
                if (f.DisableSafety) continue;
                ref AtomicSafetyHandle h = ref SysUnsafe.AsRef<AtomicSafetyHandle>(data + f.SafetyOffset);
                SafetyNode* n = h.node;
                string jobName = r != null ? r.Name : f.JobName;
                if (n == null) throw new InvalidOperationException("The " + f.ContainerType + " " + f.Name + " has not been assigned or constructed. All containers must be valid when scheduling a job.");
                if ((h.version & ~AtomicSafetyHandle.kPermissionMask) != n->Version) throw new InvalidOperationException("The " + f.ContainerType + " " + f.Name + " has been deallocated. All containers must be valid when scheduling a job.");
                if ((n->Flags & AtomicSafetyHandle.kFlagTemp) != 0) throw new InvalidOperationException("The " + f.ContainerType + " " + f.Name + " uses Allocator.Temp and can not be passed to a job (Temp memory is reclaimed at the end of the frame). Use Allocator.TempJob or Allocator.Persistent.");
                bool write = !f.ReadOnly && (h.version & AtomicSafetyHandle.kNoWrite) == 0;
                if (parallel && write && !f.Atomic && !f.DisableParallelRestriction && !f.MinMax)
                    throw new InvalidOperationException(f.Name + " is not declared [ReadOnly] in a IJobParallelFor job. The container does not support parallel writing. Please use a more suitable container type (a ParallelWriter), or mark it [ReadOnly] / [NativeDisableParallelForRestriction].");
                // 한 잡 안의 별칭: 쓰는 필드와 같은 컨테이너를 가리키는 다른 필드
                for (int j = 0; j < i; j++)
                {
                    ContainerField g = fields[j];
                    if (g.DisableSafety) continue;
                    ref AtomicSafetyHandle o = ref SysUnsafe.AsRef<AtomicSafetyHandle>(data + g.SafetyOffset);
                    if (o.node != n) continue;
                    bool otherWrite = (o.version & AtomicSafetyHandle.kNoWrite) == 0;
                    if (write || otherWrite)
                        throw new InvalidOperationException("The writable " + f.ContainerType + " " + f.Name + " is the same " + f.ContainerType + " as " + g.Name + ", two containers may not be the same (aliasing).");
                }

                // 앞서 예약된 잡과의 충돌
                if (s_NodeLocks.TryGetValue((IntPtr)n, out NodeLocks locks))
                {
                    if (locks.Version != n->Version) { s_NodeLocks.Remove((IntPtr)n); locks = null; }
                }
                if (locks != null)
                {
                    if (locks.Writer != 0 && Get(locks.Writer) != null)
                    {
                        ancestors ??= Ancestors(dependsOn);
                        if (!ancestors.Contains(locks.Writer))
                            throw new InvalidOperationException("The previously scheduled job " + JobOf(locks.WriterName) + " writes to the " + f.ContainerType + " " + locks.WriterName + ". You are trying to schedule a new job " + jobName + ", which " + (write ? "writes to" : "reads from") + " the same " + f.ContainerType + " (via " + f.Name + "). To guarantee safety, you must include " + JobOf(locks.WriterName) + " as a dependency of the newly scheduled job.");
                    }
                    if (write)
                        foreach (var (job, name) in locks.Readers)
                        {
                            if (Get(job) == null) continue;
                            ancestors ??= Ancestors(dependsOn);
                            if (!ancestors.Contains(job))
                                throw new InvalidOperationException("The previously scheduled job " + JobOf(name) + " reads from the " + f.ContainerType + " " + name + ". You are trying to schedule a new job " + jobName + ", which writes to the same " + f.ContainerType + " (via " + f.Name + "). To guarantee safety, you must include " + JobOf(name) + " as a dependency of the newly scheduled job.");
                        }
                }

                // 잡 사본: 잠금을 건너뛰는 표시 + 권한
                int perm = h.version & (AtomicSafetyHandle.kNoWrite | AtomicSafetyHandle.kNoRead);
                if (!write) perm |= AtomicSafetyHandle.kNoWrite;
                if (f.WriteOnly) perm |= AtomicSafetyHandle.kNoRead;
                h.version = n->Version | AtomicSafetyHandle.kJob | perm;

                if (!scheduled) continue;
                n->JobLocks += write ? AtomicSafetyHandle.kWriteLock : 1;
                if (r.LockCount == r.Locks.Length) Array.Resize(ref r.Locks, r.Locks.Length * 2);
                r.Locks[r.LockCount++] = new JobRecord.Lock { Node = (IntPtr)n, Version = n->Version, Write = write, Field = f.Name };
                if (locks == null) s_NodeLocks[(IntPtr)n] = locks = new NodeLocks { Version = n->Version };
                if (write) { locks.Writer = r.Key; locks.WriterName = f.Name; locks.Readers.Clear(); }
                else locks.Readers.Add((r.Key, f.Name));
            }
        }

        static string JobOf(string field) { int dot = field.LastIndexOf('.'); return dot > 0 ? field.Substring(0, dot) : field; }

        // dependsOn 과 그 앞의 모든 잡 (아직 되쓰지 않은 것)
        static HashSet<long> Ancestors(JobHandle dependsOn)
        {
            var set = new HashSet<long>();
            JobRecord start = Get(dependsOn);
            if (start == null) return set;
            var stack = new Stack<JobRecord>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                JobRecord x = stack.Pop();
                if (!set.Add(x.Key)) continue;
                for (int i = 0; i < x.DepCount; i++) { JobRecord d = Get(x.Deps[i]); if (d != null) stack.Push(d); }
            }
            return set;
        }

        static void ReleaseLocks(JobRecord r)
        {
            for (int i = 0; i < r.LockCount; i++)
            {
                JobRecord.Lock l = r.Locks[i];
                var n = (SafetyNode*)l.Node;
                if (n->Version == l.Version) n->JobLocks -= l.Write ? AtomicSafetyHandle.kWriteLock : 1;
                if (s_NodeLocks.TryGetValue(l.Node, out NodeLocks locks))
                {
                    if (locks.Writer == r.Key) { locks.Writer = 0; locks.WriterName = null; }
                    for (int k = locks.Readers.Count - 1; k >= 0; k--) if (locks.Readers[k].job == r.Key) locks.Readers.RemoveAt(k);
                    if ((locks.Writer == 0 && locks.Readers.Count == 0) || locks.Version != n->Version) s_NodeLocks.Remove(l.Node);
                }
            }
            r.LockCount = 0;
        }

        /// <summary>잠긴 컨테이너를 메인 스레드가 만질 때의 문구</summary>
        internal static string DescribeLock(IntPtr node, string action)
        {
            lock (s_Lock)
            {
                if (s_NodeLocks.TryGetValue(node, out NodeLocks locks))
                {
                    if (locks.Writer != 0)
                        return "The previously scheduled job " + JobOf(locks.WriterName) + " writes to the native container " + locks.WriterName + ". You must call JobHandle.Complete() on the job " + JobOf(locks.WriterName) + ", before you can " + action + " the native container safely.";
                    if (locks.Readers.Count > 0)
                        return "The previously scheduled job " + JobOf(locks.Readers[0].name) + " reads from the native container " + locks.Readers[0].name + ". You must call JobHandle.Complete() on the job " + JobOf(locks.Readers[0].name) + ", before you can " + action + " the native container safely.";
                }
            }
            return "A scheduled job uses this native container. You must call JobHandle.Complete() on it before you can " + action + " the native container safely.";
        }

        // ---------------------------------------------------------------- Run (지금 이 스레드에서)
        internal static void BeginRun<T>(ref T job, out bool wasInJob) where T : struct
        {
            JobReflection<T>.CheckValid();
            if (CollectionChecks.Enabled)
            {
                ContainerField[] fields = JobReflection<T>.Fields;
                if (fields.Length > 0)
                    lock (s_Lock)
                    {
                        // 예약된 잡이 쓰는 컨테이너는 Run 할 수 없다 (Complete 먼저)
                        byte* data = (byte*)SysUnsafe.AsPointer(ref job);
                        foreach (ContainerField f in fields)
                        {
                            if (f.DisableSafety) continue;
                            SafetyNode* n = SysUnsafe.AsRef<AtomicSafetyHandle>(data + f.SafetyOffset).node;
                            if (n != null && n->JobLocks != 0 && (n->JobLocks >= AtomicSafetyHandle.kWriteLock || !f.ReadOnly))
                                throw new InvalidOperationException(DescribeLock((IntPtr)n, "run a job that uses"));
                        }
                        PatchAndLock(null, data, fields, false, default, false);
                    }
            }
            wasInJob = t_InJob;
            t_InJob = true;
        }
        internal static void EndRun(bool wasInJob) { t_InJob = wasInJob; FlushErrors(); }

        // ---------------------------------------------------------------- 기다리기
        internal static bool IsCompleted(JobHandle h)
        {
            JobRecord r = Get(h);
            return r == null || Volatile.Read(ref r.State) == JobRecord.kFinished;
        }

        internal static void Complete(JobHandle h)
        {
            if (h.m_Index == 0) return;
            if (t_InJob) throw new InvalidOperationException("JobHandle.Complete can not be called from inside a job. Use the JobHandle as a dependency instead.");
            JobRecord r;
            lock (s_Lock) r = Get(h);
            if (r == null) return;
            WaitFor(r);
            lock (s_Lock) { if (r.Version == h.m_Version) Sync(r); }
            FlushErrors();
        }

        // 끝날 때까지 큐의 다른 잡을 돕는다 (Sleep(1) 없이 — Windows 는 15 ms 가 될 수 있다)
        static void WaitFor(JobRecord r)
        {
            int version = r.Version;
            var spin = new SpinWait();
            while (r.Version == version && Volatile.Read(ref r.State) != JobRecord.kFinished)
            {
                if (s_Queue.TryDequeue(out long item)) { RunTicket(item, false); spin = default; }
                else spin.SpinOnce(-1);
            }
        }

        // 끝난 잡과 그 앞의 잡을 모두 "기다린 것" 으로 (잠금 풀기) 하고 되쓴다 — s_Lock 안에서
        static void Sync(JobRecord r)
        {
            var todo = new Stack<JobRecord>();
            var done = new List<JobRecord>();
            todo.Push(r);
            while (todo.Count > 0)
            {
                JobRecord x = todo.Pop();
                if (x.Synced || x.State != JobRecord.kFinished) continue;
                x.Synced = true;
                ReleaseLocks(x);
                for (int i = 0; i < x.DepCount; i++) { JobRecord d = Get(x.Deps[i]); if (d != null) todo.Push(d); }
                done.Add(x);
            }
            foreach (JobRecord x in done) Recycle(x);
        }

        /// <summary>모든 잡을 끝내고 잠금을 푼다 (Play 끝 · 스크립트 다시 읽기 — 게임 코드가 돌고 있으면 안 된다)</summary>
        internal static void CompleteAll()
        {
            while (true)
            {
                JobRecord pending = null;
                lock (s_Lock)
                {
                    for (int i = 1; i < s_RecordCount; i++)
                    {
                        JobRecord x = s_Records[i];
                        if (x == null || x.State == JobRecord.kFree) continue;
                        if (x.State != JobRecord.kFinished) { pending = x; break; }
                    }
                    if (pending == null)
                    {
                        for (int i = 1; i < s_RecordCount; i++)
                        {
                            JobRecord x = s_Records[i];
                            if (x != null && x.State == JobRecord.kFinished && !x.Synced) Sync(x);
                        }
                        s_NodeLocks.Clear();
                        break;
                    }
                }
                WaitFor(pending);
            }
            while (s_Queue.TryDequeue(out long item)) RunTicket(item, false);   // 끝난 잡의 남은 표 (되쓰기를 마저)
            FlushErrors();
        }

        /// <summary>잡 안 예외는 메인 스레드에서 Console 로 (Complete · 프레임 시작)</summary>
        internal static void FlushErrors()
        {
            if (t_InJob || s_Errors.IsEmpty) return;
            while (s_Errors.TryDequeue(out var e))
            {
                string stack = Bridge.FormatStack(new System.Diagnostics.StackTrace(e.e, true), out string file, out int line);
                Bridge.Log(2, "Exception in job " + e.job + ": " + e.e.GetType().Name + ": " + e.e.Message, stack, file, line);
            }
        }

        internal static bool IsDependencyOrSynced(JobHandle job, JobHandle dependsOn)
        {
            lock (s_Lock)
            {
                if (Get(dependsOn) == null) return true;
                return Ancestors(job).Contains(((long)dependsOn.m_Index << 32) | (uint)dependsOn.m_Version);
            }
        }
    }
}
