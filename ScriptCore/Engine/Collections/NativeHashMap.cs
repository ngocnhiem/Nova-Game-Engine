using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using NovaEngine.Collections.LowLevel.Unsafe;
using NovaEngine.Jobs;

namespace NovaEngine.Collections
{
    /// <summary>NativeHashMap 을 돌 때의 한 쌍 (Unity.Collections.KVPair)</summary>
    public readonly struct KVPair<TKey, TValue> where TKey : unmanaged where TValue : unmanaged
    {
        readonly TKey m_Key;
        readonly TValue m_Value;
        internal KVPair(TKey key, TValue value) { m_Key = key; m_Value = value; }
        public TKey Key => m_Key;
        public TValue Value => m_Value;
        public void GetKeyValue(out TKey key, out TValue value) { key = m_Key; value = m_Value; }
        public override string ToString() => "[" + m_Key + ", " + m_Value + "]";
    }

    // 해시 맵 머리 + 한 덩어리 메모리 (키 | 값 | 다음 | 버킷) — Unity UnsafeHashMap 과 같은 사슬 방식
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct HashMapData
    {
        public byte* Block;
        public byte* Keys;
        public byte* Values;
        public int* Next;
        public int* Buckets;
        public int Count;
        public int Capacity;       // 칸 수
        public int BucketMask;     // 버킷 수 - 1 (2 의 거듭제곱)
        public int AllocatedIndex; // 한 번도 안 쓴 칸의 시작
        public int FreeList;       // 지운 칸 사슬 (-1 = 없음)
        public int Lock;           // ParallelWriter 의 회전 잠금
    }

    internal static unsafe class SpinLockPtr
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Enter(int* l)
        {
            if (Interlocked.CompareExchange(ref *l, 1, 0) == 0) return;
            var spin = new SpinWait();
            while (Interlocked.CompareExchange(ref *l, 1, 0) != 0) spin.SpinOnce(-1);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Exit(int* l) => Volatile.Write(ref *l, 0);
    }

    internal static unsafe class HashMapCore<TKey, TValue> where TKey : unmanaged, IEquatable<TKey> where TValue : unmanaged
    {
        static long Align(long v) => (v + 15) & ~15L;

        public static void Allocate(HashMapData* d, int capacity, Allocator allocator)
        {
            capacity = Math.Max(capacity, 4);
            int buckets = Math.Max(math_ceilpow2(capacity * 2), 8);
            long keys = Align((long)capacity * sizeof(TKey)), values = Align((long)capacity * sizeof(TValue)), next = Align((long)capacity * 4);
            byte* block = (byte*)UnsafeUtility.Malloc(keys + values + next + (long)buckets * 4, 16, allocator);
            d->Block = block;
            d->Keys = block;
            d->Values = block + keys;
            d->Next = (int*)(block + keys + values);
            d->Buckets = (int*)(block + keys + values + next);
            d->Capacity = capacity;
            d->BucketMask = buckets - 1;
            Clear(d);   // Lock 은 그대로 (ParallelWriter 가 잠근 채 늘린다)
        }

        static int math_ceilpow2(int x) { x -= 1; x |= x >> 1; x |= x >> 2; x |= x >> 4; x |= x >> 8; x |= x >> 16; return x + 1; }

