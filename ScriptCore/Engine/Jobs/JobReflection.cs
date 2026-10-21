using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using NovaEngine.Collections;
using NovaEngine.Collections.LowLevel.Unsafe;
using NovaEngine.Profiling;
using SysUnsafe = System.Runtime.CompilerServices.Unsafe;

namespace NovaEngine.Jobs
{
    // 잡 struct 안의 컨테이너 필드 하나 (중첩 struct 안도)
    internal sealed class ContainerField
    {
        public string Name;            // "MoveJob.positions"
        public string JobName;
        public string ContainerType;   // "NativeArray<float3>"
        public int SafetyOffset;       // 잡 struct 안에서 m_Safety 의 바이트 위치
        public int ContainerOffset;    // 컨테이너 struct 의 시작
        public bool ReadOnly, WriteOnly, Atomic, MinMax, DisableParallelRestriction, DisableSafety, Deallocate;
        public JobReflectionUtil.RefAction Dispose;
    }

    internal static unsafe class JobReflectionUtil
    {
        internal delegate void RefAction(ref byte container);
        const BindingFlags kFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // NativeArray 의 m_MinIndex · m_MaxIndex 가 m_Safety 에서 얼마나 떨어져 있나 (원소 형식과 상관없는 배치)
        internal static readonly int kArrayMinFromSafety, kArrayMaxFromSafety;
        static JobReflectionUtil()
        {
            NativeArray<byte> a = default;
            ref byte safety = ref SysUnsafe.As<AtomicSafetyHandle, byte>(ref a.m_Safety);
            kArrayMinFromSafety = (int)SysUnsafe.ByteOffset(ref safety, ref SysUnsafe.As<int, byte>(ref a.m_MinIndex));
            kArrayMaxFromSafety = (int)SysUnsafe.ByteOffset(ref safety, ref SysUnsafe.As<int, byte>(ref a.m_MaxIndex));
        }

        static readonly Dictionary<Type, string> s_Keywords = new Dictionary<Type, string>
        {
            { typeof(float), "float" }, { typeof(double), "double" }, { typeof(int), "int" }, { typeof(uint), "uint" }, { typeof(long), "long" },
            { typeof(ulong), "ulong" }, { typeof(short), "short" }, { typeof(ushort), "ushort" }, { typeof(byte), "byte" }, { typeof(sbyte), "sbyte" }, { typeof(bool), "bool" },
        };

        // 형식 이름 (Unity 오류 문구처럼 — NativeArray<float3>, NativeList<int>.ParallelWriter, Outer.MoveJob)
        internal static string NiceName(Type t)
        {
            if (s_Keywords.TryGetValue(t, out string kw)) return kw;
            Type[] all = t.IsGenericType ? t.GetGenericArguments() : Type.EmptyTypes;
            if (t.IsNested && !t.IsGenericParameter)
            {
                Type outer = t.DeclaringType;
                int outerCount = outer.IsGenericTypeDefinition ? outer.GetGenericArguments().Length : 0;
                if (outerCount > 0 && all.Length >= outerCount) outer = outer.MakeGenericType(Sub(all, 0, outerCount));
                return NiceName(outer) + "." + Plain(t.Name, Sub(all, outerCount, all.Length - outerCount));
            }
            return Plain(t.Name, all);
        }
        static string Plain(string name, Type[] args)
        {
            int tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);
            if (args.Length == 0) return name;
            var parts = new string[args.Length];
            for (int i = 0; i < args.Length; i++) parts[i] = NiceName(args[i]);
            return name + "<" + string.Join(",", parts) + ">";
        }
        static Type[] Sub(Type[] a, int start, int count) { var r = new Type[Math.Max(count, 0)]; Array.Copy(a, start, r, 0, r.Length); return r; }

        // 컨테이너 필드 경로 모으기 (중첩 struct 안까지)
        internal static void Collect(Type t, List<FieldInfo> path, List<FieldInfo[]> found, int depth)
        {
            foreach (FieldInfo f in t.GetFields(kFields))
            {
                Type ft = f.FieldType;
                if (!ft.IsValueType || ft.IsPrimitive || ft.IsEnum || ft.IsPointer) continue;
                path.Add(f);
                if (ft.IsDefined(typeof(NativeContainerAttribute), false)) found.Add(path.ToArray());
                else if (depth < 8) Collect(ft, path, found, depth + 1);
                path.RemoveAt(path.Count - 1);
            }
        }

