using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace NovaEngine.Burst.Compiler
{
    // IL 평가 스택의 칸: 정수 · 실수 · 포인터 · 참조 · struct 값, 그리고 코드가 없는 기호 (null · 문자열 · 예외 — 오류 문구에만)
    internal enum SK { I4, I8, I, R4, R8, Ptr, Ref, Val, Null, Str, Exc, Obj }

    internal sealed class SItem
    {
        public SK Kind;
        public Type Type;      // Ptr · Ref 의 대상, Val 의 struct, Exc 의 예외 형식
        public string Text;    // Str 글자 · Exc 문구 (모르면 null)
        public long? Const;    // 번역 때 아는 정수 (ldc · 정적 readonly — CollectionChecks.Enabled 분기를 접는다)
        public static SItem I4Const(long v) => new SItem { Kind = SK.I4, Const = v };

        public static readonly SItem I4 = new SItem { Kind = SK.I4 }, I8 = new SItem { Kind = SK.I8 }, I = new SItem { Kind = SK.I },
            R4 = new SItem { Kind = SK.R4 }, R8 = new SItem { Kind = SK.R8 }, Null = new SItem { Kind = SK.Null }, Obj = new SItem { Kind = SK.Obj };
        public static SItem PtrTo(Type t) => new SItem { Kind = SK.Ptr, Type = t };
        public static SItem RefTo(Type t) => new SItem { Kind = SK.Ref, Type = t };
        public static SItem Val(Type t) => new SItem { Kind = SK.Val, Type = t };
        public static SItem Str(string s) => new SItem { Kind = SK.Str, Text = s };

        public bool Symbolic => Kind == SK.Null || Kind == SK.Str || Kind == SK.Exc || Kind == SK.Obj;
        public bool IsAddress => Kind == SK.Ptr || Kind == SK.Ref;
        public bool IsInt => Kind == SK.I4 || Kind == SK.I8 || Kind == SK.I;
        public bool IsFloat => Kind == SK.R4 || Kind == SK.R8;

        public bool Same(SItem o)
        {
            if (Kind != o.Kind) return false;
            if (Kind == SK.Ptr || Kind == SK.Ref || Kind == SK.Val) return Type == o.Type;
            return true;
        }

        public static SItem Of(Type t)
        {
            if (t.IsByRef) return RefTo(t.GetElementType());
            if (t.IsPointer) return PtrTo(t.GetElementType());
            if (t.IsEnum) t = Enum.GetUnderlyingType(t);
            if (t == typeof(bool) || t == typeof(sbyte) || t == typeof(byte) || t == typeof(short) || t == typeof(ushort) || t == typeof(char) || t == typeof(int) || t == typeof(uint)) return I4;
            if (t == typeof(long) || t == typeof(ulong)) return I8;
            if (t == typeof(IntPtr) || t == typeof(UIntPtr)) return I;
            if (t == typeof(float)) return R4;
            if (t == typeof(double)) return R8;
            if (t.IsValueType) return Val(t);
            if (t == typeof(string)) return Str(null);
            throw new BurstUnsupportedException("the managed type " + BurstTypes.Display(t));
        }
    }

    internal sealed class BurstMethodCompiler
    {
        readonly BurstUnit U;
        public readonly MethodBase Method;
        public readonly string CName;
        public bool MayFail;
        public readonly HashSet<BurstMethodCompiler> Callees = new HashSet<BurstMethodCompiler>();
        public string Prototype, Body;
        public bool Inline;
        public bool ForceInline;

        List<ILInstruction> m_Code;
        Dictionary<int, int> m_IndexOf;
        SItem[][] m_Entry;
        Type[] m_Args;
        Type[] m_Locals;
        Type m_Ret;
        readonly List<ExceptionHandlingClause> m_Finally = new List<ExceptionHandlingClause>();
        readonly Dictionary<int, List<int>> m_FinallyTargets = new Dictionary<int, List<int>>();
        readonly Dictionary<string, string> m_Temps = new Dictionary<string, string>();
        readonly HashSet<int> m_Labels = new HashSet<int>();
        StringBuilder m_Out;
        bool m_Emit;

        public BurstMethodCompiler(BurstUnit unit, MethodBase method, string cname)
        {
            U = unit; Method = method; CName = cname;
        }

        public string RetType => m_Ret == typeof(void) ? "void" : U.Types.CType(m_Ret);
        public string ReturnDefault => m_Ret == typeof(void) ? "return;" : "return {};";

        public void Compile()
        {
            m_Code = ILReader.Read(Method);
            m_IndexOf = new Dictionary<int, int>();
            for (int i = 0; i < m_Code.Count; i++) m_IndexOf[m_Code[i].Offset] = i;
            MethodBody body = Method.GetMethodBody();
            ParameterInfo[] ps = Method.GetParameters();
            var args = new List<Type>();
            if (!Method.IsStatic)
            {
                if (!Method.DeclaringType.IsValueType) throw new BurstUnsupportedException("instance method on a class: " + ILReader.Name(Method));
                args.Add(Method.DeclaringType.MakeByRefType());
            }
            foreach (ParameterInfo p in ps) args.Add(p.ParameterType);
            m_Args = args.ToArray();
            m_Locals = body.LocalVariables.OrderBy(l => l.LocalIndex).Select(l => l.LocalType).ToArray();
            m_Ret = Method is MethodInfo mi ? mi.ReturnType : typeof(void);
            Inline = ForceInline || (Method.MethodImplementationFlags & MethodImplAttributes.AggressiveInlining) != 0 || m_Code.Count <= 24;
            foreach (ExceptionHandlingClause c in body.ExceptionHandlingClauses)
            {
                if (c.Flags != ExceptionHandlingClauseOptions.Finally)
                    throw new BurstUnsupportedException("try/catch in " + ILReader.Name(Method) + " (try/finally is supported)");
                m_Finally.Add(c);
            }

            FindThrowBlocks();

            // 1) 스택 형식 (모든 경로)
            m_Entry = new SItem[m_Code.Count][];
            var work = new Stack<int>();
            Seed(0, new SItem[0], work);
            foreach (ExceptionHandlingClause c in m_Finally) Seed(Index(c.HandlerOffset), new SItem[0], work);
            m_Emit = false;
            while (work.Count > 0)
            {
                int i = work.Pop();
                var stack = new List<SItem>(m_Entry[i]);
                foreach (int next in Step(i, stack)) Seed(next, stack.ToArray(), work);
            }

            // 2) 코드
            m_Emit = true;
            m_Out = new StringBuilder();
            for (int i = 0; i < m_Code.Count; i++)
            {
                if (m_Entry[i] == null) continue;   // 닿지 않는 코드
                if (m_Labels.Contains(m_Code[i].Offset)) m_Out.Append("L_").Append(m_Code[i].Offset).Append(":;\n");
                var stack = new List<SItem>(m_Entry[i]);
                Step(i, stack);
            }

            var sig = new StringBuilder();
            for (int a = 0; a < m_Args.Length; a++)
            {
                if (a > 0) sig.Append(", ");
                sig.Append(U.Types.CType(m_Args[a])).Append(" a").Append(a);
            }
            Prototype = "static " + (Inline ? "__forceinline " : "") + RetType + " " + CName + "(" + sig + ")";
            var b = new StringBuilder();
            b.Append("// ").Append(ILReader.Name(Method)).Append('\n');
            b.Append(Prototype).Append("\n{\n");
            for (int l = 0; l < m_Locals.Length; l++)
            {
                Type lt = m_Locals[l];
                b.Append("    ").Append(U.Types.CType(lt.IsByRef ? lt : lt)).Append(" l").Append(l).Append("{};\n");
            }
            foreach (var kv in m_Temps.OrderBy(k => k.Key, StringComparer.Ordinal)) b.Append("    ").Append(kv.Value).Append(' ').Append(kv.Key).Append("{};\n");
            for (int k = 0; k < m_Finally.Count; k++) b.Append("    int32_t fc").Append(k).Append(" = 0;\n");
            b.Append(m_Out);
            b.Append("    ").Append(ReturnDefault).Append("\n}\n");
            Body = b.ToString();
        }

        // throw 로 끝나는 기본 블록 (예외 문구 만들기 — 문자열 잇기 · typeof · 배열): 통째로 "실패 표시 후 돌아가기" 로 (Burst 도 문구만 남긴다)
        int[] m_ThrowBlockStart;   // 명령 → 그 throw 블록의 첫 명령 (-1 = 아님)
        string[] m_ThrowMessage;

        void FindThrowBlocks()
        {
            int n = m_Code.Count;
            var leader = new bool[n];
            if (n > 0) leader[0] = true;
            for (int i = 0; i < n; i++)
            {
                ILInstruction ins = m_Code[i];
                FlowControl fc = ins.OpCode.FlowControl;
                if (ins.Target >= 0 && m_IndexOf.TryGetValue(ins.Target, out int t)) leader[t] = true;
                if (ins.Targets != null) foreach (int o in ins.Targets) if (m_IndexOf.TryGetValue(o, out int t2)) leader[t2] = true;
                if ((fc == FlowControl.Branch || fc == FlowControl.Cond_Branch || fc == FlowControl.Return || fc == FlowControl.Throw || ins.OpCode == OpCodes.Leave || ins.OpCode == OpCodes.Leave_S || ins.OpCode == OpCodes.Endfinally) && i + 1 < n) leader[i + 1] = true;
            }
            foreach (ExceptionHandlingClause c in m_Finally)
            {
                if (m_IndexOf.TryGetValue(c.HandlerOffset, out int h)) leader[h] = true;
                if (m_IndexOf.TryGetValue(c.TryOffset, out int tr)) leader[tr] = true;
            }
            // 기본 블록 (시작 · 끝 · 다음 블록)
            var starts = new List<int>();
            for (int i = 0; i < n; i++) if (leader[i]) starts.Add(i);
            int bc = starts.Count;
            var blockOf = new int[n];
            var ends = new int[bc];
            for (int k = 0; k < bc; k++)
            {
                ends[k] = k + 1 < bc ? starts[k + 1] - 1 : n - 1;
                for (int i = starts[k]; i <= ends[k]; i++) blockOf[i] = k;
            }
            var succ = new List<int>[bc];
            var allowed = new bool[bc];
            var marked = new bool[bc];
            for (int k = 0; k < bc; k++)
            {
                succ[k] = new List<int>();
                ILInstruction last = m_Code[ends[k]];
                FlowControl fc = last.OpCode.FlowControl;
                if (last.Target >= 0 && m_IndexOf.TryGetValue(last.Target, out int t) && last.OpCode != OpCodes.Leave && last.OpCode != OpCodes.Leave_S) succ[k].Add(blockOf[t]);
                if (last.Targets != null) foreach (int o in last.Targets) if (m_IndexOf.TryGetValue(o, out int t2)) succ[k].Add(blockOf[t2]);
                bool falls = fc != FlowControl.Branch && fc != FlowControl.Return && fc != FlowControl.Throw && last.OpCode != OpCodes.Leave && last.OpCode != OpCodes.Leave_S && last.OpCode != OpCodes.Endfinally;
                if (falls && ends[k] + 1 < n) succ[k].Add(blockOf[ends[k] + 1]);
                // throw 로 가는 블록은 무엇을 쓰든 (문구 만들기의 임시 지역 등) 실패 표시 후 돌아간다 — 나가는 흐름만 없으면
                bool ok = last.OpCode != OpCodes.Leave && last.OpCode != OpCodes.Leave_S && last.OpCode != OpCodes.Endfinally && last.OpCode != OpCodes.Ret;
                allowed[k] = ok;
                marked[k] = last.OpCode == OpCodes.Throw && ok;
            }
            // 모든 다음 블록이 throw 쪽이면 그 블록도 (삼항 연산 · 문자열 잇기가 블록을 나눈다)
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int k = 0; k < bc; k++)
                    if (!marked[k] && allowed[k] && succ[k].Count > 0 && succ[k].All(x => marked[x]) && !succ[k].Contains(k)) { marked[k] = changed = true; }
            }
            m_ThrowBlockStart = Enumerable.Repeat(-1, n).ToArray();
            m_ThrowMessage = new string[n];
            for (int k = 0; k < bc; k++)
            {
                if (!marked[k]) continue;
                // 문구: 이 블록에서 throw 까지 첫 경로의 예외 형식 + 첫 글자
                string type = "Exception", literal = null;
                for (int b = k, guard = 0; b >= 0 && guard < 64; guard++)
                {
                    for (int i = starts[b]; i <= ends[b]; i++)
                    {
                        if (m_Code[i].OpCode == OpCodes.Newobj && m_Code[i].Operand is ConstructorInfo ci && typeof(Exception).IsAssignableFrom(ci.DeclaringType)) type = BurstTypes.Display(ci.DeclaringType);
                        if (literal == null && m_Code[i].OpCode == OpCodes.Ldstr) literal = (string)m_Code[i].Operand;
                    }
                    if (m_Code[ends[b]].OpCode == OpCodes.Throw) break;
                    b = succ[b].Count > 0 ? succ[b][0] : -1;
                }
                for (int i = starts[k]; i <= ends[k]; i++) m_ThrowBlockStart[i] = starts[k];
                m_ThrowMessage[starts[k]] = type + (literal != null ? ": " + literal.Trim() + "…" : "");
            }
        }

        int Index(int offset)
        {
            if (!m_IndexOf.TryGetValue(offset, out int i)) throw new BurstUnsupportedException("branch into the middle of an instruction in " + ILReader.Name(Method));
            return i;
        }

        void Seed(int index, SItem[] stack, Stack<int> work)
        {
            if (index < 0 || index >= m_Code.Count) return;
            SItem[] cur = m_Entry[index];
            if (cur == null) { m_Entry[index] = stack; work.Push(index); return; }
            if (cur.Length != stack.Length) throw new BurstUnsupportedException("stack depth mismatch at IL_" + m_Code[index].Offset.ToString("X4") + " in " + ILReader.Name(Method));
            for (int k = 0; k < cur.Length; k++)
            {
                if (cur[k].Same(stack[k]) && cur[k].Const != null && cur[k].Const != stack[k].Const)
                {
                    cur[k] = new SItem { Kind = cur[k].Kind, Type = cur[k].Type, Text = cur[k].Text };   // 경로마다 다른 값 → 모른다
                    work.Push(index);
                }
                if (!cur[k].Same(stack[k]))
                {
                    if (cur[k].Kind == SK.Null && stack[k].IsAddress) { cur[k] = stack[k]; work.Push(index); continue; }
                    if (stack[k].Kind == SK.Null && cur[k].IsAddress) continue;
                    throw new BurstUnsupportedException("stack type mismatch at IL_" + m_Code[index].Offset.ToString("X4") + " in " + ILReader.Name(Method) + " (" + cur[k].Kind + " vs " + stack[k].Kind + ")");
                }
            }
        }

        // ---------------------------------------------------------------- 이름 · 식
        string Tag(SItem s)
        {
            switch (s.Kind)
            {
                case SK.I4: return "i4";
                case SK.I8: return "i8";
                case SK.I: return "ip";
                case SK.R4: return "r4";
                case SK.R8: return "r8";
                case SK.Ptr: return "pt" + U.TypeId(s.Type);
                case SK.Ref: return "rf" + U.TypeId(s.Type);
                default: return "vl" + U.TypeId(s.Type);
            }
        }
        string CTypeOf(SItem s)
        {
            switch (s.Kind)
            {
                case SK.I4: return "int32_t";
                case SK.I8: return "int64_t";
                case SK.I: return "intptr_t";
                case SK.R4: return "float";
                case SK.R8: return "double";
                case SK.Ptr: case SK.Ref: return (s.Type == typeof(void) ? "void" : U.Types.CType(s.Type.IsByRef || s.Type.IsPointer ? s.Type : s.Type)) + "*";
                default: return U.Types.CType(s.Type);
            }
        }
        string Slot(int depth, SItem s)
        {
            string n = "s" + depth + "_" + Tag(s);
            if (m_Emit && !m_Temps.ContainsKey(n)) m_Temps[n] = CTypeOf(s);
            return n;
        }
        // 값을 쓰는 곳의 식 (기호는 글자로)
        string Expr(int depth, SItem s)
        {
            if (s.Kind == SK.Null) return "nullptr";
            if (s.Symbolic) throw new BurstUnsupportedException(KindName(s) + " used as a value in " + ILReader.Name(Method) + " (only in Debug.Log and exception messages)");
            return Slot(depth, s);
        }
        static string KindName(SItem s) => s.Kind == SK.Str ? "a string" : s.Kind == SK.Exc ? "an exception object" : s.Kind == SK.Obj ? "a boxed value" : "null";

        void Line(string code) { if (m_Emit) m_Out.Append("    ").Append(code).Append('\n'); }

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nb_nanf()";
            if (float.IsPositiveInfinity(v)) return "nb_inff()";
            if (float.IsNegativeInfinity(v)) return "(-nb_inff())";
            return "nb_bitcast<float>((uint32_t)0x" + BitConverter.SingleToUInt32Bits(v).ToString("X8") + "u) /*" + v.ToString("R", CultureInfo.InvariantCulture) + "*/";
        }
        static string D(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "nb_bitcast<double>((uint64_t)0x" + BitConverter.DoubleToUInt64Bits(v).ToString("X16") + "ull)";
            return "nb_bitcast<double>((uint64_t)0x" + BitConverter.DoubleToUInt64Bits(v).ToString("X16") + "ull) /*" + v.ToString("R", CultureInfo.InvariantCulture) + "*/";
        }

        // ---------------------------------------------------------------- 한 명령 (형식 추적 · 코드)
        IEnumerable<int> Step(int index, List<SItem> st)
        {
            ILInstruction ins = m_Code[index];
            OpCode op = ins.OpCode;
            short v = op.Value;
            int next = index + 1;
            if (m_ThrowBlockStart[index] >= 0)
            {
                if (m_ThrowBlockStart[index] == index)
                {
                    MayFail = true;
                    Line("nb_fail(" + CString(m_ThrowMessage[index]) + "); " + ReturnDefault);
                }
                return new int[0];
            }

            // 꺼내기 · 넣기 (식은 넣기 전에 만든다)
            SItem Pop() { SItem s = st[st.Count - 1]; st.RemoveAt(st.Count - 1); return s; }
            string PopE(out SItem s) { s = st[st.Count - 1]; string e = Expr(st.Count - 1, s); st.RemoveAt(st.Count - 1); return e; }
            string Push(SItem s) { st.Add(s); return s.Symbolic ? null : Slot(st.Count - 1, s); }

            if (op == OpCodes.Nop || op == OpCodes.Break) return new[] { next };

            // 인자 · 지역
            if (op == OpCodes.Ldarg_0 || op == OpCodes.Ldarg_1 || op == OpCodes.Ldarg_2 || op == OpCodes.Ldarg_3 || op == OpCodes.Ldarg || op == OpCodes.Ldarg_S)
            {
                int a = op == OpCodes.Ldarg_0 ? 0 : op == OpCodes.Ldarg_1 ? 1 : op == OpCodes.Ldarg_2 ? 2 : op == OpCodes.Ldarg_3 ? 3 : (int)ins.Operand;
                string dst = Push(SItem.Of(m_Args[a]));
                if (dst != null) Line(dst + " = a" + a + ";");
                return new[] { next };
            }
            if (op == OpCodes.Ldarga || op == OpCodes.Ldarga_S)
            {
                int a = (int)ins.Operand;
                string dst = Push(SItem.RefTo(m_Args[a]));
                Line(dst + " = &a" + a + ";");
                return new[] { next };
            }
            if (op == OpCodes.Starg || op == OpCodes.Starg_S)
            {
                int a = (int)ins.Operand;
                string e = PopE(out _);
                Line("a" + a + " = " + Cast(m_Args[a], e) + ";");
                return new[] { next };
            }
            if (op == OpCodes.Ldloc_0 || op == OpCodes.Ldloc_1 || op == OpCodes.Ldloc_2 || op == OpCodes.Ldloc_3 || op == OpCodes.Ldloc || op == OpCodes.Ldloc_S)
            {
                int l = op == OpCodes.Ldloc_0 ? 0 : op == OpCodes.Ldloc_1 ? 1 : op == OpCodes.Ldloc_2 ? 2 : op == OpCodes.Ldloc_3 ? 3 : (int)ins.Operand;
                string dst = Push(SItem.Of(m_Locals[l]));
                if (dst != null) Line(dst + " = l" + l + ";");
                return new[] { next };
            }
            if (op == OpCodes.Ldloca || op == OpCodes.Ldloca_S)
            {
                int l = (int)ins.Operand;
                Type lt = m_Locals[l];
                string dst = Push(SItem.RefTo(lt));
                Line(dst + " = &l" + l + ";");
                return new[] { next };
            }
            if (op == OpCodes.Stloc_0 || op == OpCodes.Stloc_1 || op == OpCodes.Stloc_2 || op == OpCodes.Stloc_3 || op == OpCodes.Stloc || op == OpCodes.Stloc_S)
            {
                int l = op == OpCodes.Stloc_0 ? 0 : op == OpCodes.Stloc_1 ? 1 : op == OpCodes.Stloc_2 ? 2 : op == OpCodes.Stloc_3 ? 3 : (int)ins.Operand;
                string e = PopE(out _);
                Line("l" + l + " = " + Cast(m_Locals[l], e) + ";");
                return new[] { next };
            }

            // 상수
            if (op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_S || (v >= OpCodes.Ldc_I4_M1.Value && v <= OpCodes.Ldc_I4_8.Value))
            {
                int c = op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_S ? (int)ins.Operand : v - OpCodes.Ldc_I4_0.Value;
                string dst = Push(SItem.I4Const(c));
                Line(dst + " = " + (c == int.MinValue ? "(int32_t)0x80000000" : c.ToString(CultureInfo.InvariantCulture)) + ";");
                return new[] { next };
            }
            if (op == OpCodes.Ldc_I8) { long c = (long)ins.Operand; string dst = Push(SItem.I8); Line(dst + " = (int64_t)0x" + c.ToString("X16") + "ull;"); return new[] { next }; }
            if (op == OpCodes.Ldc_R4) { string dst = Push(SItem.R4); Line(dst + " = " + F((float)ins.Operand) + ";"); return new[] { next }; }
            if (op == OpCodes.Ldc_R8) { string dst = Push(SItem.R8); Line(dst + " = " + D((double)ins.Operand) + ";"); return new[] { next }; }
            if (op == OpCodes.Ldnull) { Push(SItem.Null); return new[] { next }; }
            if (op == OpCodes.Ldstr) { Push(SItem.Str((string)ins.Operand)); return new[] { next }; }

            if (op == OpCodes.Dup)
            {
                SItem s = st[st.Count - 1];
                string src = s.Symbolic ? null : Slot(st.Count - 1, s);
                string dst = Push(s);
                if (dst != null) Line(dst + " = " + src + ";");
                return new[] { next };
            }
            if (op == OpCodes.Pop) { Pop(); return new[] { next }; }

            // 산술 · 논리
            if (op == OpCodes.Add || op == OpCodes.Sub || op == OpCodes.Mul || op == OpCodes.Div || op == OpCodes.Div_Un || op == OpCodes.Rem || op == OpCodes.Rem_Un
                || op == OpCodes.And || op == OpCodes.Or || op == OpCodes.Xor || op == OpCodes.Add_Ovf || op == OpCodes.Add_Ovf_Un || op == OpCodes.Sub_Ovf || op == OpCodes.Sub_Ovf_Un || op == OpCodes.Mul_Ovf || op == OpCodes.Mul_Ovf_Un)
            {
                string eb = PopE(out SItem b), ea = PopE(out SItem a);
                Binary(op, a, ea, b, eb, st);
                return new[] { next };
            }
            if (op == OpCodes.Shl || op == OpCodes.Shr || op == OpCodes.Shr_Un)
            {
                string eb = PopE(out SItem b), ea = PopE(out SItem a);
                if (!a.IsInt) throw Unsupported("shift of " + a.Kind);
                string mask = a.Kind == SK.I4 ? "31" : "63";
                string ut = a.Kind == SK.I4 ? "uint32_t" : a.Kind == SK.I8 ? "uint64_t" : "uintptr_t", stt = a.Kind == SK.I4 ? "int32_t" : a.Kind == SK.I8 ? "int64_t" : "intptr_t";
                string dst = Push(a.Kind == SK.I4 ? SItem.I4 : a.Kind == SK.I8 ? SItem.I8 : SItem.I);
                if (op == OpCodes.Shl) Line(dst + " = (" + stt + ")((" + ut + ")" + ea + " << ((int32_t)" + eb + " & " + mask + "));");
                else if (op == OpCodes.Shr) Line(dst + " = (" + stt + ")(" + ea + " >> ((int32_t)" + eb + " & " + mask + "));");
                else Line(dst + " = (" + stt + ")((" + ut + ")" + ea + " >> ((int32_t)" + eb + " & " + mask + "));");
                return new[] { next };
            }
            if (op == OpCodes.Neg)
            {
                string ea = PopE(out SItem a);
                string dst = Push(a.Kind == SK.R4 ? SItem.R4 : a.Kind == SK.R8 ? SItem.R8 : a.Kind == SK.I8 ? SItem.I8 : a.Kind == SK.I ? SItem.I : SItem.I4);
                if (a.IsFloat) Line(dst + " = -" + ea + ";");
                else Line(dst + " = (" + CTypeOf(st[st.Count - 1]) + ")(0 - (" + (a.Kind == SK.I4 ? "uint32_t" : a.Kind == SK.I8 ? "uint64_t" : "uintptr_t") + ")" + ea + ");");
                return new[] { next };
            }
            if (op == OpCodes.Not)
            {
                string ea = PopE(out SItem a);
                string dst = Push(a.Kind == SK.I8 ? SItem.I8 : a.Kind == SK.I ? SItem.I : SItem.I4);
                Line(dst + " = ~" + ea + ";");
                return new[] { next };
            }

            // 비교
            if (op == OpCodes.Ceq || op == OpCodes.Cgt || op == OpCodes.Cgt_Un || op == OpCodes.Clt || op == OpCodes.Clt_Un)
            {
                string eb = PopE(out SItem b), ea = PopE(out SItem a);
                string cond = Compare(op == OpCodes.Ceq ? "eq" : op == OpCodes.Cgt ? "gt" : op == OpCodes.Cgt_Un ? "gt.un" : op == OpCodes.Clt ? "lt" : "lt.un", a, ea, b, eb);
                string dst = Push(SItem.I4);
                Line(dst + " = (" + cond + ") ? 1 : 0;");
                return new[] { next };
            }

            // 변환
            if (op.Name.StartsWith("conv.", StringComparison.Ordinal))
            {
                string ea = PopE(out SItem a);
                string name = op.Name.Replace(".ovf", "").Replace(".un", "_un");
                Convert(name, a, ea, st, op.Name.Contains(".un"));
                return new[] { next };
            }

            // 분기
            if (op == OpCodes.Br || op == OpCodes.Br_S)
            {
                Label(ins.Target);
                Line("goto L_" + ins.Target + ";");
                return new[] { Index(ins.Target) };
            }
            if (op == OpCodes.Brtrue || op == OpCodes.Brtrue_S || op == OpCodes.Brfalse || op == OpCodes.Brfalse_S)
            {
                string ea = PopE(out SItem a);
                bool t = op == OpCodes.Brtrue || op == OpCodes.Brtrue_S;
                if (a.Const != null || a.Kind == SK.Null)
                {
                    bool taken = (a.Const ?? 0) != 0 == t;   // 죽은 쪽은 번역하지 않는다 (그 안의 검사 · throw 도)
                    if (!taken) return new[] { next };
                    Label(ins.Target);
                    Line("goto L_" + ins.Target + ";");
                    return new[] { Index(ins.Target) };
                }
                Label(ins.Target);
                string test = a.Kind == SK.Null ? "0" : ea;
                Line("if (" + (t ? "" : "!") + "(" + test + ")) goto L_" + ins.Target + ";");
                return new[] { next, Index(ins.Target) };
            }
            if (op.FlowControl == FlowControl.Cond_Branch && op.OperandType != OperandType.InlineSwitch)
            {
                string eb = PopE(out SItem b), ea = PopE(out SItem a);
                string n = op.Name.Replace(".s", "");
                string kind = n == "beq" ? "eq" : n == "bne.un" ? "ne.un" : n == "bge" ? "ge" : n == "bge.un" ? "ge.un" : n == "bgt" ? "gt" : n == "bgt.un" ? "gt.un" : n == "ble" ? "le" : n == "ble.un" ? "le.un" : n == "blt" ? "lt" : "lt.un";
                Label(ins.Target);
                Line("if (" + Compare(kind, a, ea, b, eb) + ") goto L_" + ins.Target + ";");
                return new[] { next, Index(ins.Target) };
            }
            if (op == OpCodes.Switch)
            {
                string ea = PopE(out _);
                var succ = new List<int> { next };
                if (m_Emit)
                {
                    m_Out.Append("    switch (").Append(ea).Append(") {");
                    for (int k = 0; k < ins.Targets.Length; k++) m_Out.Append(" case ").Append(k).Append(": goto L_").Append(ins.Targets[k]).Append(';');
                    m_Out.Append(" default: break; }\n");
                }
                foreach (int t in ins.Targets) { Label(t); succ.Add(Index(t)); }
                return succ;
            }
            if (op == OpCodes.Ret)
            {
                if (m_Ret != typeof(void)) { string e = PopE(out _); Line("return " + Cast(m_Ret, e) + ";"); }
                else Line("return;");
                return new int[0];
            }
            if (op == OpCodes.Throw)
            {
                SItem e = Pop();
                string msg = e.Kind == SK.Exc ? e.Text : "Exception";
                MayFail = true;
                Line("nb_fail(" + CString(msg ?? "Exception") + "); " + ReturnDefault);
                return new int[0];
            }
            if (op == OpCodes.Leave || op == OpCodes.Leave_S)
            {
                st.Clear();
                int target = ins.Target;
                var exited = new List<int>();
                for (int k = 0; k < m_Finally.Count; k++)
                {
                    ExceptionHandlingClause c = m_Finally[k];
                    bool inTry = ins.Offset >= c.TryOffset && ins.Offset < c.TryOffset + c.TryLength;
                    bool targetIn = target >= c.TryOffset && target < c.TryOffset + c.TryLength;
                    if (inTry && !targetIn) exited.Add(k);
                }
                Label(target);
                if (exited.Count == 0) { Line("goto L_" + target + ";"); return new[] { Index(target) }; }
                if (exited.Count > 1) throw Unsupported("leaving two nested try/finally blocks at once");
                int clause = exited[0];
                if (!m_FinallyTargets.TryGetValue(clause, out List<int> targets)) m_FinallyTargets[clause] = targets = new List<int>();
                int id = targets.IndexOf(target);
                if (id < 0) { targets.Add(target); id = targets.Count - 1; }
                Label(m_Finally[clause].HandlerOffset);
                Line("fc" + clause + " = " + id + "; goto L_" + m_Finally[clause].HandlerOffset + ";");
                return new[] { Index(target) };
            }
            if (op == OpCodes.Endfinally)
            {
                int clause = -1;
                for (int k = 0; k < m_Finally.Count; k++)
                    if (ins.Offset >= m_Finally[k].HandlerOffset && ins.Offset < m_Finally[k].HandlerOffset + m_Finally[k].HandlerLength) clause = k;
                if (clause < 0) throw Unsupported("endfinally outside a finally block");
                if (m_Emit)
                {
                    m_Out.Append("    switch (fc").Append(clause).Append(") {");
                    if (m_FinallyTargets.TryGetValue(clause, out List<int> targets))
                        for (int k = 0; k < targets.Count; k++) m_Out.Append(" case ").Append(k).Append(": goto L_").Append(targets[k]).Append(';');
                    m_Out.Append(" default: ").Append(ReturnDefault).Append(" }\n");
                }
                return new int[0];
            }

            // 메모리
            if (op.Name.StartsWith("ldind.", StringComparison.Ordinal))
            {
                string ea = PopE(out SItem a);
                Type t = IndType(op.Name.Substring(6), a);
                string dst = Push(SItem.Of(t));
                Line(dst + " = *(" + U.Types.CType(t) + "*)(" + ea + ");");
                return new[] { next };
            }
            if (op.Name.StartsWith("stind.", StringComparison.Ordinal))
            {
                string ev = PopE(out SItem val), ea = PopE(out SItem a);
                Type t = IndType(op.Name.Substring(6), a);
                Line("*(" + U.Types.CType(t) + "*)(" + ea + ") = " + Cast(t, ev) + ";");
                return new[] { next };
            }
            if (op == OpCodes.Ldobj)
            {
                Type t = (Type)ins.Operand;
                string ea = PopE(out _);
                string dst = Push(SItem.Of(t));
                Line(dst + " = *(" + U.Types.CType(t) + "*)(" + ea + ");");
                return new[] { next };
            }
            if (op == OpCodes.Stobj)
            {
                Type t = (Type)ins.Operand;
                string ev = PopE(out _), ea = PopE(out _);
                Line("*(" + U.Types.CType(t) + "*)(" + ea + ") = " + Cast(t, ev) + ";");
                return new[] { next };
            }
            if (op == OpCodes.Initobj)
            {
                Type t = (Type)ins.Operand;
                string ea = PopE(out _);
                Line("*(" + U.Types.CType(t) + "*)(" + ea + ") = {};");
                return new[] { next };
            }
            if (op == OpCodes.Cpobj)
            {
                Type t = (Type)ins.Operand;
                string es = PopE(out _), ed = PopE(out _);
                Line("*(" + U.Types.CType(t) + "*)(" + ed + ") = *(" + U.Types.CType(t) + "*)(" + es + ");");
                return new[] { next };
            }
            if (op == OpCodes.Initblk) { string n = PopE(out _), val = PopE(out _), a = PopE(out _); Line("memset((void*)(" + a + "), (int)" + val + ", (size_t)" + n + ");"); return new[] { next }; }
            if (op == OpCodes.Cpblk) { string n = PopE(out _), s = PopE(out _), a = PopE(out _); Line("memcpy((void*)(" + a + "), (const void*)(" + s + "), (size_t)" + n + ");"); return new[] { next }; }
            if (op == OpCodes.Localloc) { string n = PopE(out _); string dst = Push(SItem.PtrTo(typeof(void))); Line(dst + " = _alloca((size_t)" + n + "); memset(" + dst + ", 0, (size_t)" + n + ");"); return new[] { next }; }
            if (op == OpCodes.Sizeof) { Type t = (Type)ins.Operand; string dst = Push(SItem.I4); Line(dst + " = " + BurstTypes.SizeOf(t) + ";"); return new[] { next }; }

            // 필드
            if (op == OpCodes.Ldfld || op == OpCodes.Ldflda)
            {
                var f = (FieldInfo)ins.Operand;
                string eo = PopE(out SItem o);
                string access = U.Types.FieldAccess(eo, o.IsAddress, f);
                if (op == OpCodes.Ldfld) { string dst = Push(SItem.Of(f.FieldType)); Line(dst + " = " + access + ";"); }
                else { string dst = Push(SItem.RefTo(f.FieldType)); Line(dst + " = &" + access + ";"); }
                return new[] { next };
            }
            if (op == OpCodes.Stfld)
            {
                var f = (FieldInfo)ins.Operand;
                string ev = PopE(out _), eo = PopE(out SItem o);
                if (!o.IsAddress) throw Unsupported("stfld on a value");
                Line(U.Types.FieldAccess(eo, true, f) + " = " + Cast(f.FieldType, ev) + ";");
                return new[] { next };
            }
            if (op == OpCodes.Ldsfld || op == OpCodes.Ldsflda)
            {
                var f = (FieldInfo)ins.Operand;
                if (op == OpCodes.Ldsfld)
                {
                    string val = U.StaticValue(f, out bool global);
                    SItem item = SItem.Of(f.FieldType);
                    if (!global && item.Kind == SK.I4 && U.ConstOf(f) is long cv) item = SItem.I4Const(cv);
                    string dst = Push(item);
                    Line(dst + " = " + val + ";");
                }
                else
                {
                    string dst = Push(SItem.RefTo(f.FieldType));
                    Line(dst + " = (" + U.Types.CType(f.FieldType) + "*)&" + U.StaticAddress(f) + ";");
                }
                return new[] { next };
            }
            if (op == OpCodes.Stsfld) throw Unsupported("writing the static field " + f2(ins));

            // 호출
            if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
            {
                Call(ins, st);
                return new[] { next };
            }

            if (op == OpCodes.Box)
            {
                SItem s = Pop();
                st.Add(SItem.Obj);   // 글자 만들기에만 (Debug.Log · 예외 문구)
                return new[] { next };
            }

            throw Unsupported("the IL instruction " + op.Name);
        }

        string f2(ILInstruction ins) => ins.Operand is FieldInfo f ? f.DeclaringType.Name + "." + f.Name : "?";

        BurstUnsupportedException Unsupported(string what) => new BurstUnsupportedException(what + " in " + ILReader.Name(Method));

        void Label(int offset) { m_Labels.Add(offset); }

        internal static string CString(string s)
        {
            var b = new StringBuilder("\"");
            foreach (byte c in Encoding.UTF8.GetBytes(s ?? ""))
            {
                if (c == '"' || c == '\\') b.Append('\\').Append((char)c);
                else if (c < 32 || c >= 127) b.Append("\\x").Append(c.ToString("X2")).Append("\"\"");
                else b.Append((char)c);
            }
            return b.Append('"').ToString();
        }

        // 형식에 맞춰 넣기 (bool · 작은 정수 · 포인터)
        string Cast(Type t, string e)
        {
            if (t.IsEnum) t = Enum.GetUnderlyingType(t);
            if (t == typeof(bool)) return "(bool)((" + e + ") != 0)";
            if (t.IsPointer || t.IsByRef) return "(" + U.Types.CType(t) + ")(" + e + ")";
            if (t.IsPrimitive) return "(" + U.Types.CType(t) + ")(" + e + ")";
            return e;
        }

        Type IndType(string suffix, SItem addr)
        {
            switch (suffix)
            {
                case "i1": return typeof(sbyte);
                case "u1": return typeof(byte);
                case "i2": return typeof(short);
                case "u2": return typeof(ushort);
                case "i4": return typeof(int);
                case "u4": return typeof(uint);
                case "i8": return typeof(long);
                case "r4": return typeof(float);
                case "r8": return typeof(double);
                case "i": return typeof(IntPtr);
                default: throw Unsupported("ldind/stind." + suffix);
            }
        }

        void Binary(OpCode op, SItem a, string ea, SItem b, string eb, List<SItem> st)
        {
            string sym = op == OpCodes.Add || op == OpCodes.Add_Ovf || op == OpCodes.Add_Ovf_Un ? "+" : op == OpCodes.Sub || op == OpCodes.Sub_Ovf || op == OpCodes.Sub_Ovf_Un ? "-"
                : op == OpCodes.Mul || op == OpCodes.Mul_Ovf || op == OpCodes.Mul_Ovf_Un ? "*" : op == OpCodes.Div || op == OpCodes.Div_Un ? "/" : op == OpCodes.Rem || op == OpCodes.Rem_Un ? "%"
                : op == OpCodes.And ? "&" : op == OpCodes.Or ? "|" : "^";
            bool un = op == OpCodes.Div_Un || op == OpCodes.Rem_Un;
            // 포인터 산술 (바이트 단위)
            if (a.IsAddress || b.IsAddress)
            {
                if (sym == "+" && a.IsAddress && (b.IsInt))
                {
                    string dst = Push(st, a);
                    Line(dst + " = (" + CTypeOf(a) + ")((uint8_t*)" + ea + " + (intptr_t)" + eb + ");");
                    return;
                }
                if (sym == "+" && b.IsAddress && a.IsInt)
                {
                    string dst = Push(st, b);
                    Line(dst + " = (" + CTypeOf(b) + ")((uint8_t*)" + eb + " + (intptr_t)" + ea + ");");
                    return;
                }
                if (sym == "-" && a.IsAddress && b.IsInt)
                {
                    string dst = Push(st, a);
                    Line(dst + " = (" + CTypeOf(a) + ")((uint8_t*)" + ea + " - (intptr_t)" + eb + ");");
                    return;
                }
                if (sym == "-" && a.IsAddress && b.IsAddress)
                {
                    string dst = Push(st, SItem.I);
                    Line(dst + " = (intptr_t)((uint8_t*)" + ea + " - (uint8_t*)" + eb + ");");
                    return;
                }
                if ((sym == "&" || sym == "|") && a.IsAddress && b.IsInt)
                {
                    string dst = Push(st, SItem.I);
                    Line(dst + " = (intptr_t)" + ea + " " + sym + " (intptr_t)" + eb + ";");
                    return;
                }
                throw Unsupported("pointer arithmetic " + sym);
            }
            if (a.IsFloat || b.IsFloat)
            {
                bool r8 = a.Kind == SK.R8 || b.Kind == SK.R8;
                string dst = Push(st, r8 ? SItem.R8 : SItem.R4);
                if (sym == "%") Line(dst + " = " + (r8 ? "fmod" : "fmodf") + "(" + ea + ", " + eb + ");");
                else if (sym == "&" || sym == "|" || sym == "^") throw Unsupported("bit operation on floats");
                else Line(dst + " = " + ea + " " + sym + " " + eb + ";");
                return;
            }
            SK k = a.Kind == SK.I8 || b.Kind == SK.I8 ? SK.I8 : a.Kind == SK.I || b.Kind == SK.I ? SK.I : SK.I4;
            string st_ = k == SK.I4 ? "int32_t" : k == SK.I8 ? "int64_t" : "intptr_t", ut = k == SK.I4 ? "uint32_t" : k == SK.I8 ? "uint64_t" : "uintptr_t";
            string d = Push(st, k == SK.I4 ? SItem.I4 : k == SK.I8 ? SItem.I8 : SItem.I);
            if (sym == "+" || sym == "-" || sym == "*")   // 감싸기 (C# unchecked) — 부호 있는 넘침은 C++ 에서 미정의
                Line(d + " = (" + st_ + ")((" + ut + ")" + ea + " " + sym + " (" + ut + ")" + eb + ");");
            else if (un)
                Line(d + " = (" + st_ + ")((" + ut + ")" + ea + " " + sym + " (" + ut + ")" + eb + ");");
            else if (sym == "/" || sym == "%")
                Line(d + " = (" + st_ + ")" + ea + " " + sym + " (" + st_ + ")" + eb + ";");
            else
                Line(d + " = (" + st_ + ")((" + st_ + ")" + ea + " " + sym + " (" + st_ + ")" + eb + ");");
        }

        string Push(List<SItem> st, SItem s) { st.Add(s); return Slot(st.Count - 1, s); }

        string Compare(string kind, SItem a, string ea, SItem b, string eb)
        {
            bool un = kind.EndsWith(".un");
            string k = un ? kind.Substring(0, kind.Length - 3) : kind;
            string sym = k == "eq" ? "==" : k == "ne" ? "!=" : k == "gt" ? ">" : k == "ge" ? ">=" : k == "lt" ? "<" : "<=";
            if (a.Kind == SK.Null) ea = "nullptr";
            if (b.Kind == SK.Null) eb = "nullptr";
            if (a.IsAddress || b.IsAddress || a.Kind == SK.Null || b.Kind == SK.Null)
                return "(uintptr_t)(" + ea + ") " + sym + " (uintptr_t)(" + eb + ")";
            if (a.IsFloat || b.IsFloat)
            {
                if (!un || sym == "==" || sym == "!=") return ea + " " + sym + " " + eb;
                string inv = sym == ">" ? "<=" : sym == ">=" ? "<" : sym == "<" ? ">=" : ">";   // 순서 없음 (NaN) 도 참
                return "!(" + ea + " " + inv + " " + eb + ")";
            }
            SK kk = a.Kind == SK.I8 || b.Kind == SK.I8 ? SK.I8 : a.Kind == SK.I || b.Kind == SK.I ? SK.I : SK.I4;
            string ct = un ? (kk == SK.I4 ? "uint32_t" : kk == SK.I8 ? "uint64_t" : "uintptr_t") : (kk == SK.I4 ? "int32_t" : kk == SK.I8 ? "int64_t" : "intptr_t");
            return "(" + ct + ")" + ea + " " + sym + " (" + ct + ")" + eb;
        }

        void Convert(string name, SItem a, string ea, List<SItem> st, bool fromUnsigned)
        {
            // name: conv.i1 · conv.u1 · … · conv.r4 · conv.r8 · conv.r_un · conv.i · conv.u (.ovf 는 넘침 검사 없이)
            string to = name.Substring(5);
            if (to.EndsWith("_un") && to != "r_un") to = to.Substring(0, to.Length - 3);
            string src = ea;
            if (a.IsAddress) src = "(intptr_t)" + ea;
            // conv.u8 · conv.u 는 32 비트 정수를 0 으로 늘린다
            string zext32 = a.Kind == SK.I4 ? "(uint32_t)" + src : src;
            switch (to)
            {
                case "i1": Line(Push(st, SItem.I4) + " = (int32_t)(int8_t)" + src + ";"); return;
                case "u1": Line(Push(st, SItem.I4) + " = (int32_t)(uint8_t)" + src + ";"); return;
                case "i2": Line(Push(st, SItem.I4) + " = (int32_t)(int16_t)" + src + ";"); return;
                case "u2": Line(Push(st, SItem.I4) + " = (int32_t)(uint16_t)" + src + ";"); return;
                case "i4": Line(Push(st, SItem.I4) + " = (int32_t)" + src + ";"); return;
                case "u4": Line(Push(st, SItem.I4) + " = (int32_t)(uint32_t)" + (a.IsFloat ? "(int64_t)" : "") + src + ";"); return;
                case "i8": Line(Push(st, SItem.I8) + " = (int64_t)" + (fromUnsigned && a.Kind == SK.I4 ? "(uint32_t)" : "") + src + ";"); return;
                case "u8": Line(Push(st, SItem.I8) + " = (int64_t)(uint64_t)" + (a.IsFloat ? src : zext32) + ";"); return;
                case "r4": Line(Push(st, SItem.R4) + " = (float)" + src + ";"); return;
                case "r8": Line(Push(st, SItem.R8) + " = (double)" + src + ";"); return;
                case "r_un": Line(Push(st, SItem.R8) + " = (double)(" + (a.Kind == SK.I8 ? "uint64_t" : "uint32_t") + ")" + src + ";"); return;
                case "i": Line(Push(st, SItem.I) + " = (intptr_t)" + src + ";"); return;
                case "u": Line(Push(st, SItem.I) + " = (intptr_t)(uintptr_t)" + (a.IsFloat ? src : zext32) + ";"); return;
            }
            throw Unsupported("conversion " + name);
        }

        // ---------------------------------------------------------------- 호출
        void Call(ILInstruction ins, List<SItem> st)
        {
            var m = (MethodBase)ins.Operand;
            ParameterInfo[] ps = m.GetParameters();
            bool newobj = ins.OpCode == OpCodes.Newobj;
            bool hasThis = !m.IsStatic && !newobj;
            int argc = ps.Length + (hasThis ? 1 : 0);
            if (st.Count < argc) throw Unsupported("stack underflow at a call");
            var items = new SItem[argc];
            var exprs = new string[argc];
            for (int k = argc - 1; k >= 0; k--)
            {
                SItem s = st[st.Count - 1];
                items[k] = s;
                exprs[k] = s.Symbolic ? (s.Kind == SK.Null ? "nullptr" : null) : Slot(st.Count - 1, s);
                st.RemoveAt(st.Count - 1);
            }

            // constrained. T callvirt — struct 의 메서드로
            if (ins.Constrained != null && ins.OpCode == OpCodes.Callvirt && m.Name == "ToString" && (m is MethodInfo tm) && tm.ReturnType == typeof(string))
            {
                st.Add(SItem.Str(null));   // 글자 (문구에만)
                return;
            }
            if (ins.Constrained != null && ins.OpCode == OpCodes.Callvirt)
            {
                Type ct = ins.Constrained;
                if (!ct.IsValueType) throw Unsupported("virtual call on a class " + BurstTypes.Display(ct));
                MethodBase impl = Resolve(ct, (MethodInfo)m);
                if (impl == null) throw Unsupported("calling " + m.Name + " on " + BurstTypes.Display(ct) + " needs boxing");
                m = impl;
            }
            else if (ins.OpCode == OpCodes.Callvirt && !m.IsStatic && !(m.DeclaringType.IsValueType))
            {
                // callvirt 이 struct 가 아닌 대상: 기호 (문자열) 에서 ToString 등만
                if (items.Length > 0 && items[0].Symbolic && BurstIntrinsics.IsStringish(m)) { PushResult(st, m, SItem.Str(null), null); return; }
                throw Unsupported("calling " + ILReader.Name(m) + " (a managed object)");
            }

            // 예외 만들기: 기호로 (throw 가 문구를 쓴다)
            if (newobj && typeof(Exception).IsAssignableFrom(m.DeclaringType))
            {
                string text = BurstTypes.Display(m.DeclaringType);
                for (int k = 0; k < items.Length; k++)
                    if (items[k].Kind == SK.Str && items[k].Text != null && ps[k].ParameterType == typeof(string)) { text += ": " + items[k].Text; break; }
                st.Add(new SItem { Kind = SK.Exc, Type = m.DeclaringType, Text = text });
                return;
            }

            // 내장 (Math · Unsafe · Interlocked · 컨테이너 검사 · Debug.Log …)
            if (BurstIntrinsics.TryCall(this, U, m, items, exprs, out SItem result, out string code, out bool fails))
            {
                if (fails) MayFail = true;
                if (result != null)
                {
                    string dst = result.Symbolic ? null : Push(st, result);
                    if (result.Symbolic) st.Add(result);
                    if (dst != null) Line(dst + " = " + code + ";");
                    else if (code != null) Line(code + ";");
                }
                else if (code != null) Line(code + ";");
                return;
            }

            foreach (SItem s in items)
                if (s.Symbolic && s.Kind != SK.Null) throw Unsupported(KindName(s) + " passed to " + ILReader.Name(m));

            if (newobj)
            {
                Type t = m.DeclaringType;
                if (!t.IsValueType) throw Unsupported("creating the managed object " + BurstTypes.Display(t));
                BurstMethodCompiler ctor = U.Require(m);
                Callees.Add(ctor);
                var args = new List<string> { "&nw" };
                for (int k = 0; k < ps.Length; k++) args.Add(Cast(ps[k].ParameterType, exprs[k]));
                string ct = U.Types.CType(t);
                string tmp = Push(st, SItem.Val(t));   // 인자를 다 읽은 뒤에 담는다 (같은 이름의 칸일 수 있다)
                Line("{ " + ct + " nw{}; " + ctor.CName + "(" + string.Join(", ", args) + ");" + U.FailCheck(ctor, ReturnDefault) + " " + tmp + " = nw; }");
                return;
            }

            BurstMethodCompiler callee = U.Require(m);
            Callees.Add(callee);
            var a = new List<string>();
            int off = 0;
            if (hasThis)
            {
                // this: struct 의 주소 (값이면 임시로)
                if (items[0].IsAddress) a.Add("(" + U.Types.CType(m.DeclaringType) + "*)" + exprs[0]);
                else throw Unsupported("calling an instance method on a value (not an address)");
                off = 1;
            }
            for (int k = 0; k < ps.Length; k++) a.Add(Cast(ps[k].ParameterType, exprs[k + off]));
            Type ret = m is MethodInfo mi ? mi.ReturnType : typeof(void);
            string call = callee.CName + "(" + string.Join(", ", a) + ")";
            if (ret == typeof(void)) Line(call + ";" + U.FailCheck(callee, ReturnDefault));
            else { string dst = Push(st, SItem.Of(ret)); Line(dst + " = " + call + ";" + U.FailCheck(callee, ReturnDefault)); }
        }

        void PushResult(List<SItem> st, MethodBase m, SItem s, string code) { st.Add(s); }

        // 인터페이스 · Object 가상 메서드 → struct 의 구현
        static MethodBase Resolve(Type t, MethodInfo m)
        {
            if (m.DeclaringType.IsInterface)
            {
                InterfaceMapping map = t.GetInterfaceMap(m.DeclaringType);
                for (int k = 0; k < map.InterfaceMethods.Length; k++)
                    if (map.InterfaceMethods[k] == m || (map.InterfaceMethods[k].Name == m.Name && map.InterfaceMethods[k].MetadataToken == m.MetadataToken))
                        return map.TargetMethods[k].IsGenericMethodDefinition && m.IsGenericMethod ? map.TargetMethods[k].MakeGenericMethod(m.GetGenericArguments()) : map.TargetMethods[k];
                return null;
            }
            if (m.DeclaringType == t) return m;
            MethodInfo own = t.GetMethod(m.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, m.GetParameters().Select(p => p.ParameterType).ToArray(), null);
            return own;
        }

        internal string ReturnDefaultCode => ReturnDefault;
    }
}
