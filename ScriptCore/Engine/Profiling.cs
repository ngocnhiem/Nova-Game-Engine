using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NovaEngine.Interop;

namespace NovaEngine.Profiling
{
    // 구간 이름: 프로파일러 기록이 포인터를 오래 들고 있으므로 한 번 만들고 풀지 않는다
    internal static unsafe class ProfilerNames
    {
        static readonly ConcurrentDictionary<string, IntPtr> s_Names = new ConcurrentDictionary<string, IntPtr>();

        internal static byte* Intern(string name) => (byte*)s_Names.GetOrAdd(name ?? string.Empty, n =>
        {
            byte[] b = Encoding.UTF8.GetBytes(n + "\0");
            var p = (byte*)NativeMemory.Alloc((nuint)b.Length);
            for (int i = 0; i < b.Length; i++) p[i] = b[i];
            return (IntPtr)p;
        });

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Begin(byte* name, out long startNs, out ushort depth)
        {
            long s = 0; ushort d = 0;
            int mode = Native.Api.Prof_Begin != null ? Native.Api.Prof_Begin(name, &s, &d) : 0;
            startNs = s; depth = d;
            return mode;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void End(int mode, byte* name, long startNs, ushort depth)
        {
            if (mode != 0) Native.Api.Prof_End(mode, name, startNs, depth);
        }
    }

    // Begin / End 짝 (스레드마다 쌓기)
    internal static unsafe class ProfilerStack
    {
        struct Entry { public IntPtr Name; public long Start; public ushort Depth; public int Mode; }
        [ThreadStatic] static Entry[] t_Stack;
        [ThreadStatic] static int t_Count;

        internal static void Push(byte* name)
        {
            t_Stack ??= new Entry[64];
            int mode = ProfilerNames.Begin(name, out long s, out ushort d);
            if (t_Count < t_Stack.Length) t_Stack[t_Count] = new Entry { Name = (IntPtr)name, Start = s, Depth = d, Mode = mode };
            t_Count++;
        }
        internal static void Pop()
        {
            if (t_Count == 0) return;
            t_Count--;
            if (t_Count >= t_Stack.Length) return;
            Entry e = t_Stack[t_Count];
            ProfilerNames.End(e.Mode, (byte*)e.Name, e.Start, e.Depth);
        }
    }

    /// <summary>
    /// 이름 붙은 Profiler 구간 (Unity.Profiling.ProfilerMarker) — 메인 스레드는 Profiler 창의 계층에, 작업 스레드 (C# Job) 는 Timeline 줄에.
    /// <c>static readonly ProfilerMarker s_Marker = new ProfilerMarker("AI.Think");</c> 로 만들어 두고 <c>using (s_Marker.Auto()) { ... }</c>
    /// </summary>
    public readonly unsafe struct ProfilerMarker
    {
        internal readonly byte* m_Name;
        public ProfilerMarker(string name) { m_Name = ProfilerNames.Intern(name); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] public void Begin() => ProfilerStack.Push(m_Name);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public void End() => ProfilerStack.Pop();
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public AutoScope Auto() => new AutoScope(m_Name);

        public readonly struct AutoScope : IDisposable
        {
            readonly byte* m_Name;
            readonly long m_Start;
            readonly ushort m_Depth;
            readonly int m_Mode;
            internal AutoScope(byte* name)
            {
                m_Name = name;
                m_Mode = ProfilerNames.Begin(name, out m_Start, out m_Depth);
            }
            public void Dispose() => ProfilerNames.End(m_Mode, m_Name, m_Start, m_Depth);
        }
    }

    /// <summary>Profiler 구간 (UnityEngine.Profiling.Profiler) — 같은 스레드에서 BeginSample · EndSample 짝으로</summary>
    public static unsafe class Profiler
    {
        public static void BeginSample(string name) => ProfilerStack.Push(ProfilerNames.Intern(name));
        public static void BeginSample(string name, NovaEngine.Object targetObject) => BeginSample(name);
        public static void EndSample() => ProfilerStack.Pop();
    }
}
