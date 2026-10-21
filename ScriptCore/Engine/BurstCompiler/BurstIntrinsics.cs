using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace NovaEngine.Burst.Compiler
{
    // 번역하지 않고 C++ 로 바로 쓰는 호출: 수학 (CRT — .NET MathF 와 같은 UCRT 함수), Unsafe, Interlocked, 비트 연산,
    //  컨테이너 안전 검사의 throw (실패 표시 후 돌아가기), Debug.Log (문자열 글자만), Profiler 표시 (네이티브에서는 아무것도)
    internal static class BurstIntrinsics
    {
        static readonly Dictionary<string, string> s_Float = new Dictionary<string, string>
        {
            ["Abs"] = "fabsf", ["Sqrt"] = "sqrtf", ["Sin"] = "sinf", ["Cos"] = "cosf", ["Tan"] = "tanf", ["Asin"] = "asinf", ["Acos"] = "acosf", ["Atan"] = "atanf",
            ["Atan2"] = "atan2f", ["Sinh"] = "sinhf", ["Cosh"] = "coshf", ["Tanh"] = "tanhf", ["Asinh"] = "asinhf", ["Acosh"] = "acoshf", ["Atanh"] = "atanhf",
            ["Exp"] = "expf", ["Log2"] = "log2f", ["Log10"] = "log10f", ["Pow"] = "powf", ["Floor"] = "floorf", ["Ceiling"] = "ceilf", ["Truncate"] = "truncf",
            ["Min"] = "nb_minf", ["Max"] = "nb_maxf", ["CopySign"] = "copysignf", ["FusedMultiplyAdd"] = "fmaf", ["Cbrt"] = "cbrtf", ["Sign"] = "nb_signf",
            ["Clamp"] = "nb_clampf",
        };

        public static bool IsStringish(MethodBase m) => m.Name == "ToString" || m.DeclaringType == typeof(string) || m.DeclaringType == typeof(object);

        static string A(BurstMethodCompiler mc, ParameterInfo[] ps, string[] ex, int k, int off) => ex[k + off];

        public static bool TryCall(BurstMethodCompiler mc, BurstUnit u, MethodBase m, SItem[] items, string[] ex, out SItem result, out string code, out bool fails)
        {
            result = null; code = null; fails = false;
            Type dt = m.DeclaringType;
            if (dt == null) return false;
            string dn = dt.FullName ?? dt.Name;
            string n = m.Name;
            ParameterInfo[] ps = m.GetParameters();
            Type ret = m is MethodInfo mi ? mi.ReturnType : typeof(void);
            int off = m.IsStatic || m.IsConstructor ? 0 : 1;
            string Args() => string.Join(", ", Enumerable.Range(0, ps.Length).Select(k => "(" + u.Types.CType(ps[k].ParameterType) + ")(" + ex[k + off] + ")"));

            // ---- 수학
            if (dt == typeof(MathF) || dt == typeof(Math))
            {
                if (ps.Length == 0) return false;
                Type p0 = ps[0].ParameterType;
                if ((n == "Round" && ps.Length != 1) || (n == "Log" && ps.Length != 1)) return false;
                string fn = null;
                if (p0 == typeof(float))
                {
                    if (n == "Round") fn = "nearbyintf";
                    else if (n == "Log") fn = "logf";
                    else s_Float.TryGetValue(n, out fn);
                }
                else if (p0 == typeof(double))
                {
                    if (n == "Round") fn = "nearbyint";
                    else if (n == "Log") fn = "log";
                    else if (s_Float.TryGetValue(n, out string f)) fn = f.StartsWith("nb_") ? f.Substring(0, f.Length - 1) : f.Substring(0, f.Length - 1);
                    if (n == "Abs") fn = "fabs";
                }
                else if (p0.IsPrimitive)
                {
                    if (n == "Abs") fn = "nb_abs";
                    else if (n == "Min") fn = "nb_min";
                    else if (n == "Max") fn = "nb_max";
                    else if (n == "Clamp") fn = "nb_clamp";
                    else if (n == "Sign") fn = "nb_sign";
                }
                if (fn == null) return false;
                result = SItem.Of(ret);
                code = fn + "(" + Args() + ")";
                return true;
            }
            if (dt == typeof(BitConverter))
            {
                if (ps.Length != 1 || !ret.IsPrimitive) return false;
                if (n == "SingleToUInt32Bits" || n == "SingleToInt32Bits" || n == "UInt32BitsToSingle" || n == "Int32BitsToSingle" || n == "DoubleToInt64Bits" || n == "DoubleToUInt64Bits" || n == "Int64BitsToDouble" || n == "UInt64BitsToDouble")
                {
                    result = SItem.Of(ret);
                    code = "nb_bitcast<" + u.Types.CType(ret) + ">((" + u.Types.CType(ps[0].ParameterType) + ")(" + ex[0] + "))";
                    return true;
                }
                return false;
            }
            if (dn == "System.Numerics.BitOperations")
            {
                if (ps.Length == 0) return false;
                bool wide = ps[0].ParameterType == typeof(ulong) || ps[0].ParameterType == typeof(long);
                string fn = n == "LeadingZeroCount" ? "nb_lzcnt" : n == "TrailingZeroCount" ? "nb_tzcnt" : n == "PopCount" ? "nb_popcnt" : n == "Log2" ? "nb_log2i" : n == "RotateLeft" ? "nb_rotl" : n == "RotateRight" ? "nb_rotr" : n == "IsPow2" ? "nb_ispow2" : null;
                if (fn == null) return false;
                result = SItem.Of(ret);
                code = fn + (wide ? "64" : "32") + "(" + Args() + ")";
                return true;
            }

            // ---- Unsafe
            if (dt == typeof(Unsafe))
            {
                Type[] g = m.IsGenericMethod ? m.GetGenericArguments() : Type.EmptyTypes;
                string T0 = g.Length > 0 ? u.Types.CType(g[0]) : null;
                switch (n)
                {
                    case "SizeOf": result = SItem.I4; code = BurstTypes.SizeOf(g[0]).ToString(); return true;
                    case "AsRef":
                        if (ps[0].ParameterType.IsPointer || ps[0].ParameterType.IsByRef) { result = SItem.RefTo(g[0]); code = "(" + T0 + "*)(" + ex[0] + ")"; return true; }
                        return false;
                    case "AsPointer": result = SItem.PtrTo(typeof(void)); code = "(void*)(" + ex[0] + ")"; return true;
                    case "Read": result = SItem.Of(g[0]); code = "*(" + T0 + "*)(" + ex[0] + ")"; return true;
                    case "ReadUnaligned": if (!ps[0].ParameterType.IsPointer && !ps[0].ParameterType.IsByRef) return false; result = SItem.Of(g[0]); code = "nb_read_unaligned<" + T0 + ">((const void*)(" + ex[0] + "))"; return true;
                    case "Write": code = "*(" + T0 + "*)(" + ex[0] + ") = " + ex[1]; return true;
                    case "WriteUnaligned": code = "memcpy((void*)(" + ex[0] + "), &" + ex[1] + ", sizeof(" + T0 + "))"; return true;
                    case "As":
                        if (g.Length == 2) { result = SItem.RefTo(g[1]); code = "(" + u.Types.CType(g[1]) + "*)(" + ex[0] + ")"; return true; }
                        return false;
                    case "Add":
                        if (ps[0].ParameterType.IsPointer) { result = SItem.PtrTo(typeof(void)); code = "(void*)((uint8_t*)(" + ex[0] + ") + (intptr_t)(" + ex[1] + ") * " + BurstTypes.SizeOf(g[0]) + ")"; return true; }
                        result = SItem.RefTo(g[0]); code = "(" + T0 + "*)(" + ex[0] + ") + (intptr_t)(" + ex[1] + ")"; return true;
                    case "AddByteOffset": result = SItem.RefTo(g[0]); code = "(" + T0 + "*)((uint8_t*)(" + ex[0] + ") + (intptr_t)(" + ex[1] + "))"; return true;
                    case "ByteOffset": result = SItem.I; code = "(intptr_t)((uint8_t*)(" + ex[1] + ") - (uint8_t*)(" + ex[0] + "))"; return true;
                    case "AreSame": result = SItem.I4; code = "((void*)(" + ex[0] + ") == (void*)(" + ex[1] + ") ? 1 : 0)"; return true;
                    case "IsNullRef": result = SItem.I4; code = "((void*)(" + ex[0] + ") == nullptr ? 1 : 0)"; return true;
                    case "NullRef": result = SItem.RefTo(g[0]); code = "(" + T0 + "*)nullptr"; return true;
                    case "SkipInit": return true;
                    case "InitBlock": case "InitBlockUnaligned": code = "memset((void*)(" + ex[0] + "), (int)(" + ex[1] + "), (size_t)(" + ex[2] + "))"; return true;
                    case "CopyBlock": case "CopyBlockUnaligned": code = "memcpy((void*)(" + ex[0] + "), (const void*)(" + ex[1] + "), (size_t)(" + ex[2] + "))"; return true;
                }
                return false;
            }

            // ---- 스레드
            if (dt == typeof(System.Threading.Interlocked))
            {
                if (n == "MemoryBarrier") { code = "nb_barrier()"; return true; }
                if (ps.Length == 0 || !ps[0].ParameterType.IsByRef) return false;
                Type e = ps[0].ParameterType.GetElementType();
                string w = e == typeof(long) || e == typeof(ulong) ? "64" : e == typeof(int) || e == typeof(uint) ? "32" : null;
                if (w == null) return false;
                string fn = n == "Increment" ? "nb_inc" : n == "Decrement" ? "nb_dec" : n == "Add" ? "nb_add" : n == "Exchange" ? "nb_xchg" : n == "CompareExchange" ? "nb_cmpxchg" : n == "And" ? "nb_and" : n == "Or" ? "nb_or" : null;
                if (fn == null) return false;
                result = SItem.Of(ret);
                code = fn + w + "(" + string.Join(", ", ex.Select((x, k) => k == 0 ? "(void*)(" + x + ")" : "(int64_t)(" + x + ")")) + ")";
                return true;
            }
            if (dt == typeof(System.Threading.Volatile))
            {
                if (ps.Length == 0 || !ps[0].ParameterType.IsByRef) return false;
                Type e = ps[0].ParameterType.GetElementType();
                if (!e.IsPrimitive) return false;
                string ct = u.Types.CType(e);
                if (n == "Read") { result = SItem.Of(e); code = "*(volatile " + ct + "*)(" + ex[0] + ")"; return true; }
                if (n == "Write") { code = "*(volatile " + ct + "*)(" + ex[0] + ") = (" + ct + ")(" + ex[1] + ")"; return true; }
                return false;
            }
            if (dn == "System.Runtime.InteropServices.NativeMemory")
            {
                if (n == "Clear" && ps.Length == 2) { code = "memset((void*)(" + ex[0] + "), 0, (size_t)(" + ex[1] + "))"; return true; }
                if (n == "Fill" && ps.Length == 3) { code = "memset((void*)(" + ex[0] + "), (int)(" + ex[2] + "), (size_t)(" + ex[1] + "))"; return true; }
                if (n == "Copy" && ps.Length == 3) { code = "memmove((void*)(" + ex[1] + "), (const void*)(" + ex[0] + "), (size_t)(" + ex[2] + "))"; return true; }
                return false;
            }
            if (dt == typeof(Buffer) && n == "MemoryCopy" && ps.Length == 4 && ps[0].ParameterType.IsPointer)
            {
                code = "memmove((void*)(" + ex[1] + "), (const void*)(" + ex[0] + "), (size_t)(" + ex[3] + "))";
                return true;
            }
            if (dt == typeof(System.Threading.Thread) && n == "SpinWait") { code = "nb_spin((int32_t)(" + ex[0] + "))"; return true; }
            if (dt == typeof(Half) && n == "op_Explicit" && ps.Length == 1)
            {
                // Half ↔ float · double (소프트웨어 — AVX 에 F16C 가 없을 수 있다)
                if (ps[0].ParameterType == typeof(Half) && (ret == typeof(float) || ret == typeof(double))) { result = SItem.Of(ret); code = "(" + u.Types.CType(ret) + ")nb_half_to_float(nb_bitcast<uint16_t>(" + ex[0] + "))"; return true; }
                if (ret == typeof(Half) && (ps[0].ParameterType == typeof(float) || ps[0].ParameterType == typeof(double))) { result = SItem.Of(ret); code = "nb_bitcast<" + u.Types.CType(typeof(Half)) + ">(nb_float_to_half((float)(" + ex[0] + ")))"; return true; }
                return false;
            }

            // ---- 문자열 (오류 문구 · Debug.Log 에만 — 값은 만들지 않는다)
            if (dt == typeof(string) && (n == "Concat" || n == "Format" || n == "Join")) { result = SItem.Str(null); return true; }
            if (n == "ToString" && ret == typeof(string) && (dt.IsPrimitive || dt.IsEnum || dt.IsValueType)) { result = SItem.Str(null); return true; }
            if (dt == typeof(DefaultInterpolatedStringHandler))
            {
                if (n == "ToStringAndClear") result = SItem.Str(null);
                return true;   // 생성자 · AppendLiteral · AppendFormatted — 아무것도
            }

            // ---- throw 만 하는 System 도우미 ([DoesNotReturn])
            if (dt.Namespace != null && dt.Namespace.StartsWith("System", StringComparison.Ordinal) && n.StartsWith("Throw", StringComparison.Ordinal) && m.IsDefined(typeof(DoesNotReturnAttribute), false))
            {
                fails = true;
                code = "nb_fail(\"" + BurstTypes.Sanitize(dt.Name) + "." + n + "\"); " + mc.ReturnDefault;
                return true;
            }

            // ---- 엔진
            switch (dn)
            {
                case "NovaEngine.Collections.LowLevel.Unsafe.AtomicSafetyHandle":
                    if (n == "ThrowRead" || n == "ThrowWrite" || n == "ThrowDisposed")
                    {
                        fails = true;
                        string msg = n == "ThrowDisposed" ? "ObjectDisposedException: The native container has been deallocated" : n == "ThrowRead" ? "InvalidOperationException: The native container can not be read (write-only or deallocated)" : "InvalidOperationException: The native container can not be written (read-only or deallocated)";
                        code = "nb_fail(" + BurstMethodCompiler.CString(msg) + "); " + mc.ReturnDefault;
                        return true;
                    }
                    return false;
                case "NovaEngine.Collections.LowLevel.Unsafe.CollectionHelper":
                    if (n == "ThrowIndex") { fails = true; code = "nb_fail_index((int32_t)(" + ex[0] + "), (int32_t)(" + ex[1] + ")); " + mc.ReturnDefault; return true; }
                    if (n == "ThrowRestrictedIndex") { fails = true; code = "nb_fail_restricted((int32_t)(" + ex[0] + "), (int32_t)(" + ex[1] + "), (int32_t)(" + ex[2] + ")); " + mc.ReturnDefault; return true; }
                    if (n == "ThrowNotCreated") { fails = true; code = "nb_fail(\"NullReferenceException: The native container has not been created\"); " + mc.ReturnDefault; return true; }
                    return false;
                case "NovaEngine.Debug":
                    if ((n == "Log" || n == "LogWarning" || n == "LogError") && ps.Length >= 1)
                    {
                        SItem s = items[0];
                        string text = s.Kind == SK.Str && s.Text != null ? s.Text : "(message from a native Burst job)";
                        code = "nb_log(" + (n == "Log" ? 0 : n == "LogWarning" ? 1 : 2) + ", " + BurstMethodCompiler.CString(text) + ")";
                        return true;
                    }
                    if (n == "Assert") { code = null; return true; }
                    return false;
                case "NovaEngine.Profiling.ProfilerMarker":
                    if (n == "Begin" || n == "End") return true;
                    if (n == "Auto") { result = SItem.Of(ret); code = u.Types.CType(ret) + "{}"; return true; }
                    return false;
                case "NovaEngine.Profiling.ProfilerMarker+AutoScope":
                    return n == "Dispose";
                case "NovaEngine.Profiling.Profiler":
                    return n == "BeginSample" || n == "EndSample";
                case "NovaEngine.Jobs.LowLevel.Unsafe.JobsUtility":
                    if (n == "get_ThreadIndex") { result = SItem.I4; code = "nb_thread_index()"; return true; }
                    if (n == "get_IsExecutingJob") { result = SItem.I4; code = "1"; return true; }
                    return false;
                case "NovaEngine.Burst.CompilerServices.Hint":
                    if (n == "Likely" || n == "Unlikely") { result = SItem.I4; code = "(" + ex[0] + ")"; return true; }
                    if (n == "Assume") { code = "__assume(" + ex[0] + ")"; return true; }
                    return false;
                case "NovaEngine.Burst.CompilerServices.Loop":
                    return true;
            }
            return false;
        }
    }
}
