using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SysUnsafe = System.Runtime.CompilerServices.Unsafe;

namespace NovaEngine.Collections.LowLevel.Unsafe
{
    /// <summary>비관리 메모리 다루기 (Unity.Collections.LowLevel.Unsafe.UnsafeUtility)</summary>
    public static unsafe class UnsafeUtility
    {
        /// <summary>Temp 는 이 스레드의 프레임 메모리에서 (Free 는 아무것도 안 함), TempJob · Persistent 는 운영체제 힙에서</summary>
        public static void* Malloc(long size, int alignment, Allocator allocator)
        {
            if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
            int align = Math.Max(alignment, 16);
            switch (allocator)
            {
                case Allocator.Temp:
                    return TempArena.Current.Alloc(size, align);
                case Allocator.TempJob:
                case Allocator.Persistent:
                {
                    void* p = NativeMemory.AlignedAlloc((nuint)Math.Max(size, 1), (nuint)align);
                    if (p == null) throw new OutOfMemoryException();
                    AllocationTracker.Add(p, size, allocator);
                    return p;
                }
                default:
                    throw new ArgumentException("Allocator " + allocator + " can not allocate memory", nameof(allocator));
            }
        }

        public static void Free(void* memory, Allocator allocator)
        {
            if (memory == null) return;
            switch (allocator)
            {
                case Allocator.Temp:
                    return;   // 다음 프레임 시작에 한꺼번에
                case Allocator.TempJob:
                case Allocator.Persistent:
                    AllocationTracker.Remove(memory);
                    NativeMemory.AlignedFree(memory);
                    return;
                default:
                    throw new ArgumentException("Allocator " + allocator + " can not free memory", nameof(allocator));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void MemCpy(void* destination, void* source, long size) => Buffer.MemoryCopy(source, destination, size, size);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void MemMove(void* destination, void* source, long size) => Buffer.MemoryCopy(source, destination, size, size);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void MemClear(void* destination, long size) => NativeMemory.Clear(destination, (nuint)size);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void MemSet(void* destination, byte value, long size) => NativeMemory.Fill(destination, (nuint)size, value);
        public static int MemCmp(void* ptr1, void* ptr2, long size)
        {
            byte* a = (byte*)ptr1, b = (byte*)ptr2;
            while (size > 0)
            {
                int n = (int)Math.Min(size, int.MaxValue);
                int c = new ReadOnlySpan<byte>(a, n).SequenceCompareTo(new ReadOnlySpan<byte>(b, n));
                if (c != 0) return c;
                a += n; b += n; size -= n;
            }
            return 0;
        }
        /// <summary>source 의 size 바이트를 count 번 이어 붙인다</summary>
        public static void MemCpyReplicate(void* destination, void* source, int size, int count)
        {
            byte* d = (byte*)destination;
            for (int i = 0; i < count; i++, d += size) Buffer.MemoryCopy(source, d, size, size);
        }
        /// <summary>간격이 있는 복사 (원소 elementSize 바이트, count 개)</summary>
        public static void MemCpyStride(void* destination, int destinationStride, void* source, int sourceStride, int elementSize, int count)
        {
            byte* d = (byte*)destination, s = (byte*)source;
            for (int i = 0; i < count; i++, d += destinationStride, s += sourceStride) Buffer.MemoryCopy(s, d, elementSize, elementSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static int SizeOf<T>() where T : unmanaged => sizeof(T);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static int AlignOf<T>() where T : unmanaged => sizeof(AlignHelper<T>) - sizeof(T);
        [StructLayout(LayoutKind.Sequential)] struct AlignHelper<T> where T : unmanaged { public byte b; public T t; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T ReadArrayElement<T>(void* source, int index) where T : unmanaged => ((T*)source)[index];
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T ReadArrayElementWithStride<T>(void* source, int index, int stride) where T : unmanaged => *(T*)((byte*)source + (long)index * stride);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void WriteArrayElement<T>(void* destination, int index, T value) where T : unmanaged => ((T*)destination)[index] = value;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void WriteArrayElementWithStride<T>(void* destination, int index, int stride, T value) where T : unmanaged => *(T*)((byte*)destination + (long)index * stride) = value;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ref T ArrayElementAsRef<T>(void* ptr, int index) where T : unmanaged => ref ((T*)ptr)[index];
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ref T AsRef<T>(void* ptr) where T : unmanaged => ref *(T*)ptr;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void* AddressOf<T>(ref T output) where T : unmanaged => SysUnsafe.AsPointer(ref output);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void CopyPtrToStructure<T>(void* ptr, out T output) where T : unmanaged => output = *(T*)ptr;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void CopyStructureToPtr<T>(ref T input, void* ptr) where T : unmanaged => *(T*)ptr = input;
        /// <summary>참조 (class · string · 관리 배열) 가 없는 형식인지 — NativeContainer 에 넣을 수 있는지</summary>
        public static bool IsUnmanaged<T>() => !RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        public static bool IsUnmanaged(Type type)
        {
            if (type.IsPrimitive || type.IsPointer || type.IsEnum) return true;
            if (!type.IsValueType) return false;
            foreach (var f in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                if (!IsUnmanaged(f.FieldType)) return false;
            return true;
        }
    }

    // 안전 노드: 컨테이너 하나 (와 그 복사본 모두) 가 가리키는 비관리 칸. Dispose 하면 Version 이 올라 옛 복사본의 핸들이 맞지 않게 된다
    [StructLayout(LayoutKind.Sequential)]
    internal struct SafetyNode
    {
        public int Version;      // kStep 씩 오른다 (아래 두 비트는 핸들 쪽 권한 표시)
        public int Flags;
        public long WriterJob;   // C# Job System (B-2): 이 컨테이너에 쓰는 잡
        public long ReaderJobs;  //                      읽는 잡들 (묶음)
        public long Reserved;
    }

    /// <summary>
    /// 컨테이너 안전 핸들 (Unity.Collections.LowLevel.Unsafe.AtomicSafetyHandle) — Dispose 뒤 사용 · 읽기 전용에 쓰기를 잡는다.
    /// 안전 검사가 꺼지면 (플레이어) 만들지 않고 검사도 하지 않는다.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct AtomicSafetyHandle
    {
        internal SafetyNode* node;
        internal int version;

        internal const int kNoWrite = 1, kNoRead = 2, kPermissionMask = 3, kStep = 4;

        public static AtomicSafetyHandle Create() => SafetyPool.Create();
        public static void Release(AtomicSafetyHandle handle) => SafetyPool.Release(handle);
        public static bool IsDefaultValue(in AtomicSafetyHandle handle) => handle.node == null;
        /// <summary>아직 Dispose 되지 않았는지</summary>
        public static bool IsHandleValid(in AtomicSafetyHandle handle) => handle.node != null && (handle.version & ~kPermissionMask) == handle.node->Version;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CheckReadAndThrow(in AtomicSafetyHandle handle)
        {
            if (handle.node != null && (handle.version & ~kNoWrite) != handle.node->Version) ThrowRead(handle);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CheckWriteAndThrow(in AtomicSafetyHandle handle)
        {
            if (handle.node != null && handle.version != handle.node->Version) ThrowWrite(handle);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CheckExistsAndThrow(in AtomicSafetyHandle handle)
        {
            if (handle.node != null && (handle.version & ~kPermissionMask) != handle.node->Version) ThrowDisposed();
        }
        /// <summary>Dispose 할 수 있는지 (살아 있고 읽기 전용 복사본이 아님)</summary>
        public static void CheckDeallocateAndThrow(in AtomicSafetyHandle handle)
        {
            if (handle.node == null) return;
            if ((handle.version & ~kPermissionMask) != handle.node->Version) ThrowDisposed();
            if ((handle.version & kNoWrite) != 0) throw new InvalidOperationException("The native container can not be disposed because it is read-only (AsReadOnly or [ReadOnly] in a job).");
        }
        /// <summary>읽기만 되는 복사본</summary>
        public static AtomicSafetyHandle AsReadOnly(in AtomicSafetyHandle handle) => new AtomicSafetyHandle { node = handle.node, version = handle.version | kNoWrite };
        /// <summary>쓰기만 되는 복사본</summary>
        public static AtomicSafetyHandle AsWriteOnly(in AtomicSafetyHandle handle) => new AtomicSafetyHandle { node = handle.node, version = handle.version | kNoRead };

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowRead(in AtomicSafetyHandle handle)
        {
            if ((handle.version & ~kPermissionMask) != handle.node->Version) ThrowDisposed();
            throw new InvalidOperationException("The native container is write-only ([WriteOnly]); it can not be read.");
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowWrite(in AtomicSafetyHandle handle)
        {
            if ((handle.version & ~kPermissionMask) != handle.node->Version) ThrowDisposed();
            throw new InvalidOperationException("The native container is read-only (AsReadOnly or [ReadOnly] in a job); it can not be written.");
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowDisposed() => throw new ObjectDisposedException("The native container has been deallocated (Dispose, or a Temp allocation from an earlier frame); all copies of it are now invalid.");
    }

    // 안전 노드 풀: 덩어리로 받아 두고 되쓴다 (Version 은 계속 올라가므로 옛 핸들은 다시 맞지 않는다)
    internal static unsafe class SafetyPool
    {
        const int kChunk = 1024;
        static readonly object s_Lock = new object();
        static readonly Stack<IntPtr> s_Free = new Stack<IntPtr>();
        static int s_Live;

        internal static int Live => s_Live;

        public static AtomicSafetyHandle Create()
        {
            SafetyNode* n;
            lock (s_Lock)
            {
                if (s_Free.Count == 0)
                {
                    var chunk = (SafetyNode*)NativeMemory.AllocZeroed((nuint)(kChunk * sizeof(SafetyNode)));
                    for (int i = kChunk - 1; i >= 0; i--) { chunk[i].Version = AtomicSafetyHandle.kStep; s_Free.Push((IntPtr)(chunk + i)); }
                }
                n = (SafetyNode*)s_Free.Pop();
                s_Live++;
            }
            n->Flags = 0; n->WriterJob = 0; n->ReaderJobs = 0;
            return new AtomicSafetyHandle { node = n, version = n->Version };
        }

        public static void Release(AtomicSafetyHandle h)
        {
            if (h.node == null) return;
            lock (s_Lock)
            {
                if ((h.version & ~AtomicSafetyHandle.kPermissionMask) != h.node->Version) return;   // 이미 풀림
                h.node->Version += AtomicSafetyHandle.kStep;
                s_Free.Push((IntPtr)h.node);
                s_Live--;
            }
        }
    }

    // Temp: 스레드마다 덩어리 메모리에서 잘라 쓰고 프레임 시작에 한꺼번에 되돌린다 (Unity 의 Temp 와 같은 쓰임 — 가장 빠르다)
    internal sealed unsafe class TempArena
    {
        [ThreadStatic] static TempArena t_Arena;
        internal static TempArena Current => t_Arena ??= new TempArena();
        internal static TempArena CurrentOrNull => t_Arena;

        const int kChunk = 256 * 1024, kBig = 32 * 1024;
        byte* m_Chunk;
        long m_Offset;
        readonly List<IntPtr> m_Retired = new List<IntPtr>();   // 다 쓴 덩어리
        readonly List<IntPtr> m_Big = new List<IntPtr>();       // 큰 것은 따로
        readonly List<(IntPtr node, int version)> m_Handles = new List<(IntPtr, int)>();
        long m_Bytes;

        internal long BytesThisFrame => m_Bytes;

        public void* Alloc(long size, int align)
        {
            m_Bytes += size;
            if (size > kBig)
            {
                void* p = NativeMemory.AlignedAlloc((nuint)size, (nuint)align);
                if (p == null) throw new OutOfMemoryException();
                m_Big.Add((IntPtr)p);
                return p;
            }
            if (m_Chunk == null) m_Chunk = (byte*)NativeMemory.AlignedAlloc(kChunk, 64);
            long start = (m_Offset + align - 1) & ~(long)(align - 1);
            if (start + size > kChunk)
            {
                m_Retired.Add((IntPtr)m_Chunk);
                m_Chunk = (byte*)NativeMemory.AlignedAlloc(kChunk, 64);
                start = 0;
            }
            m_Offset = start + Math.Max(size, 1);
            return m_Chunk + start;
        }

        public void Track(AtomicSafetyHandle h) { if (h.node != null) m_Handles.Add(((IntPtr)h.node, h.version)); }

        /// <summary>이 스레드의 Temp 를 모두 되돌린다 — 아직 살아 있는 Temp 컨테이너는 무효가 된다</summary>
        public void Reset()
        {
            foreach (var (node, version) in m_Handles)
                SafetyPool.Release(new AtomicSafetyHandle { node = (SafetyNode*)node, version = version });
            m_Handles.Clear();
            foreach (IntPtr p in m_Big) NativeMemory.AlignedFree((void*)p);
            m_Big.Clear();
            foreach (IntPtr p in m_Retired) NativeMemory.AlignedFree((void*)p);
            m_Retired.Clear();
            m_Offset = 0;
            m_Bytes = 0;
        }
    }

    // TempJob · Persistent 할당 기록 (NativeLeakDetection) — 컨테이너의 안전 핸들도 붙여 두어, 남은 것을 풀 때 옛 복사본이 무효가 되게
    internal static unsafe class AllocationTracker
    {
        struct Record
        {
            public Allocator Allocator;
            public long Size;
            public int Frame;
            public bool Warned;
            public string Stack;
            public AtomicSafetyHandle Safety;
            public string Owner;
        }

        static readonly object s_Lock = new object();
        static readonly Dictionary<IntPtr, Record> s_Live = new Dictionary<IntPtr, Record>();
        static long s_Bytes;
        internal static int s_Frame;

        internal static int LiveCount { get { lock (s_Lock) return s_Live.Count; } }
        internal static long LiveBytes { get { lock (s_Lock) return s_Bytes; } }

        public static void Add(void* p, long size, Allocator allocator)
        {
            NativeLeakDetectionMode mode = NativeLeakDetection.Mode;
            if (mode == NativeLeakDetectionMode.Disabled) return;
            var r = new Record { Allocator = allocator, Size = size, Frame = s_Frame };
            if (mode == NativeLeakDetectionMode.EnabledWithStackTrace) r.Stack = new StackTrace(2, true).ToString();
            lock (s_Lock) { s_Live[(IntPtr)p] = r; s_Bytes += size; }
        }

        public static void Remove(void* p)
        {
            lock (s_Lock)
                if (s_Live.Remove((IntPtr)p, out Record r)) s_Bytes -= r.Size;
        }

        /// <summary>이 할당을 쓰는 컨테이너의 핸들 · 이름</summary>
        public static void Attach(void* p, AtomicSafetyHandle h, string owner)
        {
            lock (s_Lock)
                if (s_Live.TryGetValue((IntPtr)p, out Record r)) { r.Safety = h; r.Owner = owner; s_Live[(IntPtr)p] = r; }
        }

        /// <summary>프레임마다: 4 프레임이 넘은 TempJob 을 한 번 알린다 (Unity 와 같은 규칙)</summary>
        public static void BeginFrame()
        {
            s_Frame++;
            List<string> late = null;
            lock (s_Lock)
            {
                if (s_Live.Count == 0) return;
                foreach (var kv in s_Live)
                {
                    Record r = kv.Value;
                    if (r.Allocator != Allocator.TempJob || r.Warned || s_Frame - r.Frame <= 4) continue;
                    r.Warned = true;
                    s_Live[kv.Key] = r;
                    (late ??= new List<string>()).Add(r.Owner ?? "allocation");
                }
            }
            if (late != null)
                Debug.LogWarning("Internal: JobTempAlloc has allocations that are more than the maximum lifespan of 4 frames old - this is not allowed and likely a leak (" + string.Join(", ", late) + "). Dispose Allocator.TempJob containers within 4 frames, or use Allocator.Persistent.");
        }

        /// <summary>남은 것을 모두 알리고 푼다 (Play 끝 · 스크립트 다시 읽기) — 돌려준 값 = 푼 개수</summary>
        public static int ReportAndFreeAll(string when)
        {
            List<KeyValuePair<IntPtr, Record>> left;
            lock (s_Lock)
            {
                if (s_Live.Count == 0) return 0;
                left = new List<KeyValuePair<IntPtr, Record>>(s_Live);
                s_Live.Clear();
                s_Bytes = 0;
            }
            long bytes = 0;
            int containers = 0;
            string firstStack = null;
            var names = new Dictionary<string, int>();
            foreach (var kv in left)
            {
                Record r = kv.Value;
                bytes += r.Size;
                if (r.Owner != null) { containers++; names[r.Owner] = names.TryGetValue(r.Owner, out int c) ? c + 1 : 1; }
                firstStack ??= r.Stack;
                SafetyPool.Release(r.Safety);   // 옛 복사본은 이제 ObjectDisposedException
                NativeMemory.AlignedFree((void*)kv.Key);
            }
            var parts = new List<string>();
            foreach (var kv in names) parts.Add(kv.Value + " " + kv.Key);
            string what = containers > 0 ? string.Join(", ", parts) : left.Count + " allocation(s)";
            string msg = "A Native Collection has not been disposed, resulting in a memory leak (" + what + ", " + bytes + " bytes, freed at " + when + ").";
            msg += firstStack != null ? "\nAllocated from:\n" + firstStack : " Set NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace to see where it was allocated.";
            Debug.LogError(msg);
            return left.Count;
        }
    }

    // 엔진 쪽 시점 (Bridge 가 부른다)
    internal static class CollectionsRuntime
    {
        /// <summary>프레임 시작: 메인 스레드 Temp 되돌림, TempJob 수명 검사</summary>
        internal static void BeginFrame()
        {
            TempArena.CurrentOrNull?.Reset();
            AllocationTracker.BeginFrame();
        }
        /// <summary>이 스레드의 Temp 만 되돌림 (Play 밖 nova exec 뒤)</summary>
        internal static void ResetTemp() => TempArena.CurrentOrNull?.Reset();
        /// <summary>Play 끝 · 게임 어셈블리 내림: 남은 TempJob · Persistent 를 알리고 푼다</summary>
        internal static void EndSession(string when)
        {
            TempArena.CurrentOrNull?.Reset();
            AllocationTracker.ReportAndFreeAll(when);
        }
    }

    // 컨테이너 공통: 안전 핸들 만들기 (Temp 는 프레임 메모리에, 나머지는 할당 기록에 붙인다)
    internal static unsafe class CollectionHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static AtomicSafetyHandle CreateSafety(void* allocation, Allocator allocator, string owner)
        {
            if (!CollectionChecks.Enabled) return default;
            AtomicSafetyHandle h = AtomicSafetyHandle.Create();
            if (allocator == Allocator.Temp) TempArena.Current.Track(h);
            else AllocationTracker.Attach(allocation, h, owner);
            return h;
        }

        internal static void CheckAllocator(Allocator allocator)
        {
            if (allocator <= Allocator.None) throw new ArgumentException("Allocator must be Temp, TempJob or Persistent", nameof(allocator));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowIndex(int index, int length) => throw new IndexOutOfRangeException("Index " + index + " is out of range of '" + length + "' Length.");
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowRestrictedIndex(int index, int min, int max) => throw new IndexOutOfRangeException("Index " + index + " is out of restricted IJobParallelFor range [" + min + "..." + max + "] in ReadWriteBuffer.\nReadWriteBuffers are restricted to only read & write the element at the job index. You can use double buffering strategies to avoid race conditions due to reading & writing in parallel to the same elements from a job.");
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowNotCreated() => throw new NullReferenceException("The native container has not been created (default value). Construct it with an Allocator first.");
    }
}