        // 상자 안 struct 의 중첩 필드에 값 넣기 (중첩 struct 는 꺼내 고쳐 다시 넣는다)
        internal static void SetPath(object box, FieldInfo[] path, int depth, object value)
        {
            if (depth == path.Length - 1) { path[depth].SetValue(box, value); return; }
            object inner = path[depth].GetValue(box);
            SetPath(inner, path, depth + 1, value);
            path[depth].SetValue(box, inner);
        }

        // 표시 값: 이 포인터를 m_Safety.node 에 넣고 바이트에서 찾는다 (관리 배치 = 실제 메모리 배치)
        internal static object SentinelContainer(Type containerType, long sentinel, out bool ok)
        {
            object c = Activator.CreateInstance(containerType);
            FieldInfo sf = containerType.GetField("m_Safety", kFields);
            ok = sf != null && sf.FieldType == typeof(AtomicSafetyHandle);
            if (ok) sf.SetValue(c, new AtomicSafetyHandle { node = (SafetyNode*)sentinel, version = 0 });
            return c;
        }

        internal static int Find(ref byte start, int size, long sentinel)
        {
            for (int o = 0; o + 8 <= size; o += 4)
                if (SysUnsafe.ReadUnaligned<long>(ref SysUnsafe.Add(ref start, o)) == sentinel) return o;
            return -1;
        }

        // 컨테이너 struct 안의 m_Safety 위치 (MakeGenericMethod 로 부른다)
        internal static int SafetyOffsetIn<C>() where C : struct
        {
            const long kSentinel = 0x7E57_5AFE_0000_1230;
            object box = SentinelContainer(typeof(C), kSentinel, out bool ok);
            if (!ok) return -1;
            return Find(ref SysUnsafe.As<C, byte>(ref SysUnsafe.Unbox<C>(box)), SysUnsafe.SizeOf<C>(), kSentinel);
        }

