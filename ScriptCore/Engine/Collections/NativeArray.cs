using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NovaEngine.Collections.LowLevel.Unsafe;

namespace NovaEngine.Collections
{
    /// <summary>
    /// 비관리 메모리의 고정 길이 배열 (Unity.Collections.NativeArray). GC 가 건드리지 않고 잡 (Job) 에 넘길 수 있다.
    /// 값 형식이라 복사해도 같은 메모리를 가리킨다 — 다 쓰면 Dispose (Temp 는 다음 프레임에 저절로).
    /// 에디터에서는 범위 밖 색인 · Dispose 뒤 사용을 예외로 잡는다 (플레이어 빌드는 검사 없이 포인터 그대로).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct NativeArray<T> : IDisposable, IEnumerable<T>, IEquatable<NativeArray<T>> where T : unmanaged
    {
        internal void* m_Buffer;
        internal int m_Length;
        internal Allocator m_AllocatorLabel;
        internal int m_MinIndex, m_MaxIndex;   // IJobParallelFor 가 한 묶음 동안 좁힌다 (안전 검사)
        internal AtomicSafetyHandle m_Safety;

        public NativeArray(int length, Allocator allocator, NativeArrayOptions options = NativeArrayOptions.ClearMemory)
        {
            Allocate(length, allocator, out this);
            if (options == NativeArrayOptions.ClearMemory) UnsafeUtility.MemClear(m_Buffer, (long)length * sizeof(T));
        }
        public NativeArray(T[] array, Allocator allocator)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            Allocate(array.Length, allocator, out this);
            fixed (T* src = array) UnsafeUtility.MemCpy(m_Buffer, src, (long)array.Length * sizeof(T));
        }
        public NativeArray(NativeArray<T> array, Allocator allocator)
        {
            array.CheckRead();
            Allocate(array.m_Length, allocator, out this);
            UnsafeUtility.MemCpy(m_Buffer, array.m_Buffer, (long)array.m_Length * sizeof(T));
        }
        /// <summary>(NOVA) Span · 관리 배열 조각에서</summary>
        public NativeArray(ReadOnlySpan<T> span, Allocator allocator)
        {
            Allocate(span.Length, allocator, out this);
            fixed (T* src = span) UnsafeUtility.MemCpy(m_Buffer, src, (long)span.Length * sizeof(T));
        }

