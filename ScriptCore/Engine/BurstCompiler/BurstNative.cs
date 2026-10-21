using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NovaEngine.Interop;
using NovaEngine.Jobs;

namespace NovaEngine.Burst.Compiler
{
    /// <summary>네이티브 잡에 넘기는 칸 (C++ NBContext 와 같은 배치)</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NBContext
    {
        public int Error;
        public int ThreadIndex;
        public delegate* unmanaged<int, byte*, void> Log;
        public fixed byte Message[512];
    }

    internal sealed class BurstNativeException : Exception
    {
        public BurstNativeException(string message) : base(message) { }
    }

    internal static unsafe class BurstRuntime
    {
        static readonly ConcurrentQueue<(int level, string text)> s_Logs = new ConcurrentQueue<(int, string)>();

        [UnmanagedCallersOnly]
        static void NativeLog(int level, byte* text) => s_Logs.Enqueue((level, Marshal.PtrToStringUTF8((IntPtr)text)));

        internal static void FlushLogs()
        {
            while (s_Logs.TryDequeue(out var l))
            {
                if (l.level == 2) Debug.LogError(l.text);
                else if (l.level == 1) Debug.LogWarning(l.text);
                else Debug.Log(l.text);
            }
        }

        /// <summary>네이티브 잡 실행 — 실패 (범위 밖 · throw) 면 예외로 (잡 실행기가 Console 에)</summary>
        internal static void Invoke(IntPtr fn, void* data, int begin, int end)
        {
            NBContext ctx = default;
            ctx.ThreadIndex = JobScheduler.t_WorkerIndex;
            ctx.Log = &NativeLog;
            ((delegate* unmanaged<void*, int, int, NBContext*, void>)fn)(data, begin, end, &ctx);
            if (ctx.Error != 0)
            {
                int n = 0;
                while (n < 512 && ctx.Message[n] != 0) n++;
                throw new BurstNativeException(Encoding.UTF8.GetString(ctx.Message, n) + " (native Burst code)");
            }
        }

        internal static bool Enabled => BurstCompiler.Options.EnableBurstCompilation;
    }

    /// <summary>
    /// [BurstCompile] 2 단계 — 게임 어셈블리의 [BurstCompile] 잡을 C++ 로 옮겨 MSVC 로 프로젝트 DLL (Library/Burst/지문/) 을 만들고,
    /// 잡 실행 함수를 네이티브로 바꾼다. 번역 · 빌드는 백그라운드 (그동안 · 실패하면 JIT), 지문이 같으면 다시 빌드하지 않는다.
    /// </summary>
    internal static unsafe class BurstNative
    {
        internal const string Version = "nb2";

        internal sealed class Entry
        {
            public Type JobType, DataType;
            public string Kind;            // IJob · IJobFor · IJobParallelFor · IJobParallelForBatch · IJobParallelForTransform · IJobParallelForTransformReadOnly
            public string Export;
            public string Status = "pending";   // pending · native · jit
            public string Reason;
            public BurstUnit Unit;
            public string Code;
            public bool Checks;
        }

        static readonly object s_Lock = new object();
        static readonly object s_BuildLock = new object();
        static List<Entry> s_Entries = new List<Entry>();
        static string s_State = "idle";
        static string s_Message = "";
        static string s_Dll;
        static double s_Seconds;
        static IntPtr s_Lib;
        static int s_Generation;
        static Assembly s_Game;
        static string s_ProjectRoot;
        static string s_GameDll;
        static Task s_Task;

        internal static bool Available => s_Lib != IntPtr.Zero;