        internal static void DisposeAt<C>(ref byte container) where C : struct, IDisposable => SysUnsafe.As<byte, C>(ref container).Dispose();
    }

    // 잡 형식마다 한 번: 이름, 검사, 컨테이너 필드 (첫 예약 때)
    internal static unsafe class JobReflection<T> where T : struct
    {
        internal static readonly string Name = JobReflectionUtil.NiceName(typeof(T));
        // Unity 와 같이 [BurstCompile] 잡은 Profiler 에 "(Burst)"
        internal static readonly bool IsBurst = typeof(T).IsDefined(typeof(NovaEngine.Burst.BurstCompileAttribute), false)
            || (typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(DisposeJob<>));
        internal static readonly byte* ProfName = ProfilerNames.Intern(IsBurst ? Name + " (Burst)" : Name);
        static readonly bool s_HasReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        static ContainerField[] s_Fields;
        static int[] s_Restrict;   // IJobParallelFor 에서 묶음 범위로 좁힐 NativeArray 의 m_Safety 위치
        static ContainerField[] s_Deallocate;

        internal static void CheckValid()
        {
            if (s_HasReferences)
                throw new InvalidOperationException(Name + " is not a valid job: job structs may not contain reference types (class, string, delegate, managed array). Use NativeArray and the other native containers instead.");
            if (s_Fields == null) Init();
        }

        internal static ContainerField[] Fields { get { if (s_Fields == null) Init(); return s_Fields; } }
        internal static bool HasRestrict { get { if (s_Fields == null) Init(); return s_Restrict.Length > 0; } }
        internal static bool HasDeallocate { get { if (s_Fields == null) Init(); return s_Deallocate.Length > 0; } }

        static void Init()
        {
            var paths = new List<FieldInfo[]>();
            JobReflectionUtil.Collect(typeof(T), new List<FieldInfo>(), paths, 0);
            var fields = new List<ContainerField>();
            if (paths.Count > 0)
            {
                object box = default(T);
                var sentinels = new long[paths.Count];
                var valid = new bool[paths.Count];
                for (int k = 0; k < paths.Count; k++)
                {
                    sentinels[k] = 0x7E57_0000_0000_0000 + (long)(k + 1) * 0x1000;
                    FieldInfo[] path = paths[k];
                    object c = JobReflectionUtil.SentinelContainer(path[path.Length - 1].FieldType, sentinels[k], out valid[k]);
                    if (valid[k]) JobReflectionUtil.SetPath(box, path, 0, c);
                }
                ref byte start = ref SysUnsafe.As<T, byte>(ref SysUnsafe.Unbox<T>(box));
                int size = SysUnsafe.SizeOf<T>();
                for (int k = 0; k < paths.Count; k++)
                {
                    if (!valid[k]) continue;
                    FieldInfo[] path = paths[k];
                    FieldInfo last = path[path.Length - 1];
                    Type ct = last.FieldType;
                    int off = JobReflectionUtil.Find(ref start, size, sentinels[k]);
                    if (off < 0) continue;
                    var names = new string[path.Length];
                    for (int i = 0; i < path.Length; i++) names[i] = path[i].Name;
                    var f = new ContainerField
                    {
                        Name = Name + "." + string.Join(".", names),
                        JobName = Name,
                        ContainerType = JobReflectionUtil.NiceName(ct),
                        SafetyOffset = off,
                        ReadOnly = last.IsDefined(typeof(ReadOnlyAttribute), false) || ct.IsDefined(typeof(NativeContainerIsReadOnlyAttribute), false),
                        WriteOnly = last.IsDefined(typeof(WriteOnlyAttribute), false),
                        Atomic = ct.IsDefined(typeof(NativeContainerIsAtomicWriteOnlyAttribute), false),
                        MinMax = ct.IsDefined(typeof(NativeContainerSupportsMinMaxWriteRestrictionAttribute), false),
                        DisableParallelRestriction = last.IsDefined(typeof(NativeDisableParallelForRestrictionAttribute), false),
                        DisableSafety = last.IsDefined(typeof(NativeDisableContainerSafetyRestrictionAttribute), false),
                        Deallocate = last.IsDefined(typeof(DeallocateOnJobCompletionAttribute), false),
                    };
                    if (f.Deallocate)
                    {
                        if (!typeof(IDisposable).IsAssignableFrom(ct))
                            throw new InvalidOperationException(f.Name + ": [DeallocateOnJobCompletion] needs a disposable container (" + f.ContainerType + ")");
                        int inner = (int)typeof(JobReflectionUtil).GetMethod(nameof(JobReflectionUtil.SafetyOffsetIn), BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(ct).Invoke(null, null);
                        f.ContainerOffset = off - inner;
                        f.Dispose = (JobReflectionUtil.RefAction)typeof(JobReflectionUtil).GetMethod(nameof(JobReflectionUtil.DisposeAt), BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(ct).CreateDelegate(typeof(JobReflectionUtil.RefAction));
                    }
                    fields.Add(f);
                }
            }
            var restrict = new List<int>();
            var dealloc = new List<ContainerField>();
            foreach (ContainerField f in fields)
            {
                if (f.MinMax && !f.ReadOnly && !f.DisableParallelRestriction && !f.DisableSafety) restrict.Add(f.SafetyOffset);
                if (f.Deallocate) dealloc.Add(f);
            }
            s_Restrict = restrict.ToArray();
            s_Deallocate = dealloc.ToArray();
            s_Fields = fields.ToArray();
        }

        /// <summary>IJobParallelFor 묶음 사본: 쓰는 NativeArray 를 [min, max] 로 좁힌다 (안전 검사)</summary>
        internal static void Restrict(ref T job, int min, int max)
        {
            ref byte b = ref SysUnsafe.As<T, byte>(ref job);
            foreach (int off in s_Restrict)
            {
                SysUnsafe.As<byte, int>(ref SysUnsafe.Add(ref b, off + JobReflectionUtil.kArrayMinFromSafety)) = min;
                SysUnsafe.As<byte, int>(ref SysUnsafe.Add(ref b, off + JobReflectionUtil.kArrayMaxFromSafety)) = max;
            }
        }

        /// <summary>[DeallocateOnJobCompletion] — 잡이 다 끝난 뒤 작업 스레드에서</summary>
        internal static void DeallocateFields(JobRecord r)
        {
            foreach (ContainerField f in s_Deallocate)
                f.Dispose(ref SysUnsafe.AsRef<byte>((byte*)r.Data + f.ContainerOffset));
        }
    }
}