        static void Allocate(int length, Allocator allocator, out NativeArray<T> array)
        {
            CollectionHelper.CheckAllocator(allocator);
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "Length must be >= 0");
            array = default;
            array.m_Buffer = UnsafeUtility.Malloc((long)length * sizeof(T), UnsafeUtility.AlignOf<T>(), allocator);
            array.m_Length = length;
            array.m_AllocatorLabel = allocator;
            array.m_MinIndex = 0;
            array.m_MaxIndex = length - 1;
            array.m_Safety = CollectionHelper.CreateSafety(array.m_Buffer, allocator, "NativeArray<" + typeof(T).Name + ">");
        }

        public readonly int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] get => m_Length;
        }
        public readonly bool IsCreated
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] get => m_Buffer != null;
        }

        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            readonly get
            {
                if (CollectionChecks.Enabled) CheckReadIndex(index);
                return ((T*)m_Buffer)[index];
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                if (CollectionChecks.Enabled) CheckWriteIndex(index);
                ((T*)m_Buffer)[index] = value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly void CheckReadIndex(int index)
        {
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
            if (index < m_MinIndex || index > m_MaxIndex) FailOutOfRange(index);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly void CheckWriteIndex(int index)
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            if (index < m_MinIndex || index > m_MaxIndex) FailOutOfRange(index);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        readonly void FailOutOfRange(int index)
        {
            if (m_Buffer == null) CollectionHelper.ThrowNotCreated();
            if (index >= 0 && index < m_Length) CollectionHelper.ThrowRestrictedIndex(index, m_MinIndex, m_MaxIndex);
            CollectionHelper.ThrowIndex(index, m_Length);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void CheckRead()
        {
            if (!CollectionChecks.Enabled) return;
            if (m_Buffer == null) CollectionHelper.ThrowNotCreated();
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void CheckWrite()
        {
            if (!CollectionChecks.Enabled) return;
            if (m_Buffer == null) CollectionHelper.ThrowNotCreated();
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
        }

        public void Dispose()
        {
            if (CollectionChecks.Enabled && !AtomicSafetyHandle.IsDefaultValue(m_Safety)) AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            if (m_Buffer == null) return;
            if (m_AllocatorLabel <= Allocator.None)
                throw new InvalidOperationException("The NativeArray can not be Disposed because it was not allocated with a valid allocator (a sub array, Reinterpret or AsArray view does not own its memory).");
            if (CollectionChecks.Enabled)
            {
                AtomicSafetyHandle.CheckDeallocateAndThrow(m_Safety);
                AtomicSafetyHandle.Release(m_Safety);
            }
            UnsafeUtility.Free(m_Buffer, m_AllocatorLabel);
            m_Buffer = null;
            m_Length = 0;
        }

        public void CopyFrom(T[] array) { CheckWrite(); Copy(array, this); }
        public void CopyFrom(NativeArray<T> array) { CheckWrite(); Copy(array, this); }
        public readonly void CopyTo(T[] array) { CheckRead(); Copy(this, array); }
        public readonly void CopyTo(NativeArray<T> array) { CheckRead(); Copy(this, array); }
        public readonly T[] ToArray()
        {
            CheckRead();
            var a = new T[m_Length];
            fixed (T* dst = a) UnsafeUtility.MemCpy(dst, m_Buffer, (long)m_Length * sizeof(T));
            return a;
        }

        /// <summary>(NOVA) .NET Span 으로 — 검사 없이 가장 빠른 접근 (Span 자체는 범위를 검사한다)</summary>
        public readonly Span<T> AsSpan() { CheckWrite(); return new Span<T>(m_Buffer, m_Length); }
        public readonly ReadOnlySpan<T> AsReadOnlySpan() { CheckRead(); return new ReadOnlySpan<T>(m_Buffer, m_Length); }

        /// <summary>같은 메모리의 일부 (Dispose 할 수 없다)</summary>
        public readonly NativeArray<T> GetSubArray(int start, int length)
        {
            CheckRead();
            if (start < 0 || length < 0 || (long)start + length > m_Length)
                throw new ArgumentOutOfRangeException(nameof(start), "Sub array range " + start + " + " + length + " is outside of the NativeArray (Length " + m_Length + ")");
            var a = this;
            a.m_Buffer = (T*)m_Buffer + start;
            a.m_Length = length;
            a.m_MinIndex = 0;
            a.m_MaxIndex = length - 1;
            a.m_AllocatorLabel = Allocator.None;
            return a;
        }

        /// <summary>같은 메모리를 다른 형식으로 (크기가 같아야 한다 — Vector3 ↔ float3 처럼)</summary>
        public readonly NativeArray<U> Reinterpret<U>() where U : unmanaged
        {
            if (sizeof(T) != sizeof(U)) throw new InvalidOperationException("Types " + typeof(T).Name + " (" + sizeof(T) + " bytes) and " + typeof(U).Name + " (" + sizeof(U) + " bytes) must be the same size; use Reinterpret<U>(expectedTypeSize)");
            return ReinterpretCore<U>(m_Length);
        }
        /// <summary>같은 메모리를 다른 크기의 형식으로 (전체 바이트가 나누어떨어져야 한다)</summary>
        public readonly NativeArray<U> Reinterpret<U>(int expectedTypeSize) where U : unmanaged
        {
            if (expectedTypeSize != sizeof(T)) throw new InvalidOperationException("expectedTypeSize " + expectedTypeSize + " does not match " + typeof(T).Name + " (" + sizeof(T) + " bytes)");
            long bytes = (long)m_Length * sizeof(T);
            if (bytes % sizeof(U) != 0) throw new InvalidOperationException("NativeArray of " + bytes + " bytes can not be divided into " + typeof(U).Name + " (" + sizeof(U) + " bytes)");
            return ReinterpretCore<U>((int)(bytes / sizeof(U)));
        }
        readonly NativeArray<U> ReinterpretCore<U>(int length) where U : unmanaged
        {
            CheckRead();
            var a = new NativeArray<U>();
            a.m_Buffer = m_Buffer;
            a.m_Length = length;
            a.m_MinIndex = 0;
            a.m_MaxIndex = length - 1;
            a.m_AllocatorLabel = Allocator.None;
            a.m_Safety = m_Safety;
            return a;
        }

        public readonly ReadOnly AsReadOnly() => new ReadOnly(m_Buffer, m_Length, AtomicSafetyHandle.AsReadOnly(m_Safety));

        // 복사
        public static void Copy(NativeArray<T> src, NativeArray<T> dst)
        {
            if (src.m_Length != dst.m_Length) throw new ArgumentException("source and destination length must be the same");
            Copy(src, 0, dst, 0, src.m_Length);
        }
        public static void Copy(T[] src, NativeArray<T> dst)
        {
            if (src.Length != dst.m_Length) throw new ArgumentException("source and destination length must be the same");
            Copy(src, 0, dst, 0, src.Length);
        }
        public static void Copy(NativeArray<T> src, T[] dst)
        {
            if (src.m_Length != dst.Length) throw new ArgumentException("source and destination length must be the same");
            Copy(src, 0, dst, 0, src.m_Length);
        }
        public static void Copy(NativeArray<T> src, NativeArray<T> dst, int length) => Copy(src, 0, dst, 0, length);
        public static void Copy(NativeArray<T> src, int srcIndex, NativeArray<T> dst, int dstIndex, int length)
        {
            src.CheckRead(); dst.CheckWrite();
            CheckCopyRange(src.m_Length, srcIndex, dst.m_Length, dstIndex, length);
            UnsafeUtility.MemMove((T*)dst.m_Buffer + dstIndex, (T*)src.m_Buffer + srcIndex, (long)length * sizeof(T));
        }
        public static void Copy(T[] src, int srcIndex, NativeArray<T> dst, int dstIndex, int length)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            dst.CheckWrite();
            CheckCopyRange(src.Length, srcIndex, dst.m_Length, dstIndex, length);
            fixed (T* s = src) UnsafeUtility.MemCpy((T*)dst.m_Buffer + dstIndex, s + srcIndex, (long)length * sizeof(T));
        }
        public static void Copy(NativeArray<T> src, int srcIndex, T[] dst, int dstIndex, int length)
        {
            if (dst == null) throw new ArgumentNullException(nameof(dst));
            src.CheckRead();
            CheckCopyRange(src.m_Length, srcIndex, dst.Length, dstIndex, length);
            fixed (T* d = dst) UnsafeUtility.MemCpy(d + dstIndex, (T*)src.m_Buffer + srcIndex, (long)length * sizeof(T));
        }
        static void CheckCopyRange(int srcLength, int srcIndex, int dstLength, int dstIndex, int length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            if (srcIndex < 0 || (long)srcIndex + length > srcLength) throw new ArgumentOutOfRangeException(nameof(srcIndex), "source range " + srcIndex + " + " + length + " is outside of Length " + srcLength);
            if (dstIndex < 0 || (long)dstIndex + length > dstLength) throw new ArgumentOutOfRangeException(nameof(dstIndex), "destination range " + dstIndex + " + " + length + " is outside of Length " + dstLength);
        }

        public readonly Enumerator GetEnumerator() { CheckRead(); return new Enumerator(this); }
        readonly IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<T>
        {
            NativeArray<T> m_Array;
            int m_Index;
            public Enumerator(NativeArray<T> array) { m_Array = array; m_Index = -1; }
            public bool MoveNext() => ++m_Index < m_Array.m_Length;
            public void Reset() => m_Index = -1;
            public T Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { if (CollectionChecks.Enabled) AtomicSafetyHandle.CheckReadAndThrow(m_Array.m_Safety); return ((T*)m_Array.m_Buffer)[m_Index]; }
            }
            object IEnumerator.Current => Current;
            public void Dispose() { }
        }

        public readonly bool Equals(NativeArray<T> other) => m_Buffer == other.m_Buffer && m_Length == other.m_Length;
        public override readonly bool Equals(object obj) => obj is NativeArray<T> a && Equals(a);
        public override readonly int GetHashCode() => HashCode.Combine((IntPtr)m_Buffer, m_Length);
        public static bool operator ==(NativeArray<T> left, NativeArray<T> right) => left.Equals(right);
        public static bool operator !=(NativeArray<T> left, NativeArray<T> right) => !left.Equals(right);
        public override readonly string ToString() => "NativeArray<" + typeof(T).Name + ">(" + (m_Buffer == null ? "not created" : m_Length.ToString()) + ")";

        /// <summary>읽기만 되는 NativeArray (Unity.Collections.NativeArray&lt;T&gt;.ReadOnly)</summary>
        [StructLayout(LayoutKind.Sequential)]
        public readonly struct ReadOnly : IEnumerable<T>
        {
            internal readonly void* m_Buffer;
            internal readonly int m_Length;
            internal readonly AtomicSafetyHandle m_Safety;
            internal ReadOnly(void* buffer, int length, AtomicSafetyHandle safety) { m_Buffer = buffer; m_Length = length; m_Safety = safety; }
            public int Length => m_Length;
            public bool IsCreated => m_Buffer != null;
            public T this[int index]
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    if (CollectionChecks.Enabled)
                    {
                        AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                        if ((uint)index >= (uint)m_Length) CollectionHelper.ThrowIndex(index, m_Length);
                    }
                    return ((T*)m_Buffer)[index];
                }
            }
            public ReadOnlySpan<T> AsReadOnlySpan() { if (CollectionChecks.Enabled) AtomicSafetyHandle.CheckReadAndThrow(m_Safety); return new ReadOnlySpan<T>(m_Buffer, m_Length); }
            public T[] ToArray() => AsReadOnlySpan().ToArray();
            public Enumerator GetEnumerator() => new Enumerator(this);
            IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public struct Enumerator : IEnumerator<T>
            {
                ReadOnly m_Array;
                int m_Index;
                public Enumerator(ReadOnly array) { m_Array = array; m_Index = -1; }
                public bool MoveNext() => ++m_Index < m_Array.m_Length;
                public void Reset() => m_Index = -1;
                public T Current => m_Array[m_Index];
                object IEnumerator.Current => Current;
                public void Dispose() { }
            }
        }
    }
}