        // ---------------------------------------------------------------- 잡 찾기
        static List<Entry> FindJobs(Assembly game)
        {
            var list = new List<Entry>();
            Type[] types;
            try { types = game.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            foreach (Type t in types)
            {
                if (!t.IsValueType || t.IsGenericTypeDefinition || !t.IsDefined(typeof(BurstCompileAttribute), false)) continue;
                foreach (Type i in t.GetInterfaces())
                {
                    if (i == typeof(IJob)) list.Add(new Entry { JobType = t, DataType = t, Kind = "IJob" });
                    else if (i == typeof(IJobFor)) list.Add(new Entry { JobType = t, DataType = t, Kind = "IJobFor" });
                    else if (i == typeof(IJobParallelFor)) list.Add(new Entry { JobType = t, DataType = t, Kind = "IJobParallelFor" });
                    else if (i == typeof(IJobParallelForBatch)) list.Add(new Entry { JobType = t, DataType = t, Kind = "IJobParallelForBatch" });
                    else if (i == typeof(IJobParallelForTransform))
                    {
                        list.Add(new Entry { JobType = t, DataType = typeof(TransformJobData<>).MakeGenericType(t), Kind = "IJobParallelForTransform" });
                        list.Add(new Entry { JobType = t, DataType = typeof(TransformJobDataReadOnly<>).MakeGenericType(t), Kind = "IJobParallelForTransformReadOnly" });
                    }
                }
            }
            for (int k = 0; k < list.Count; k++) list[k].Export = "nb_job_" + k;
            return list;
        }

        // ---------------------------------------------------------------- 번역
        static MethodInfo ExecuteOf(Type job, Type iface)
        {
            InterfaceMapping map = job.GetInterfaceMap(iface);
            for (int k = 0; k < map.InterfaceMethods.Length; k++) if (map.InterfaceMethods[k].Name == "Execute") return map.TargetMethods[k];
            throw new BurstUnsupportedException("no Execute on " + BurstTypes.Display(job));
        }

        static bool ChecksFor(Entry e, bool editorChecks)
        {
            if (!editorChecks) return false;
            if (BurstCompiler.Options.ForceEnableBurstSafetyChecks) return true;
            if (!BurstCompiler.Options.EnableBurstSafetyChecks) return false;
            return e.JobType.GetCustomAttribute<BurstCompileAttribute>()?.DisableSafetyChecks != true;
        }

        static void Translate(Entry e, bool checks, int index)
        {
            checks = ChecksFor(e, checks);
            e.Checks = checks;
            var u = new BurstUnit("nbj" + index, checks);   // ("j0" 은 math.h 의 베셀 함수)
            Type iface = e.Kind.StartsWith("IJobParallelForTransform", StringComparison.Ordinal) ? typeof(IJobParallelForTransform)
                : e.Kind == "IJob" ? typeof(IJob) : e.Kind == "IJobFor" ? typeof(IJobFor) : e.Kind == "IJobParallelFor" ? typeof(IJobParallelFor) : typeof(IJobParallelForBatch);
            MethodInfo exec = ExecuteOf(e.JobType, iface);
            BurstMethodCompiler m = u.Require(exec);
            m.ForceInline = true;   // 잡 고리 안에 펼쳐 원소마다 벡터화
            u.Run();
            string data = u.Types.CType(e.DataType);
            string fc = m.MayFail ? " if (c->error) return;" : "";
            var sb = new StringBuilder();
            sb.Append("static void entry(void* d, int32_t b, int32_t e, NBContext* c)\n{\n    nb_enter(c);\n");
            switch (e.Kind)
            {
                case "IJob":
                    sb.Append("    ").Append(data).Append(" local = *(").Append(data).Append("*)d;\n    ").Append(m.CName).Append("(&local);\n");
                    break;
                case "IJobFor":
                case "IJobParallelFor":
                    sb.Append("    ").Append(data).Append(" local = *(").Append(data).Append("*)d;\n");
                    sb.Append("    for (int32_t i = b; i < e; ++i) { ").Append(m.CName).Append("(&local, i);").Append(fc).Append(" }\n");
                    break;
                case "IJobParallelForBatch":
                    sb.Append("    ").Append(data).Append(" local = *(").Append(data).Append("*)d;\n    ").Append(m.CName).Append("(&local, b, e - b);\n");
                    break;
                default:
                {
                    // TransformJobData<T>: Job · Transforms (TransformAccessArray → m_Data → Buffer)
                    FieldInfo fJob = e.DataType.GetField("Job"), fArr = e.DataType.GetField("Transforms");
                    FieldInfo fData = typeof(TransformAccessArray).GetField("m_Data", BindingFlags.Instance | BindingFlags.NonPublic);
                    FieldInfo fBuf = typeof(TransformArrayData).GetField("Buffer");
                    FieldInfo fTa = typeof(TransformAccess).GetField("m_Data", BindingFlags.Instance | BindingFlags.NonPublic);
                    string job = u.Types.CType(e.JobType), ta = u.Types.CType(typeof(TransformAccess)), tad = u.Types.CType(typeof(TransformAccessData));
                    sb.Append("    ").Append(data).Append(" local = *(").Append(data).Append("*)d;\n");
                    sb.Append("    ").Append(job).Append("* job = &").Append(u.Types.FieldAccess("local", false, fJob)).Append(";\n");
                    string arr = u.Types.FieldAccess("local", false, fArr);
                    sb.Append("    ").Append(tad).Append("* buf = ").Append(u.Types.FieldAccess(u.Types.FieldAccess(arr, false, fData), true, fBuf)).Append(";\n");
                    sb.Append("    for (int32_t i = b; i < e; ++i) { ").Append(ta).Append(" t{}; ").Append(u.Types.FieldAccess("t", false, fTa)).Append(" = buf + i; ")
                      .Append(m.CName).Append("(job, i, t);").Append(fc).Append(" }\n");
                    break;
                }
            }
            sb.Append("}\n");
            string entry = sb.ToString();
            string code = u.Emit(entry);
            // FloatMode.Fast: 이 잡만 순서 바꾸기 · FMA 를 허락 (나머지는 .NET 과 같은 /fp:precise)
            if (e.JobType.GetCustomAttribute<BurstCompileAttribute>()?.FloatMode == FloatMode.Fast)
                code = "#pragma float_control(precise, off, push)\n#pragma fp_contract(on)\n" + code + "#pragma fp_contract(off)\n#pragma float_control(pop)\n";
            code += "extern \"C\" __declspec(dllexport) void " + e.Export + "(void* d, int32_t b, int32_t e, NBContext* c) { " + u.Ns + "::entry(d, b, e, c); }\n";
            e.Unit = u;
            e.Code = code;
        }

        // ---------------------------------------------------------------- C++ 앞부분 (.NET 과 같은 뜻의 도우미)
        internal const string Prelude = @"// NOVA Burst (C# [BurstCompile] → C++) — 자동 생성, 고치지 말 것
#include <stdint.h>
#include <string.h>
#include <math.h>
#include <stdio.h>
#include <malloc.h>
#include <intrin.h>
#include <immintrin.h>
#pragma warning(disable: 4102 4100 4189 4244 4146 4700 4723 4307 4305 4309 4018 4389 4101 4800)
struct NBContext { int32_t error; int32_t threadIndex; void (*log)(int32_t, const char*); char message[512]; };
static thread_local NBContext* nb_ctx = nullptr;
static inline void nb_enter(NBContext* c) { nb_ctx = c; }
static inline bool nb_failed() { return nb_ctx->error != 0; }
static __declspec(noinline) void nb_fail(const char* m) { if (!nb_ctx->error) { nb_ctx->error = 1; strncpy_s(nb_ctx->message, sizeof(nb_ctx->message), m, _TRUNCATE); } }
static __declspec(noinline) void nb_fail_index(int32_t i, int32_t n) { if (!nb_ctx->error) { nb_ctx->error = 1; snprintf(nb_ctx->message, sizeof(nb_ctx->message), ""IndexOutOfRangeException: Index %d is out of range of '%d' Length."", i, n); } }
static __declspec(noinline) void nb_fail_restricted(int32_t i, int32_t lo, int32_t hi) { if (!nb_ctx->error) { nb_ctx->error = 1; snprintf(nb_ctx->message, sizeof(nb_ctx->message), ""IndexOutOfRangeException: Index %d is out of restricted IJobParallelFor range [%d...%d] in ReadWriteBuffer."", i, lo, hi); } }
static inline void nb_log(int32_t level, const char* m) { if (nb_ctx->log) nb_ctx->log(level, m); }
static inline int32_t nb_thread_index() { return nb_ctx->threadIndex; }
template<class To, class From> static __forceinline To nb_bitcast(From f) { static_assert(sizeof(To) == sizeof(From), ""bitcast""); To t; memcpy(&t, &f, sizeof(To)); return t; }
template<class T> static __forceinline T nb_read_unaligned(const void* p) { T t; memcpy(&t, p, sizeof(T)); return t; }
static __forceinline float nb_nanf() { return nb_bitcast<float>(0x7FC00000u); }
static __forceinline float nb_inff() { return nb_bitcast<float>(0x7F800000u); }
// .NET MathF.Min / Max (NaN · -0 과 같은 뜻)
static __forceinline float nb_minf(float a, float b) { if (a != b) { if (a == a) return a < b ? a : b; return a; } return signbit(a) ? a : b; }
static __forceinline float nb_maxf(float a, float b) { if (a != b) { if (a == a) return b < a ? a : b; return a; } return signbit(b) ? a : b; }
static __forceinline double nb_min(double a, double b) { if (a != b) { if (a == a) return a < b ? a : b; return a; } return signbit(a) ? a : b; }
static __forceinline double nb_max(double a, double b) { if (a != b) { if (a == a) return b < a ? a : b; return a; } return signbit(b) ? a : b; }
template<class T> static __forceinline T nb_min(T a, T b) { return a < b ? a : b; }
template<class T> static __forceinline T nb_max(T a, T b) { return a > b ? a : b; }
static __forceinline float nb_clampf(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
template<class T> static __forceinline T nb_clamp(T v, T lo, T hi) { return v < lo ? lo : (v > hi ? hi : v); }
static __forceinline int32_t nb_signf(float x) { return x > 0 ? 1 : (x < 0 ? -1 : 0); }
template<class T> static __forceinline int32_t nb_sign(T x) { return x > 0 ? 1 : (x < 0 ? -1 : 0); }
template<class T> static __forceinline T nb_abs(T x) { return x < 0 ? (T)(0 - x) : x; }
static __forceinline int32_t nb_lzcnt32(uint32_t x) { unsigned long i; return _BitScanReverse(&i, x) ? 31 - (int32_t)i : 32; }
static __forceinline int32_t nb_lzcnt64(uint64_t x) { unsigned long i; return _BitScanReverse64(&i, x) ? 63 - (int32_t)i : 64; }
static __forceinline int32_t nb_tzcnt32(uint32_t x) { unsigned long i; return _BitScanForward(&i, x) ? (int32_t)i : 32; }
static __forceinline int32_t nb_tzcnt64(uint64_t x) { unsigned long i; return _BitScanForward64(&i, x) ? (int32_t)i : 64; }
static __forceinline int32_t nb_popcnt32(uint32_t x) { return (int32_t)__popcnt(x); }
static __forceinline int32_t nb_popcnt64(uint64_t x) { return (int32_t)__popcnt64(x); }
static __forceinline int32_t nb_log2i32(uint32_t x) { return x == 0 ? 0 : 31 - nb_lzcnt32(x); }
static __forceinline int32_t nb_log2i64(uint64_t x) { return x == 0 ? 0 : 63 - nb_lzcnt64(x); }
static __forceinline uint32_t nb_rotl32(uint32_t x, int32_t n) { return _rotl(x, n); }
static __forceinline uint32_t nb_rotr32(uint32_t x, int32_t n) { return _rotr(x, n); }
static __forceinline uint64_t nb_rotl64(uint64_t x, int32_t n) { return _rotl64(x, n); }
static __forceinline uint64_t nb_rotr64(uint64_t x, int32_t n) { return _rotr64(x, n); }
static __forceinline int32_t nb_ispow2_32(uint32_t x) { return x != 0 && (x & (x - 1)) == 0; }
static __forceinline int32_t nb_inc32(void* p) { return (int32_t)_InterlockedIncrement((volatile long*)p); }
static __forceinline int32_t nb_dec32(void* p) { return (int32_t)_InterlockedDecrement((volatile long*)p); }
static __forceinline int32_t nb_add32(void* p, int64_t v) { return (int32_t)(_InterlockedExchangeAdd((volatile long*)p, (long)v) + (long)v); }
static __forceinline int32_t nb_xchg32(void* p, int64_t v) { return (int32_t)_InterlockedExchange((volatile long*)p, (long)v); }
static __forceinline int32_t nb_cmpxchg32(void* p, int64_t v, int64_t c) { return (int32_t)_InterlockedCompareExchange((volatile long*)p, (long)v, (long)c); }
static __forceinline int32_t nb_and32(void* p, int64_t v) { return (int32_t)_InterlockedAnd((volatile long*)p, (long)v); }
static __forceinline int32_t nb_or32(void* p, int64_t v) { return (int32_t)_InterlockedOr((volatile long*)p, (long)v); }
static __forceinline int64_t nb_inc64(void* p) { return _InterlockedIncrement64((volatile __int64*)p); }
static __forceinline int64_t nb_dec64(void* p) { return _InterlockedDecrement64((volatile __int64*)p); }
static __forceinline int64_t nb_add64(void* p, int64_t v) { return _InterlockedExchangeAdd64((volatile __int64*)p, v) + v; }
static __forceinline int64_t nb_xchg64(void* p, int64_t v) { return _InterlockedExchange64((volatile __int64*)p, v); }
static __forceinline int64_t nb_cmpxchg64(void* p, int64_t v, int64_t c) { return _InterlockedCompareExchange64((volatile __int64*)p, v, c); }
static __forceinline int64_t nb_and64(void* p, int64_t v) { return _InterlockedAnd64((volatile __int64*)p, v); }
static __forceinline int64_t nb_or64(void* p, int64_t v) { return _InterlockedOr64((volatile __int64*)p, v); }
static __forceinline void nb_barrier() { _ReadWriteBarrier(); __faststorefence(); }
static __forceinline void nb_spin(int32_t n) { for (int32_t i = 0; i < n; ++i) _mm_pause(); }
static inline float nb_half_to_float(uint16_t h)
{
    uint32_t s = (uint32_t)(h & 0x8000) << 16, e = (h >> 10) & 0x1F, m = h & 0x3FF, f;
    if (e == 0) { if (m == 0) f = s; else { e = 1; while (!(m & 0x400)) { m <<= 1; --e; } m &= 0x3FF; f = s | ((e + 112) << 23) | (m << 13); } }
    else if (e == 31) f = s | 0x7F800000u | (m << 13);
    else f = s | ((e + 112) << 23) | (m << 13);
    return nb_bitcast<float>(f);
}
static inline uint16_t nb_float_to_half(float x)
{
    uint32_t f = nb_bitcast<uint32_t>(x), s = (f >> 16) & 0x8000; int32_t e = (int32_t)((f >> 23) & 0xFF) - 112; uint32_t m = f & 0x7FFFFF;
    if (((f >> 23) & 0xFF) == 0xFF) return (uint16_t)(s | 0x7C00 | (m ? 0x200 : 0));
    if (e <= 0) { if (e < -10) return (uint16_t)s; m |= 0x800000; uint32_t sh = (uint32_t)(14 - e); uint32_t hm = m >> sh; uint32_t rem = m & ((1u << sh) - 1), half = 1u << (sh - 1); if (rem > half || (rem == half && (hm & 1))) ++hm; return (uint16_t)(s | hm); }
    if (e >= 31) return (uint16_t)(s | 0x7C00);
    uint32_t hm = m >> 13, rem = m & 0x1FFF; uint32_t r = (uint32_t)(s | (e << 10) | hm); if (rem > 0x1000 || (rem == 0x1000 && (hm & 1))) ++r; return (uint16_t)r;
}
";

        // ---------------------------------------------------------------- 빌드
        static string s_VcVars;
        static string s_VcVarsLog = "";
        static string FindVcVars()
        {
            if (s_VcVars != null && s_VcVars.Length > 0) return s_VcVars;
            string env = Environment.GetEnvironmentVariable("NOVA_VCVARS");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return s_VcVars = env;
            var log = new StringBuilder();
            string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (File.Exists(vswhere))
            {
                try
                {
                    var psi = new ProcessStartInfo(vswhere, @"-latest -products * -find VC\Auxiliary\Build\vcvars64.bat")
                    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                    using var p = Process.Start(psi);
                    var err = p.StandardError.ReadToEndAsync();
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    string first = o.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0 && File.Exists(x));
                    if (first != null) return s_VcVars = first;
                    log.Append("vswhere: '").Append(o.Trim()).Append("' ").Append(err.Result.Trim()).Append("; ");
                }
                catch (Exception e) { log.Append("vswhere failed: ").Append(e.Message).Append("; "); }
            }
            else log.Append("no vswhere; ");
            // 설치 폴더를 직접 (Program Files · (x86) 의 Visual Studio */*/VC/Auxiliary/Build)
            foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                string vs = Path.Combine(root, "Microsoft Visual Studio");
                if (!Directory.Exists(vs)) continue;
                try
                {
                    foreach (string ver in Directory.GetDirectories(vs).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                        foreach (string ed in Directory.GetDirectories(ver))
                        {
                            string c = Path.Combine(ed, "VC", "Auxiliary", "Build", "vcvars64.bat");
                            if (File.Exists(c)) return s_VcVars = c;
                        }
                }
                catch (Exception e) { log.Append(e.Message).Append("; "); }
            }
            s_VcVarsLog = log.ToString();
            return null;
        }

        static bool RunCl(string dir, string cpp, string dll, bool avx2, out string output)
        {
            string vcvars = FindVcVars();
            if (vcvars == null) { output = "no MSVC C++ compiler found (install Visual Studio or Build Tools with 'Desktop development with C++', or set NOVA_VCVARS to vcvars64.bat) — " + s_VcVarsLog; return false; }
            string arch = avx2 ? "/arch:AVX2" : "/arch:AVX";
            string cmd = "/c \"\"" + vcvars + "\" >nul && cl.exe /nologo /O2 /Oi /GS- /EHs-c- /GR- " + arch + " /fp:precise /std:c++17 /LD /utf-8 \"" + cpp + "\" /Fe:\"" + dll + "\" /link /NOLOGO\"";
            var psi = new ProcessStartInfo("cmd.exe", cmd) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.Environment["VSCMD_SKIP_SENDTELEMETRY"] = "1";
            using var p = Process.Start(psi);
            var so = p.StandardOutput.ReadToEndAsync();
            string se = p.StandardError.ReadToEnd();
            p.WaitForExit();
            output = so.Result + se;
            return p.ExitCode == 0 && File.Exists(dll);
        }

        static string Hash(string gameDll, bool checks, bool avx2)
        {
            using var sha = SHA256.Create();
            var ms = new MemoryStream();
            byte[] dll = File.ReadAllBytes(gameDll);
            ms.Write(dll, 0, dll.Length);
            byte[] extra = Encoding.UTF8.GetBytes(Version + "|" + typeof(BurstNative).Assembly.ManifestModule.ModuleVersionId + "|" + checks + "|" + avx2 + "|" + BurstCompiler.Options.EnableBurstSafetyChecks + "|" + BurstCompiler.Options.ForceEnableBurstSafetyChecks);
            ms.Write(extra, 0, extra.Length);
            return Convert.ToHexString(sha.ComputeHash(ms.ToArray())).Substring(0, 16).ToLowerInvariant();
        }

        // 번역 + 빌드 (실패한 잡은 빼고 다시) — 돌려준 값: DLL 경로 (null = 없음)
        static string Build(List<Entry> entries, string dir, bool checks, bool avx2, StringBuilder log)
        {
            Directory.CreateDirectory(dir);
            for (int k = 0; k < entries.Count; k++)
            {
                Entry e = entries[k];
                try { Translate(e, checks, k); e.Status = "pending"; }
                catch (BurstUnsupportedException ex) { e.Status = "jit"; e.Reason = ex.Message; }
                catch (Exception ex) { e.Status = "jit"; e.Reason = "translator error: " + ex.GetType().Name + ": " + ex.Message; }
            }
            for (int attempt = 0; attempt < 2; attempt++)
            {
                List<Entry> ok = entries.Where(x => x.Status == "pending" || x.Status == "native").ToList();
                if (ok.Count == 0) return null;
                string cpp = Path.Combine(dir, "nova_burst.cpp");
                File.WriteAllText(cpp, Prelude + string.Concat(ok.Select(x => x.Code)), new UTF8Encoding(false));
                string dll = Path.Combine(dir, "NovaBurst.dll");
                if (RunCl(dir, cpp, dll, avx2, out string output)) { foreach (Entry x in ok) x.Status = "native"; return dll; }
                log.Append(output);
                if (attempt == 1) break;
                // 어느 잡이 C++ 컴파일을 깨는지 하나씩 (번역기 버그 — 그 잡만 JIT 로)
                foreach (Entry x in ok)
                {
                    string one = Path.Combine(dir, "probe.cpp");
                    File.WriteAllText(one, Prelude + x.Code, new UTF8Encoding(false));
                    if (!RunCl(dir, one, Path.Combine(dir, "probe.dll"), avx2, out string err))
                    {
                        x.Status = "jit";
                        string line = err.Split('\n').FirstOrDefault(l => l.Contains("error")) ?? err;
                        x.Reason = "C++ compile error: " + line.Trim();
                    }
                }
            }
            return null;
        }

        static void WriteManifest(string dir, List<Entry> entries)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("version", Version);
                w.WriteStartArray("jobs");
                foreach (Entry e in entries)
                {
                    w.WriteStartObject();
                    w.WriteString("job", e.JobType.FullName);
                    w.WriteString("kind", e.Kind);
                    w.WriteString("status", e.Status);
                    w.WriteString("export", e.Export);
                    w.WriteBoolean("checks", e.Checks);
                    if (e.Reason != null) w.WriteString("reason", e.Reason);
                    w.WriteNumber("size", BurstTypes.SizeOf(e.DataType));
                    w.WriteStartArray("globals");
                    if (e.Unit != null && e.Status == "native")
                        foreach (var (f, export, _) in e.Unit.RuntimeGlobals)
                        {
                            w.WriteStartObject();
                            w.WriteString("type", f.DeclaringType.AssemblyQualifiedName);
                            w.WriteString("field", f.Name);
                            w.WriteString("export", export);
                            w.WriteEndObject();
                        }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.WriteAllBytes(Path.Combine(dir, "NovaBurst.json"), ms.ToArray());
        }

        // ---------------------------------------------------------------- 묶기 (DLL → 잡 실행 함수)
        static void SetNative(Type dataType, IntPtr fn)
        {
            FieldInfo f = typeof(JobReflection<>).MakeGenericType(dataType).GetField("s_NativePtr", BindingFlags.Static | BindingFlags.NonPublic);
            f.SetValue(null, fn);
        }

        static bool Bind(string dll, string manifest, List<Entry> entries, Assembly game)
        {
            IntPtr lib;
            try { lib = NativeLibrary.Load(dll); }
            catch (Exception e) { s_Message = "can not load " + dll + ": " + e.Message; return false; }
            using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
            var byKey = entries.ToDictionary(x => x.JobType.FullName + "|" + x.Kind);
            int bound = 0;
            foreach (JsonElement j in doc.RootElement.GetProperty("jobs").EnumerateArray())
            {
                string key = j.GetProperty("job").GetString() + "|" + j.GetProperty("kind").GetString();
                if (!byKey.TryGetValue(key, out Entry e)) continue;
                e.Status = j.GetProperty("status").GetString();
                e.Reason = j.TryGetProperty("reason", out JsonElement r) ? r.GetString() : null;
                e.Checks = j.TryGetProperty("checks", out JsonElement ck) && ck.ValueKind == JsonValueKind.True;
                if (e.Status != "native") continue;
                if (j.GetProperty("size").GetInt32() != BurstTypes.SizeOf(e.DataType)) { e.Status = "jit"; e.Reason = "job layout changed"; continue; }
                foreach (JsonElement g in j.GetProperty("globals").EnumerateArray())
                {
                    Type owner = Type.GetType(g.GetProperty("type").GetString()) ?? game.GetType(g.GetProperty("type").GetString().Split(',')[0]);
                    FieldInfo f = owner?.GetField(g.GetProperty("field").GetString(), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f == null || !NativeLibrary.TryGetExport(lib, g.GetProperty("export").GetString(), out IntPtr init)) { e.Status = "jit"; e.Reason = "static value not found"; break; }
                    byte[] bytes = BurstTypes.Bytes(f.FieldType, f.GetValue(null));
                    fixed (byte* p = bytes) ((delegate* unmanaged<void*, void>)init)(p);
                }
                if (e.Status != "native") continue;
                if (!NativeLibrary.TryGetExport(lib, e.Export, out IntPtr fn)) { e.Status = "jit"; e.Reason = "export missing"; continue; }
                SetNative(e.DataType, fn);
                bound++;
            }
            s_Lib = lib;
            s_Dll = dll;
            return bound > 0;
        }

        // ---------------------------------------------------------------- 시점 (Bridge 가 부른다)
        /// <summary>게임 어셈블리를 읽은 뒤: 에디터는 캐시 · 백그라운드 빌드, 플레이어는 빌드에 든 DLL</summary>
        internal static void OnGameAssemblyLoaded(Assembly game, string gameDllPath, bool player)
        {
            s_Game = game;
            s_GameDll = gameDllPath;
            int generation = ++s_Generation;
            List<Entry> entries;
            try { entries = FindJobs(game); }
            catch (Exception e) { s_State = "failed"; s_Message = e.Message; return; }
            lock (s_Lock) { s_Entries = entries; }
            if (entries.Count == 0) { s_State = "idle"; s_Message = "no [BurstCompile] jobs"; return; }
            string dllDir = Path.GetDirectoryName(Path.GetFullPath(gameDllPath));
            if (player)
            {
                string dir = Path.Combine(Path.GetDirectoryName(dllDir), "Burst");
                string dll = Path.Combine(dir, "NovaBurst.dll"), man = Path.Combine(dir, "NovaBurst.json");
                if (File.Exists(dll) && File.Exists(man) && Bind(dll, man, entries, game)) s_State = "ready";
                else { s_State = "jit"; s_Message = "no native Burst library in the build"; }
                return;
            }
            s_ProjectRoot = Path.GetDirectoryName(Path.GetDirectoryName(dllDir));   // <프로젝트>/Library/ScriptAssemblies
            bool sync = entries.Any(x => x.JobType.GetCustomAttribute<BurstCompileAttribute>()?.CompileSynchronously == true);
            StartCompile(generation, entries, sync);
        }

        static void StartCompile(int generation, List<Entry> entries, bool wait)
        {
            bool checks = NovaEngine.Collections.CollectionChecks.Enabled;
            bool avx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
            string hash;
            try { hash = Hash(s_GameDll, checks, avx2); }
            catch (Exception e) { s_State = "failed"; s_Message = e.Message; return; }
            string dir = Path.Combine(s_ProjectRoot, "Library", "Burst", hash);
            string cachedDll = Path.Combine(dir, "NovaBurst.dll"), cachedMan = Path.Combine(dir, "NovaBurst.json");
            if (File.Exists(cachedDll) && File.Exists(cachedMan))
            {
                if (Bind(cachedDll, cachedMan, entries, s_Game)) { s_State = "ready"; s_Message = "cached"; s_Seconds = 0; return; }
            }
            s_State = "compiling";
            s_Message = "";
            var game = s_Game;
            Task t = Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var log = new StringBuilder();
                string dll = null;
                try { lock (s_BuildLock) dll = Build(entries, dir, checks, avx2, log); }
                catch (Exception e) { log.Append(e.ToString()); }
                if (generation != s_Generation) return;   // 그사이 다시 읽었다
                try { WriteManifest(dir, entries); } catch { }
                if (dll != null && Bind(dll, Path.Combine(dir, "NovaBurst.json"), entries, game))
                {
                    s_State = "ready";
                    s_Seconds = sw.Elapsed.TotalSeconds;
                    int native = entries.Count(x => x.Status == "native"), jit = entries.Count(x => x.Status == "jit");
                    string msg = "Burst: " + native + " job(s) compiled to native code in " + s_Seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " s";
                    if (jit > 0) msg += "; " + jit + " stay with the JIT: " + string.Join("; ", entries.Where(x => x.Status == "jit").Select(x => BurstTypes.Display(x.JobType) + " — " + x.Reason));
                    s_Message = msg;
                    Enqueue(jit > 0 ? 1 : 0, msg);
                }
                else
                {
                    s_State = entries.Any(x => x.Status == "native" || x.Status == "pending") ? "failed" : "jit";
                    foreach (Entry x in entries) if (x.Status != "jit") { x.Status = "jit"; x.Reason ??= "native build failed"; }
                    string first = log.ToString().Split('\n').FirstOrDefault(l => l.Contains("error")) ?? log.ToString();
                    s_Message = first.Length > 600 ? first.Substring(0, 600) : first;
                    Enqueue(1, "Burst: native compilation failed, jobs run with the JIT — " + s_Message.Trim() + (entries.Count > 0 && entries.All(x => x.Reason != null) ? " (" + string.Join("; ", entries.Select(x => BurstTypes.Display(x.JobType) + ": " + x.Reason)) + ")" : ""));
                }
            });
            s_Task = t;
            if (wait) t.Wait();
        }

