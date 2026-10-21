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
    // 목록 머리 (비관리 메모리) — NativeList 의 복사본이 모두 같은 길이 · 버퍼를 본다
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ListData
    {
        public void* Ptr;
        public int Length;
        public int Capacity;
    }

    /// <summary>
    /// 길이가 바뀌는 비관리 목록 (Unity.Collections.NativeList). 다 쓰면 Dispose.
    /// AsArray() 로 얻은 NativeArray 는 목록이 커지면 (Add · Capacity) 버퍼가 옮겨져 쓸 수 없다.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    [NativeContainer]
    public unsafe struct NativeList<T> : IDisposable, IEnumerable<T> where T : unmanaged
    {
        internal ListData* m_Data;
        internal Allocator m_AllocatorLabel;
        internal AtomicSafetyHandle m_Safety;

        public NativeList(Allocator allocator) : this(1, allocator) { }
        public NativeList(int initialCapacity, Allocator allocator)
        {
            CollectionHelper.CheckAllocator(allocator);
            if (initialCapacity < 0) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
            m_Data = (ListData*)UnsafeUtility.Malloc(sizeof(ListData), 16, allocator);
            m_Data->Ptr = null;
            m_Data->Length = 0;
            m_Data->Capacity = 0;
            m_AllocatorLabel = allocator;
            m_Safety = CollectionHelper.CreateSafety(m_Data, allocator, "NativeList<" + typeof(T).Name + ">");
            if (initialCapacity > 0) Realloc(initialCapacity);
        }

        public readonly bool IsCreated => m_Data != null;
        public readonly bool IsEmpty { get { CheckRead(); return m_Data->Length == 0; } }

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            readonly get { CheckRead(); return m_Data->Length; }
            set { Resize(value, NativeArrayOptions.ClearMemory); }
        }
        public int Capacity
        {
            readonly get { CheckRead(); return m_Data->Capacity; }
            set
            {
                CheckWrite();
                if (value < m_Data->Length) throw new ArgumentOutOfRangeException(nameof(value), "Capacity " + value + " is less than Length " + m_Data->Length);
                Realloc(value);
            }
        }

        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            readonly get
            {
                if (CollectionChecks.Enabled) CheckIndex(index, false);
                return ((T*)m_Data->Ptr)[index];
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                if (CollectionChecks.Enabled) CheckIndex(index, true);
                ((T*)m_Data->Ptr)[index] = value;
            }
        }
        /// <summary>원소의 참조 (복사 없이 고치기)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ref T ElementAt(int index)
        {
            if (CollectionChecks.Enabled) CheckIndex(index, true);
            return ref ((T*)m_Data->Ptr)[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly void CheckIndex(int index, bool write)
        {
            if (m_Data == null) CollectionHelper.ThrowNotCreated();
            if (write) AtomicSafetyHandle.CheckWriteAndThrow(m_Safety); else AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
            if ((uint)index >= (uint)m_Data->Length) CollectionHelper.ThrowIndex(index, m_Data->Length);
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

        void Realloc(int capacity)
        {
            if (capacity == m_Data->Capacity) return;
            void* p = capacity > 0 ? UnsafeUtility.Malloc((long)capacity * sizeof(T), UnsafeUtility.AlignOf<T>(), m_AllocatorLabel) : null;
            if (m_Data->Ptr != null)
            {
                if (p != null) UnsafeUtility.MemCpy(p, m_Data->Ptr, (long)Math.Min(m_Data->Length, capacity) * sizeof(T));
                UnsafeUtility.Free(m_Data->Ptr, m_AllocatorLabel);
            }
            m_Data->Ptr = p;
            m_Data->Capacity = capacity;
        }
        void Grow(int needed)
        {
            int cap = Math.Max(m_Data->Capacity * 2, Math.Max(needed, Math.Max(64 / sizeof(T), 4)));
            Realloc(cap);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(in T value)
        {
            CheckWrite();
            int n = m_Data->Length;
            if (n == m_Data->Capacity) Grow(n + 1);
            ((T*)m_Data->Ptr)[n] = value;
            m_Data->Length = n + 1;
        }
        /// <summary>자리가 있을 때만 (늘리지 않는다)</summary>
        public void AddNoResize(T value)
        {
            CheckWrite();
            int n = m_Data->Length;
            if (n >= m_Data->Capacity) throw new InvalidOperationException("AddNoResize: Length " + n + " would exceed Capacity " + m_Data->Capacity);
            ((T*)m_Data->Ptr)[n] = value;
            m_Data->Length = n + 1;
        }
        public void AddRange(NativeArray<T> array)
        {
            array.CheckRead();
            AddRange(array.m_Buffer, array.m_Length);
        }
        public void AddRange(void* ptr, int count)
        {
            CheckWrite();
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            int n = m_Data->Length;
            if (n + count > m_Data->Capacity) Grow(n + count);
            UnsafeUtility.MemCpy((T*)m_Data->Ptr + n, ptr, (long)count * sizeof(T));
            m_Data->Length = n + count;
        }
        /// <summary>(NOVA) Span 에서</summary>
        public void AddRange(ReadOnlySpan<T> span) { fixed (T* p = span) AddRange(p, span.Length); }
        public void AddReplicate(in T value, int count)
        {
            CheckWrite();
            int n = m_Data->Length;
            if (n + count > m_Data->Capacity) Grow(n + count);
            T* p = (T*)m_Data->Ptr + n;
            for (int i = 0; i < count; i++) p[i] = value;
            m_Data->Length = n + count;
        }
        public void InsertRange(int index, int count)
        {
            CheckWrite();
            int n = m_Data->Length;
            if ((uint)index > (uint)n) CollectionHelper.ThrowIndex(index, n);
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (n + count > m_Data->Capacity) Grow(n + count);
            T* p = (T*)m_Data->Ptr;
            UnsafeUtility.MemMove(p + index + count, p + index, (long)(n - index) * sizeof(T));
            m_Data->Length = n + count;
        }
        public void InsertRangeWithBeginEnd(int begin, int end) => InsertRange(begin, end - begin);
        public void Insert(int index, in T value) { InsertRange(index, 1); ((T*)m_Data->Ptr)[index] = value; }
        /// <summary>뒤를 당겨 순서를 지킨다</summary>
        public void RemoveAt(int index) => RemoveRange(index, 1);
        public void RemoveRange(int index, int count)
        {
            CheckWrite();
            int n = m_Data->Length;
            if (index < 0 || count < 0 || index + count > n) throw new ArgumentOutOfRangeException(nameof(index), "Range " + index + " + " + count + " is outside of Length " + n);
            T* p = (T*)m_Data->Ptr;
            UnsafeUtility.MemMove(p + index, p + index + count, (long)(n - index - count) * sizeof(T));
            m_Data->Length = n - count;
        }
        /// <summary>마지막 원소로 메운다 (빠르지만 순서가 바뀐다)</summary>
        public void RemoveAtSwapBack(int index)
        {
            CheckWrite();
            int n = m_Data->Length;
            if ((uint)index >= (uint)n) CollectionHelper.ThrowIndex(index, n);
            T* p = (T*)m_Data->Ptr;
            p[index] = p[n - 1];
            m_Data->Length = n - 1;
        }
        public void RemoveRangeSwapBack(int index, int count)
        {
            CheckWrite();
            int n = m_Data->Length;
            if (index < 0 || count < 0 || index + count > n) throw new ArgumentOutOfRangeException(nameof(index));
            int copy = Math.Min(count, n - index - count);
            T* p = (T*)m_Data->Ptr;
            UnsafeUtility.MemCpy(p + index, p + n - copy, (long)copy * sizeof(T));
            m_Data->Length = n - count;
        }
        public void Clear() { CheckWrite(); m_Data->Length = 0; }

        public void Resize(int length, NativeArrayOptions options)
        {
            CheckWrite();
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            int n = m_Data->Length;
            if (length > m_Data->Capacity) Grow(length);
            if (options == NativeArrayOptions.ClearMemory && length > n)
                UnsafeUtility.MemClear((T*)m_Data->Ptr + n, (long)(length - n) * sizeof(T));
            m_Data->Length = length;
        }
        public void ResizeUninitialized(int length) => Resize(length, NativeArrayOptions.UninitializedMemory);
        public void SetCapacity(int capacity) => Capacity = capacity;
        public void TrimExcess() { CheckWrite(); Realloc(m_Data->Length); }

        /// <summary>같은 버퍼를 NativeArray 로 (복사 없음 · Dispose 하지 않는다 — 목록이 커지면 쓸 수 없다)</summary>
        public readonly NativeArray<T> AsArray()
        {
            CheckRead();
            var a = new NativeArray<T>();
            a.m_Buffer = m_Data->Ptr;
            a.m_Length = m_Data->Length;
            a.m_MinIndex = 0;
            a.m_MaxIndex = m_Data->Length - 1;
            a.m_AllocatorLabel = Allocator.None;
            a.m_Safety = m_Safety;
            return a;
        }
        public readonly NativeArray<T>.ReadOnly AsReadOnly() => AsArray().AsReadOnly();
        public readonly NativeArray<T> ToArray(Allocator allocator) => new NativeArray<T>(AsArray(), allocator);
        /// <summary>(NOVA) 관리 배열로 복사</summary>
        public readonly T[] ToArray() => AsArray().ToArray();
        public void CopyFrom(NativeArray<T> array)
        {
            array.CheckRead();
            Resize(array.m_Length, NativeArrayOptions.UninitializedMemory);
            UnsafeUtility.MemCpy(m_Data->Ptr, array.m_Buffer, (long)array.m_Length * sizeof(T));
        }
        public readonly Span<T> AsSpan() { CheckWrite(); return new Span<T>(m_Data->Ptr, m_Data->Length); }
        public readonly ReadOnlySpan<T> AsReadOnlySpan() { CheckRead(); return new ReadOnlySpan<T>(m_Data->Ptr, m_Data->Length); }

        public static implicit operator NativeArray<T>(NativeList<T> list) => list.AsArray();

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
            JobHandle h = new DisposeJob<NativeList<T>> { Container = this }.Schedule(inputDeps);
            m_Data = null;
            return h;
        }

        /// <summary>여러 잡이 함께 덧붙이기 (IJobParallelFor 안에서 — 미리 Capacity 를 넉넉히)</summary>
        public readonly ParallelWriter AsParallelWriter() { CheckWrite(); return new ParallelWriter { m_ListData = m_Data, m_Safety = m_Safety }; }

        /// <summary>NativeList 의 동시 쓰기 (Unity.Collections.NativeList&lt;T&gt;.ParallelWriter) — 늘리지 않는다</summary>
        [StructLayout(LayoutKind.Sequential)]
        [NativeContainer, NativeContainerIsAtomicWriteOnly]
        public unsafe struct ParallelWriter
        {
            internal ListData* m_ListData;
            internal AtomicSafetyHandle m_Safety;

            public readonly void* Ptr => m_ListData->Ptr;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void AddNoResize(T value)
            {
                if (CollectionChecks.Enabled) AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
                int index = Interlocked.Increment(ref m_ListData->Length) - 1;
                if (index >= m_ListData->Capacity)
                {
                    Interlocked.Decrement(ref m_ListData->Length);
                    throw new InvalidOperationException("ParallelWriter.AddNoResize: Length would exceed Capacity " + m_ListData->Capacity + " (set Capacity before scheduling)");
                }
                ((T*)m_ListData->Ptr)[index] = value;
            }
            public void AddRangeNoResize(void* ptr, int count)
            {
                if (CollectionChecks.Enabled) AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
                int index = Interlocked.Add(ref m_ListData->Length, count) - count;
                if (index + count > m_ListData->Capacity)
                {
                    Interlocked.Add(ref m_ListData->Length, -count);
                    throw new InvalidOperationException("ParallelWriter.AddRangeNoResize: Length would exceed Capacity " + m_ListData->Capacity);
                }
                UnsafeUtility.MemCpy((T*)m_ListData->Ptr + index, ptr, (long)count * sizeof(T));
            }
            public void AddRangeNoResize(NativeList<T> list) => AddRangeNoResize(list.m_Data->Ptr, list.m_Data->Length);
        }

        public readonly NativeArray<T>.Enumerator GetEnumerator() => AsArray().GetEnumerator();
        readonly IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public override readonly string ToString() => "NativeList<" + typeof(T).Name + ">(" + (m_Data == null ? "not created" : m_Data->Length.ToString()) + ")";
    }

    /// <summary>
    /// 정수 · 열거형 같은 비관리 값 하나 (Unity.Collections.NativeReference) — 잡의 결과를 돌려받을 때.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    [NativeContainer]
    public unsafe struct NativeReference<T> : IDisposable, IEquatable<NativeReference<T>> where T : unmanaged
    {
        internal void* m_Data;
        internal Allocator m_AllocatorLabel;
        internal AtomicSafetyHandle m_Safety;

        public NativeReference(Allocator allocator, NativeArrayOptions options = NativeArrayOptions.ClearMemory)
        {
            CollectionHelper.CheckAllocator(allocator);
            m_Data = UnsafeUtility.Malloc(sizeof(T), UnsafeUtility.AlignOf<T>(), allocator);
            m_AllocatorLabel = allocator;
            m_Safety = CollectionHelper.CreateSafety(m_Data, allocator, "NativeReference<" + typeof(T).Name + ">");
            if (options == NativeArrayOptions.ClearMemory) UnsafeUtility.MemClear(m_Data, sizeof(T));
        }
        public NativeReference(T value, Allocator allocator) : this(allocator, NativeArrayOptions.UninitializedMemory) { *(T*)m_Data = value; }

        public readonly bool IsCreated => m_Data != null;
        public T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            readonly get { if (CollectionChecks.Enabled) { if (m_Data == null) CollectionHelper.ThrowNotCreated(); AtomicSafetyHandle.CheckReadAndThrow(m_Safety); } return *(T*)m_Data; }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set { if (CollectionChecks.Enabled) { if (m_Data == null) CollectionHelper.ThrowNotCreated(); AtomicSafetyHandle.CheckWriteAndThrow(m_Safety); } *(T*)m_Data = value; }
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
            UnsafeUtility.Free(m_Data, m_AllocatorLabel);
            m_Data = null;
        }

        /// <summary>의존성 (앞 잡) 이 끝난 뒤 작업 스레드에서 푼다</summary>
        public JobHandle Dispose(JobHandle inputDeps)
        {
            if (CollectionChecks.Enabled && !AtomicSafetyHandle.IsDefaultValue(m_Safety)) AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            if (m_Data == null) return inputDeps;
            JobHandle h = new DisposeJob<NativeReference<T>> { Container = this }.Schedule(inputDeps);
            m_Data = null;
            return h;
        }
        public readonly bool Equals(NativeReference<T> other) => m_Data == other.m_Data;
        public override readonly bool Equals(object obj) => obj is NativeReference<T> r && Equals(r);
        public override readonly int GetHashCode() => ((IntPtr)m_Data).GetHashCode();
        public static bool operator ==(NativeReference<T> left, NativeReference<T> right) => left.Equals(right);
        public static bool operator !=(NativeReference<T> left, NativeReference<T> right) => !left.Equals(right);
    }
}