        public static void Clear(HashMapData* d)
        {
            UnsafeUtility.MemSet(d->Buckets, 0xFF, (long)(d->BucketMask + 1) * 4);
            d->Count = 0;
            d->AllocatedIndex = 0;
            d->FreeList = -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Bucket(HashMapData* d, in TKey key)
        {
            uint h = (uint)key.GetHashCode();
            h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15;   // 정수 키 (0, 1, 2 ...) 도 고르게
            return (int)(h & (uint)d->BucketMask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Find(HashMapData* d, in TKey key)
        {
            int i = d->Buckets[Bucket(d, key)];
            TKey* keys = (TKey*)d->Keys;
            while (i >= 0)
            {
                if (keys[i].Equals(key)) return i;
                i = d->Next[i];
            }
            return -1;
        }

        /// <summary>새 칸 (있으면 -1 이 아니라 기존 칸을 돌려주고 added = false)</summary>
        public static int Add(HashMapData* d, in TKey key, Allocator allocator, out bool added)
        {
            int found = Find(d, key);
            if (found >= 0) { added = false; return found; }
            int slot;
            if (d->FreeList >= 0) { slot = d->FreeList; d->FreeList = d->Next[slot]; }
            else
            {
                if (d->AllocatedIndex >= d->Capacity) Resize(d, d->Capacity * 2, allocator);
                slot = d->AllocatedIndex++;
            }
            ((TKey*)d->Keys)[slot] = key;
            int b = Bucket(d, key);
            d->Next[slot] = d->Buckets[b];
            d->Buckets[b] = slot;
            d->Count++;
            added = true;
            return slot;
        }

        public static bool Remove(HashMapData* d, in TKey key)
        {
            int b = Bucket(d, key);
            int i = d->Buckets[b], prev = -1;
            TKey* keys = (TKey*)d->Keys;
            while (i >= 0)
            {
                if (keys[i].Equals(key))
                {
                    if (prev < 0) d->Buckets[b] = d->Next[i]; else d->Next[prev] = d->Next[i];
                    d->Next[i] = d->FreeList;
                    d->FreeList = i;
                    d->Count--;
                    return true;
                }
                prev = i;
                i = d->Next[i];
            }
            return false;
        }

        /// <summary>쓰는 칸만 앞으로 모아 새 크기로</summary>
        public static void Resize(HashMapData* d, int capacity, Allocator allocator)
        {
            capacity = Math.Max(capacity, d->Count);
            HashMapData old = *d;
            Allocate(d, capacity, allocator);
            TKey* ok = (TKey*)old.Keys; TValue* ov = (TValue*)old.Values;
            TKey* nk = (TKey*)d->Keys; TValue* nv = (TValue*)d->Values;
            int n = 0;
            for (int b = 0; b <= old.BucketMask; b++)
                for (int i = old.Buckets[b]; i >= 0; i = old.Next[i])
                {
                    nk[n] = ok[i]; nv[n] = ov[i];
                    int nb = Bucket(d, nk[n]);
                    d->Next[n] = d->Buckets[nb];
                    d->Buckets[nb] = n;
                    n++;
                }
            d->Count = n;
            d->AllocatedIndex = n;
            UnsafeUtility.Free(old.Block, allocator);
        }

        public static void Dispose(HashMapData* d, Allocator allocator)
        {
            if (d->Block != null) UnsafeUtility.Free(d->Block, allocator);
            d->Block = null;
        }

        // 쓰는 칸을 차례로 (버킷 → 사슬)
        public struct Iterator
        {
            HashMapData* m_Data;
            int m_Bucket, m_Slot;
            public Iterator(HashMapData* d) { m_Data = d; m_Bucket = -1; m_Slot = -1; }
            public int Slot => m_Slot;
            public bool MoveNext()
            {
                if (m_Slot >= 0) m_Slot = m_Data->Next[m_Slot];
                while (m_Slot < 0)
                {
                    if (++m_Bucket > m_Data->BucketMask) return false;
                    m_Slot = m_Data->Buckets[m_Bucket];
                }
                return true;
            }
        }
    }

    /// <summary>
    /// 비관리 해시 맵 (Unity.Collections.NativeHashMap). 키는 IEquatable 을 구현하는 비관리 형식 (int, int2, FixedString ...).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    [NativeContainer]
    public unsafe struct NativeHashMap<TKey, TValue> : IDisposable, IEnumerable<KVPair<TKey, TValue>>
        where TKey : unmanaged, IEquatable<TKey> where TValue : unmanaged
    {
        internal HashMapData* m_Data;
        internal Allocator m_AllocatorLabel;
        internal AtomicSafetyHandle m_Safety;

        public NativeHashMap(int initialCapacity, Allocator allocator)
        {
            CollectionHelper.CheckAllocator(allocator);
            if (initialCapacity < 0) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
            m_Data = (HashMapData*)UnsafeUtility.Malloc(sizeof(HashMapData), 16, allocator);
            m_Data->Lock = 0;
            m_AllocatorLabel = allocator;
            HashMapCore<TKey, TValue>.Allocate(m_Data, initialCapacity, allocator);
            m_Safety = CollectionHelper.CreateSafety(m_Data, allocator, "NativeHashMap<" + typeof(TKey).Name + "," + typeof(TValue).Name + ">");
        }

        public readonly bool IsCreated => m_Data != null;
        public readonly bool IsEmpty { get { CheckRead(); return m_Data->Count == 0; } }
        public readonly int Count { get { CheckRead(); return m_Data->Count; } }
        public int Capacity
        {
            readonly get { CheckRead(); return m_Data->Capacity; }
            set { CheckWrite(); if (value != m_Data->Capacity) HashMapCore<TKey, TValue>.Resize(m_Data, value, m_AllocatorLabel); }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void CheckRead()
        {
            if (!CollectionChecks.Enabled) return;
            if (m_Data == null) CollectionHelper.ThrowNotCreated();
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void CheckWrite()
        {
            if (!CollectionChecks.Enabled) return;
            if (m_Data == null) CollectionHelper.ThrowNotCreated();
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
        }

        /// <summary>없으면 넣고 true, 있으면 그대로 두고 false</summary>
        public bool TryAdd(TKey key, TValue item)
        {
            CheckWrite();
            int slot = HashMapCore<TKey, TValue>.Add(m_Data, key, m_AllocatorLabel, out bool added);
            if (added) ((TValue*)m_Data->Values)[slot] = item;
            return added;
        }
        /// <summary>넣기 — 이미 있으면 ArgumentException</summary>
        public void Add(TKey key, TValue item)
        {
            if (!TryAdd(key, item)) throw new ArgumentException("An item with the same key has already been added: " + key);
        }
        public bool Remove(TKey key) { CheckWrite(); return HashMapCore<TKey, TValue>.Remove(m_Data, key); }
        public readonly bool TryGetValue(TKey key, out TValue item)
        {
            CheckRead();
            int slot = HashMapCore<TKey, TValue>.Find(m_Data, key);
            if (slot < 0) { item = default; return false; }
            item = ((TValue*)m_Data->Values)[slot];
            return true;
        }
        public readonly bool ContainsKey(TKey key) { CheckRead(); return HashMapCore<TKey, TValue>.Find(m_Data, key) >= 0; }
        /// <summary>읽기는 없으면 ArgumentException, 쓰기는 넣거나 바꾼다</summary>
        public TValue this[TKey key]
        {
            readonly get
            {
                if (TryGetValue(key, out TValue v)) return v;
                throw new ArgumentException("Key: " + key + " is not present in the NativeHashMap.");
            }
            set
            {
                CheckWrite();
                int slot = HashMapCore<TKey, TValue>.Add(m_Data, key, m_AllocatorLabel, out _);
                ((TValue*)m_Data->Values)[slot] = value;
            }
        }
        public void Clear() { CheckWrite(); HashMapCore<TKey, TValue>.Clear(m_Data); }
        public void TrimExcess() { CheckWrite(); HashMapCore<TKey, TValue>.Resize(m_Data, m_Data->Count, m_AllocatorLabel); }

        public readonly NativeArray<TKey> GetKeyArray(Allocator allocator)
        {
            CheckRead();
            var a = new NativeArray<TKey>(m_Data->Count, allocator, NativeArrayOptions.UninitializedMemory);
            var it = new HashMapCore<TKey, TValue>.Iterator(m_Data);
            TKey* dst = (TKey*)a.m_Buffer;
            for (int n = 0; it.MoveNext(); n++) dst[n] = ((TKey*)m_Data->Keys)[it.Slot];
            return a;
        }
        public readonly NativeArray<TValue> GetValueArray(Allocator allocator)
        {
            CheckRead();
            var a = new NativeArray<TValue>(m_Data->Count, allocator, NativeArrayOptions.UninitializedMemory);
            var it = new HashMapCore<TKey, TValue>.Iterator(m_Data);
            TValue* dst = (TValue*)a.m_Buffer;
            for (int n = 0; it.MoveNext(); n++) dst[n] = ((TValue*)m_Data->Values)[it.Slot];
            return a;
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
            HashMapCore<TKey, TValue>.Dispose(m_Data, m_AllocatorLabel);
            UnsafeUtility.Free(m_Data, m_AllocatorLabel);
            m_Data = null;
        }

        /// <summary>의존성 (앞 잡) 이 끝난 뒤 작업 스레드에서 푼다</summary>
        public JobHandle Dispose(JobHandle inputDeps)
        {
            if (CollectionChecks.Enabled && !AtomicSafetyHandle.IsDefaultValue(m_Safety)) AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            if (m_Data == null) return inputDeps;
            JobHandle h = new DisposeJob<NativeHashMap<TKey, TValue>> { Container = this }.Schedule(inputDeps);
            m_Data = null;
            return h;
        }

        /// <summary>여러 잡이 함께 넣기 (잠금 — 넣기만 한다)</summary>
        public readonly ParallelWriter AsParallelWriter() { CheckWrite(); return new ParallelWriter { m_Data = m_Data, m_AllocatorLabel = m_AllocatorLabel, m_Safety = m_Safety }; }

        [StructLayout(LayoutKind.Sequential)]
        [NativeContainer, NativeContainerIsAtomicWriteOnly]
        public unsafe struct ParallelWriter
        {
            internal HashMapData* m_Data;
            internal Allocator m_AllocatorLabel;
            internal AtomicSafetyHandle m_Safety;
            public bool TryAdd(TKey key, TValue item)
            {
                if (CollectionChecks.Enabled) AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
                SpinLockPtr.Enter(&m_Data->Lock);
                try
                {
                    int slot = HashMapCore<TKey, TValue>.Add(m_Data, key, m_AllocatorLabel, out bool added);
                    if (added) ((TValue*)m_Data->Values)[slot] = item;
                    return added;
                }
                finally { SpinLockPtr.Exit(&m_Data->Lock); }
            }
        }

        public readonly Enumerator GetEnumerator() { CheckRead(); return new Enumerator(this); }
        readonly IEnumerator<KVPair<TKey, TValue>> IEnumerable<KVPair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
        readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<KVPair<TKey, TValue>>
        {
            NativeHashMap<TKey, TValue> m_Map;
            HashMapCore<TKey, TValue>.Iterator m_It;
            public Enumerator(NativeHashMap<TKey, TValue> map) { m_Map = map; m_It = new HashMapCore<TKey, TValue>.Iterator(map.m_Data); }
            public bool MoveNext() { m_Map.CheckRead(); return m_It.MoveNext(); }
            public void Reset() => m_It = new HashMapCore<TKey, TValue>.Iterator(m_Map.m_Data);
            public KVPair<TKey, TValue> Current => new KVPair<TKey, TValue>(((TKey*)m_Map.m_Data->Keys)[m_It.Slot], ((TValue*)m_Map.m_Data->Values)[m_It.Slot]);
            object IEnumerator.Current => Current;
            public void Dispose() { }
        }

        public override readonly string ToString() => "NativeHashMap<" + typeof(TKey).Name + "," + typeof(TValue).Name + ">(" + (m_Data == null ? "not created" : m_Data->Count.ToString()) + ")";
    }

    /// <summary>비관리 집합 (Unity.Collections.NativeHashSet)</summary>
    [StructLayout(LayoutKind.Sequential)]
    [NativeContainer]
    public unsafe struct NativeHashSet<T> : IDisposable, IEnumerable<T> where T : unmanaged, IEquatable<T>
    {
        internal HashMapData* m_Data;
        internal Allocator m_AllocatorLabel;
        internal AtomicSafetyHandle m_Safety;

        public NativeHashSet(int initialCapacity, Allocator allocator)
        {
            CollectionHelper.CheckAllocator(allocator);
            if (initialCapacity < 0) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
            m_Data = (HashMapData*)UnsafeUtility.Malloc(sizeof(HashMapData), 16, allocator);
            m_Data->Lock = 0;
            m_AllocatorLabel = allocator;
            HashMapCore<T, byte>.Allocate(m_Data, initialCapacity, allocator);
            m_Safety = CollectionHelper.CreateSafety(m_Data, allocator, "NativeHashSet<" + typeof(T).Name + ">");
        }

        public readonly bool IsCreated => m_Data != null;
        public readonly bool IsEmpty { get { CheckRead(); return m_Data->Count == 0; } }
        public readonly int Count { get { CheckRead(); return m_Data->Count; } }
        public int Capacity
        {
            readonly get { CheckRead(); return m_Data->Capacity; }
            set { CheckWrite(); if (value != m_Data->Capacity) HashMapCore<T, byte>.Resize(m_Data, value, m_AllocatorLabel); }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void CheckRead()
        {
            if (!CollectionChecks.Enabled) return;
            if (m_Data == null) CollectionHelper.ThrowNotCreated();
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void CheckWrite()
        {
            if (!CollectionChecks.Enabled) return;
            if (m_Data == null) CollectionHelper.ThrowNotCreated();
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
        }

        /// <summary>새로 넣었으면 true</summary>
        public bool Add(T item) { CheckWrite(); HashMapCore<T, byte>.Add(m_Data, item, m_AllocatorLabel, out bool added); return added; }
        public bool Remove(T item) { CheckWrite(); return HashMapCore<T, byte>.Remove(m_Data, item); }
        public readonly bool Contains(T item) { CheckRead(); return HashMapCore<T, byte>.Find(m_Data, item) >= 0; }
        public void Clear() { CheckWrite(); HashMapCore<T, byte>.Clear(m_Data); }
        public readonly NativeArray<T> ToNativeArray(Allocator allocator)
        {
            CheckRead();
            var a = new NativeArray<T>(m_Data->Count, allocator, NativeArrayOptions.UninitializedMemory);
            var it = new HashMapCore<T, byte>.Iterator(m_Data);
            T* dst = (T*)a.m_Buffer;
            for (int n = 0; it.MoveNext(); n++) dst[n] = ((T*)m_Data->Keys)[it.Slot];
            return a;
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
            HashMapCore<T, byte>.Dispose(m_Data, m_AllocatorLabel);
            UnsafeUtility.Free(m_Data, m_AllocatorLabel);
            m_Data = null;
        }

        /// <summary>의존성 (앞 잡) 이 끝난 뒤 작업 스레드에서 푼다</summary>
        public JobHandle Dispose(JobHandle inputDeps)
        {
            if (CollectionChecks.Enabled && !AtomicSafetyHandle.IsDefaultValue(m_Safety)) AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            if (m_Data == null) return inputDeps;
            JobHandle h = new DisposeJob<NativeHashSet<T>> { Container = this }.Schedule(inputDeps);
            m_Data = null;
            return h;
        }

        public readonly ParallelWriter AsParallelWriter() { CheckWrite(); return new ParallelWriter { m_Data = m_Data, m_AllocatorLabel = m_AllocatorLabel, m_Safety = m_Safety }; }

        [StructLayout(LayoutKind.Sequential)]
        [NativeContainer, NativeContainerIsAtomicWriteOnly]
        public unsafe struct ParallelWriter
        {
            internal HashMapData* m_Data;
            internal Allocator m_AllocatorLabel;
            internal AtomicSafetyHandle m_Safety;
            public bool Add(T item)
            {
                if (CollectionChecks.Enabled) AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
                SpinLockPtr.Enter(&m_Data->Lock);
                try { HashMapCore<T, byte>.Add(m_Data, item, m_AllocatorLabel, out bool added); return added; }
                finally { SpinLockPtr.Exit(&m_Data->Lock); }
            }
        }

        public readonly Enumerator GetEnumerator() { CheckRead(); return new Enumerator(this); }
        readonly IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<T>
        {
            NativeHashSet<T> m_Set;
            HashMapCore<T, byte>.Iterator m_It;
            public Enumerator(NativeHashSet<T> set) { m_Set = set; m_It = new HashMapCore<T, byte>.Iterator(set.m_Data); }
            public bool MoveNext() { m_Set.CheckRead(); return m_It.MoveNext(); }
            public void Reset() => m_It = new HashMapCore<T, byte>.Iterator(m_Set.m_Data);
            public T Current => ((T*)m_Set.m_Data->Keys)[m_It.Slot];
            object IEnumerator.Current => Current;
            public void Dispose() { }
        }
    }

    // 링 버퍼 머리
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct QueueData
    {
        public void* Ptr;
        public int Head;
        public int Count;
        public int Capacity;
        public int Lock;   // ParallelWriter
    }

    /// <summary>비관리 선입선출 큐 (Unity.Collections.NativeQueue)</summary>
    [StructLayout(LayoutKind.Sequential)]
    [NativeContainer]
    public unsafe struct NativeQueue<T> : IDisposable where T : unmanaged
    {
        internal QueueData* m_Data;
        internal Allocator m_AllocatorLabel;
        internal AtomicSafetyHandle m_Safety;

        public NativeQueue(Allocator allocator)
        {
            CollectionHelper.CheckAllocator(allocator);
            m_Data = (QueueData*)UnsafeUtility.Malloc(sizeof(QueueData), 16, allocator);
            m_Data->Ptr = null; m_Data->Head = 0; m_Data->Count = 0; m_Data->Capacity = 0; m_Data->Lock = 0;
            m_AllocatorLabel = allocator;
            m_Safety = CollectionHelper.CreateSafety(m_Data, allocator, "NativeQueue<" + typeof(T).Name + ">");
        }

        public readonly bool IsCreated => m_Data != null;
        public readonly int Count { get { CheckRead(); return m_Data->Count; } }
        public readonly bool IsEmpty() { CheckRead(); return m_Data->Count == 0; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly void CheckRead()
        {
            if (!CollectionChecks.Enabled) return;
            if (m_Data == null) CollectionHelper.ThrowNotCreated();
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly void CheckWrite()
        {
            if (!CollectionChecks.Enabled) return;
            if (m_Data == null) CollectionHelper.ThrowNotCreated();
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
        }

        public void Enqueue(T value)
        {
            CheckWrite();
            EnqueueCore(m_Data, value, m_AllocatorLabel);
        }
        internal static void EnqueueCore(QueueData* d, T value, Allocator allocator)
        {
            if (d->Count == d->Capacity)
            {
                int cap = Math.Max(d->Capacity * 2, Math.Max(64 / sizeof(T), 4));
                T* p = (T*)UnsafeUtility.Malloc((long)cap * sizeof(T), UnsafeUtility.AlignOf<T>(), allocator);
                for (int i = 0; i < d->Count; i++) p[i] = ((T*)d->Ptr)[(d->Head + i) % d->Capacity];
                if (d->Ptr != null) UnsafeUtility.Free(d->Ptr, allocator);
                d->Ptr = p; d->Head = 0; d->Capacity = cap;
            }
            ((T*)d->Ptr)[(d->Head + d->Count) % d->Capacity] = value;
            d->Count++;
        }
        public bool TryDequeue(out T item)
        {
            CheckWrite();
            QueueData* d = m_Data;
            if (d->Count == 0) { item = default; return false; }
            item = ((T*)d->Ptr)[d->Head];
            d->Head = (d->Head + 1) % d->Capacity;
            d->Count--;
            return true;
        }
        public T Dequeue()
        {
            if (!TryDequeue(out T item)) throw new InvalidOperationException("Trying to dequeue from an empty queue");
            return item;
        }
        public readonly T Peek()
        {
            CheckRead();
            if (m_Data->Count == 0) throw new InvalidOperationException("Trying to peek into an empty queue");
            return ((T*)m_Data->Ptr)[m_Data->Head];
        }
        public void Clear() { CheckWrite(); m_Data->Head = 0; m_Data->Count = 0; }
        public readonly NativeArray<T> ToArray(Allocator allocator)
        {
            CheckRead();
            QueueData* d = m_Data;
            var a = new NativeArray<T>(d->Count, allocator, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < d->Count; i++) ((T*)a.m_Buffer)[i] = ((T*)d->Ptr)[(d->Head + i) % d->Capacity];
            return a;
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
            if (m_Data->Ptr != null) UnsafeUtility.Free(m_Data->Ptr, m_AllocatorLabel);
            UnsafeUtility.Free(m_Data, m_AllocatorLabel);
            m_Data = null;
        }

        /// <summary>의존성 (앞 잡) 이 끝난 뒤 작업 스레드에서 푼다</summary>
        public JobHandle Dispose(JobHandle inputDeps)
        {
            if (CollectionChecks.Enabled && !AtomicSafetyHandle.IsDefaultValue(m_Safety)) AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            if (m_Data == null) return inputDeps;
            JobHandle h = new DisposeJob<NativeQueue<T>> { Container = this }.Schedule(inputDeps);
            m_Data = null;
            return h;
        }

        /// <summary>여러 잡이 함께 넣기 (잠금)</summary>
        public readonly ParallelWriter AsParallelWriter() { CheckWrite(); return new ParallelWriter { m_Data = m_Data, m_AllocatorLabel = m_AllocatorLabel, m_Safety = m_Safety }; }

        [StructLayout(LayoutKind.Sequential)]
        [NativeContainer, NativeContainerIsAtomicWriteOnly]
        public unsafe struct ParallelWriter
        {
            internal QueueData* m_Data;
            internal Allocator m_AllocatorLabel;
            internal AtomicSafetyHandle m_Safety;
            public void Enqueue(T value)
            {
                if (CollectionChecks.Enabled) AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
                SpinLockPtr.Enter(&m_Data->Lock);
                try { EnqueueCore(m_Data, value, m_AllocatorLabel); }
                finally { SpinLockPtr.Exit(&m_Data->Lock); }
            }
        }
    }

    /// <summary>NativeArray · NativeList 정렬 · 찾기 (Unity.Collections.NativeSortExtension · NativeArrayExtensions)</summary>
    public static unsafe class NativeCollectionExtensions
    {
        public static void Sort<T>(this NativeArray<T> array) where T : unmanaged, IComparable<T> => array.AsSpan().Sort();
        public static void Sort<T, U>(this NativeArray<T> array, U comp) where T : unmanaged where U : IComparer<T> => array.AsSpan().Sort(comp);
        public static void Sort<T>(this NativeList<T> list) where T : unmanaged, IComparable<T> => list.AsSpan().Sort();
        public static void Sort<T, U>(this NativeList<T> list, U comp) where T : unmanaged where U : IComparer<T> => list.AsSpan().Sort(comp);
        /// <summary>정렬된 배열에서 찾기 — 없으면 들어갈 자리의 보수 (~index)</summary>
        public static int BinarySearch<T>(this NativeArray<T> array, T value) where T : unmanaged, IComparable<T> => array.AsReadOnlySpan().BinarySearch(value);
        public static int BinarySearch<T>(this NativeList<T> list, T value) where T : unmanaged, IComparable<T> => list.AsReadOnlySpan().BinarySearch(value);
        public static bool Contains<T, U>(this NativeArray<T> array, U value) where T : unmanaged, IEquatable<U> => IndexOf(array, value) >= 0;
        public static int IndexOf<T, U>(this NativeArray<T> array, U value) where T : unmanaged, IEquatable<U>
        {
            array.CheckRead();
            T* p = (T*)array.m_Buffer;
            for (int i = 0; i < array.m_Length; i++) if (p[i].Equals(value)) return i;
            return -1;
        }
        public static bool Contains<T, U>(this NativeList<T> list, U value) where T : unmanaged, IEquatable<U> => IndexOf(list.AsArray(), value) >= 0;
        public static int IndexOf<T, U>(this NativeList<T> list, U value) where T : unmanaged, IEquatable<U> => IndexOf(list.AsArray(), value);
        /// <summary>정렬된 상태에서 같은 값이 이어지면 하나만 남긴다 — 남은 개수</summary>
        public static int Unique<T>(this NativeArray<T> array) where T : unmanaged, IEquatable<T>
        {
            array.CheckWrite();
            T* p = (T*)array.m_Buffer;
            if (array.m_Length == 0) return 0;
            int n = 1;
            for (int i = 1; i < array.m_Length; i++) if (!p[i].Equals(p[n - 1])) p[n++] = p[i];
            return n;
        }
    }
}