        static readonly ConcurrentQueue<(int level, string text)> s_Messages = new ConcurrentQueue<(int, string)>();
        static void Enqueue(int level, string text) => s_Messages.Enqueue((level, text));
        internal static void Flush()
        {
            while (s_Messages.TryDequeue(out var m))
            {
                if (m.level == 1) Debug.LogWarning(m.text); else Debug.Log(m.text);
            }
            BurstRuntime.FlushLogs();
        }

        /// <summary>게임 어셈블리를 내리기 전 (모든 잡은 이미 끝났다)</summary>
        internal static void Unload()
        {
            ++s_Generation;   // 도는 빌드는 그대로 두고 (끝나면 세대가 달라 묶지 않는다) — 스크립트 다시 읽기를 멈추지 않게
            if (s_Lib != IntPtr.Zero) { try { NativeLibrary.Free(s_Lib); } catch { } s_Lib = IntPtr.Zero; }
            lock (s_Lock) s_Entries = new List<Entry>();
            s_Game = null;
            s_State = "idle";
        }

        // ---------------------------------------------------------------- CLI (nova burst)
        internal static string Command(string json)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(json) ? "{}" : json);
            JsonElement a = doc.RootElement;
            string op = a.TryGetProperty("op", out JsonElement o) ? o.GetString() : "info";
            if (op == "compile")
            {
                if (s_Game == null) return Error("no game assembly");
                bool wait = !a.TryGetProperty("wait", out JsonElement w) || w.ValueKind != JsonValueKind.False;
                try { s_Task?.Wait(); } catch { }   // 도는 빌드가 끝난 뒤
                if (s_Lib != IntPtr.Zero) { foreach (Entry e in s_Entries) if (e.Status == "native") SetNative(e.DataType, IntPtr.Zero); try { NativeLibrary.Free(s_Lib); } catch { } s_Lib = IntPtr.Zero; }
                List<Entry> entries = FindJobs(s_Game);
                lock (s_Lock) s_Entries = entries;
                bool force = a.TryGetProperty("force", out JsonElement fo) && fo.ValueKind == JsonValueKind.True;
                if (force)
                {
                    try { Directory.Delete(Path.Combine(s_ProjectRoot, "Library", "Burst", Hash(s_GameDll, NovaEngine.Collections.CollectionChecks.Enabled, System.Runtime.Intrinsics.X86.Avx2.IsSupported)), true); } catch { }
                }
                StartCompile(++s_Generation, entries, wait);
                return Info();
            }
            if (op == "set")
            {
                static bool B(JsonElement x) => x.ValueKind == JsonValueKind.True || (x.ValueKind == JsonValueKind.String && x.GetString() == "true");
                if (a.TryGetProperty("enabled", out JsonElement en)) BurstCompiler.Options.EnableBurstCompilation = B(en);
                if (a.TryGetProperty("safetyChecks", out JsonElement sc)) BurstCompiler.Options.EnableBurstSafetyChecks = B(sc);   // 다음 compile 부터
                return Info();
            }
            if (op == "player")
            {
                // 플레이어 빌드: 안전 검사 없이 (Unity 와 같다), AVX (2011 년 뒤 CPU)
                if (s_Game == null) return Error("no game assembly");
                string outDir = a.TryGetProperty("out", out JsonElement od) ? od.GetString() : null;
                if (string.IsNullOrEmpty(outDir)) return Error("player needs --out <dir>");
                List<Entry> entries = FindJobs(s_Game);
                var log = new StringBuilder();
                var sw = Stopwatch.StartNew();
                string dll = null;
                if (entries.Count > 0) lock (s_BuildLock) dll = Build(entries, outDir, false, false, log);
                if (entries.Count > 0) WriteManifest(outDir, entries);
                foreach (string junk in new[] { "probe.cpp", "probe.dll", "probe.obj", "nova_burst.obj", "NovaBurst.lib", "NovaBurst.exp", "probe.lib", "probe.exp" })
                    try { File.Delete(Path.Combine(outDir, junk)); } catch { }
                return Json(w =>
                {
                    w.WriteBoolean("ok", dll != null || entries.Count == 0);
                    w.WriteString("dll", dll ?? "");
                    w.WriteNumber("jobs", entries.Count);
                    w.WriteNumber("native", entries.Count(x => x.Status == "native"));
                    w.WriteNumber("seconds", Math.Round(sw.Elapsed.TotalSeconds, 2));
                    if (dll == null && entries.Count > 0) w.WriteString("error", log.ToString().Length > 800 ? log.ToString().Substring(0, 800) : log.ToString());
                });
            }
            if (op == "source")
            {
                string dir = s_Dll != null ? Path.GetDirectoryName(s_Dll) : null;
                return Json(w => w.WriteString("cpp", dir != null ? Path.Combine(dir, "nova_burst.cpp") : ""));
            }
            return Info();
        }

        static string Error(string e) => Json(w => w.WriteString("error", e));

        static string Json(Action<Utf8JsonWriter> body)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms)) { w.WriteStartObject(); body(w); w.WriteEndObject(); }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        static string Info()
        {
            List<Entry> entries;
            lock (s_Lock) entries = s_Entries.ToList();
            return Json(w =>
            {
                w.WriteString("state", s_State);
                w.WriteBoolean("enabled", BurstCompiler.Options.EnableBurstCompilation);
                w.WriteBoolean("available", s_Lib != IntPtr.Zero);
                w.WriteString("dll", s_Dll ?? "");
                w.WriteNumber("seconds", Math.Round(s_Seconds, 2));
                w.WriteString("message", s_Message ?? "");
                w.WriteBoolean("compiler", FindVcVars() != null);
                w.WriteStartArray("jobs");
                foreach (Entry e in entries)
                {
                    w.WriteStartObject();
                    w.WriteString("job", BurstTypes.Display(e.JobType));
                    w.WriteString("kind", e.Kind);
                    w.WriteString("status", e.Status);
                    w.WriteBoolean("safetyChecks", e.Checks);
                    if (e.Reason != null) w.WriteString("reason", e.Reason);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }
    }
}