namespace NovaEngine.Collections.LowLevel.Unsafe
{
    /// <summary>NativeArray 의 포인터 (Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility)</summary>
    public static unsafe class NativeArrayUnsafeUtility
    {
        public static void* GetUnsafePtr<T>(this NativeArray<T> array) where T : unmanaged { array.CheckWrite(); return array.m_Buffer; }
        public static void* GetUnsafeReadOnlyPtr<T>(this NativeArray<T> array) where T : unmanaged { array.CheckRead(); return array.m_Buffer; }
        public static void* GetUnsafeBufferPointerWithoutChecks<T>(NativeArray<T> array) where T : unmanaged => array.m_Buffer;
        public static void* GetUnsafeReadOnlyPtr<T>(this NativeArray<T>.ReadOnly array) where T : unmanaged => array.m_Buffer;
        /// <summary>남의 메모리를 NativeArray 로 감싼다 (Dispose 하지 않는다 — Allocator.None)</summary>
        public static NativeArray<T> ConvertExistingDataToNativeArray<T>(void* dataPointer, int length, Allocator allocator) where T : unmanaged
        {
            var a = new NativeArray<T>();
            a.m_Buffer = dataPointer;
            a.m_Length = length;
            a.m_MinIndex = 0;
            a.m_MaxIndex = length - 1;
            a.m_AllocatorLabel = allocator;
            return a;
        }
        public static AtomicSafetyHandle GetAtomicSafetyHandle<T>(NativeArray<T> array) where T : unmanaged => array.m_Safety;
        public static void SetAtomicSafetyHandle<T>(ref NativeArray<T> array, AtomicSafetyHandle safety) where T : unmanaged => array.m_Safety = safety;
    }
}
