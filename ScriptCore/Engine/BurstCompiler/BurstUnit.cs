using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NovaEngine.Collections;

namespace NovaEngine.Burst.Compiler
{
    // 잡 하나의 번역 단위 (C++ namespace 하나) — 한 잡이 실패해도 다른 잡은 네이티브로
    internal sealed class BurstUnit
    {
        public readonly string Ns;
        public readonly bool Checks;
        public readonly BurstTypes Types = new BurstTypes();
        readonly Dictionary<MethodBase, BurstMethodCompiler> m_Methods = new Dictionary<MethodBase, BurstMethodCompiler>();
        readonly List<BurstMethodCompiler> m_All = new List<BurstMethodCompiler>();
        readonly Queue<BurstMethodCompiler> m_Queue = new Queue<BurstMethodCompiler>();
        readonly Dictionary<Type, int> m_TypeIds = new Dictionary<Type, int>();
        readonly Dictionary<FieldInfo, string> m_Statics = new Dictionary<FieldInfo, string>();
        readonly StringBuilder m_Globals = new StringBuilder();
        /// <summary>포인터가 든 정적 readonly 값 (SharedStatic 등) — DLL 을 읽은 뒤 C# 이 채운다 (값이 프로세스마다 다르다)</summary>
        public readonly List<(FieldInfo Field, string Export, string Global)> RuntimeGlobals = new List<(FieldInfo, string, string)>();

        public BurstUnit(string ns, bool checks) { Ns = ns; Checks = checks; }

        public int TypeId(Type t)
        {
            if (!m_TypeIds.TryGetValue(t, out int id)) m_TypeIds[t] = id = m_TypeIds.Count;
            return id;
        }

        public BurstMethodCompiler Require(MethodBase m)
        {
            if (m_Methods.TryGetValue(m, out BurstMethodCompiler c)) return c;
            Type dt = m.DeclaringType;
            if (dt == null) throw new BurstUnsupportedException("a global method");
            if (dt.Assembly == typeof(object).Assembly || (dt.Namespace != null && (dt.Namespace == "System" || dt.Namespace.StartsWith("System.", StringComparison.Ordinal))))
                throw new BurstUnsupportedException("the .NET method " + ILReader.Name(m) + " (not available in native Burst code)");
            if (m.IsAbstract || m.GetMethodBody() == null) throw new BurstUnsupportedException(ILReader.Name(m) + " has no body");
            if (m.ContainsGenericParameters) throw new BurstUnsupportedException("open generic method " + ILReader.Name(m));
            c = new BurstMethodCompiler(this, m, "m" + m_All.Count + "_" + BurstTypes.Sanitize(m.Name.Replace(".", "")));
            m_Methods[m] = c;
            m_All.Add(c);
            m_Queue.Enqueue(c);
            return c;
        }

        public void Run()
        {
            while (m_Queue.Count > 0) m_Queue.Dequeue().Compile();
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (BurstMethodCompiler c in m_All)
                    if (!c.MayFail && c.Callees.Any(x => x.MayFail)) { c.MayFail = true; changed = true; }
            }
        }

        // 부른 뒤 실패 검사 (부른 함수가 실패할 수 있을 때만 — 안전 검사가 없는 플레이어 빌드는 대부분 없다 → 벡터화)
        public string FailCheck(BurstMethodCompiler callee, string returnDefault) => "/*FC:" + callee.CName + "*/";

        // ---------------------------------------------------------------- 정적 값
        static bool ContainsPointers(Type t)
        {
            if (t.IsPointer || t == typeof(IntPtr) || t == typeof(UIntPtr)) return true;
            if (t.IsPrimitive || t.IsEnum) return false;
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (ContainsPointers(f.FieldType)) return true;
            return false;
        }

        internal static string Literal(Type t, object v)
        {
            if (t.IsEnum) { t = Enum.GetUnderlyingType(t); v = System.Convert.ChangeType(v, t, CultureInfo.InvariantCulture); }
            if (t == typeof(bool)) return (bool)v ? "1" : "0";
            if (t == typeof(float)) return "nb_bitcast<float>((uint32_t)0x" + BitConverter.SingleToUInt32Bits((float)v).ToString("X8") + "u)";
            if (t == typeof(double)) return "nb_bitcast<double>((uint64_t)0x" + BitConverter.DoubleToUInt64Bits((double)v).ToString("X16") + "ull)";
            if (t == typeof(char)) return ((int)(char)v).ToString(CultureInfo.InvariantCulture);
            if (t == typeof(long)) return "(int64_t)0x" + ((long)v).ToString("X16") + "ull";
            if (t == typeof(ulong)) return "(uint64_t)0x" + ((ulong)v).ToString("X16") + "ull";
            if (t == typeof(uint)) return "(uint32_t)0x" + ((uint)v).ToString("X8") + "u";
            if (t == typeof(int)) return (int)v == int.MinValue ? "(int32_t)0x80000000" : ((int)v).ToString(CultureInfo.InvariantCulture);
            return System.Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }

        // 바이트 → 집합 초기화 (typed struct 는 필드마다 — 컴파일러가 상수로 접는다)
        string Initializer(Type t, byte[] b, int off)
        {
            if (t.IsEnum) t = Enum.GetUnderlyingType(t);
            if (t.IsPrimitive)
            {
                object v;
                if (t == typeof(bool)) v = b[off] != 0;
                else if (t == typeof(byte)) v = b[off];
                else if (t == typeof(sbyte)) v = (sbyte)b[off];
                else if (t == typeof(short)) v = BitConverter.ToInt16(b, off);
                else if (t == typeof(ushort)) v = BitConverter.ToUInt16(b, off);
                else if (t == typeof(char)) v = BitConverter.ToChar(b, off);
                else if (t == typeof(int)) v = BitConverter.ToInt32(b, off);
                else if (t == typeof(uint)) v = BitConverter.ToUInt32(b, off);
                else if (t == typeof(long)) v = BitConverter.ToInt64(b, off);
                else if (t == typeof(ulong)) v = BitConverter.ToUInt64(b, off);
                else if (t == typeof(float)) v = BitConverter.ToSingle(b, off);
                else if (t == typeof(double)) v = BitConverter.ToDouble(b, off);
                else throw new BurstUnsupportedException("static value of " + t.Name);
                return Literal(t, v);
            }
            BurstTypes.StructInfo s = Types.Info(t);
            if (s.Raw)
            {
                var sb = new StringBuilder("{ { ");
                for (int k = 0; k < s.Size; k++) { if (k > 0) sb.Append(", "); sb.Append(b[off + k]); }
                return sb.Append(" } }").ToString();
            }
            return "{ " + string.Join(", ", s.Fields.Select(f => Initializer(f.Field.FieldType, b, off + f.Offset))) + " }";
        }

        string Global(FieldInfo f)
        {
            if (m_Statics.TryGetValue(f, out string g)) return g;
            if (!f.IsInitOnly && !f.IsLiteral) throw new BurstUnsupportedException("reading the non-readonly static field " + BurstTypes.Display(f.DeclaringType) + "." + f.Name + " (use SharedStatic<T> or pass the value into the job)");
            g = "g" + m_Statics.Count;
            m_Statics[f] = g;
            Type t = f.FieldType;
            string ct = Types.CType(t);
            object v = f.GetValue(null);
            if (ContainsPointers(t))
            {
                // 포인터 값은 이 프로세스에서만 맞다 — DLL 을 읽은 뒤 채운다
                string export = "nb_init_" + Ns + "_" + g;
                m_Globals.Append("static ").Append(ct).Append(' ').Append(g).Append("{};\n");
                RuntimeGlobals.Add((f, export, g));
                return g;
            }
            byte[] bytes = t.IsPrimitive || t.IsEnum ? null : BurstTypes.Bytes(t, v);
            string init = bytes == null ? Literal(t, v) : Initializer(t, bytes, 0);
            m_Globals.Append("static const ").Append(ct).Append(' ').Append(g).Append(" = ").Append(init).Append(";   // ").Append(BurstTypes.Display(f.DeclaringType)).Append('.').Append(f.Name).Append('\n');
            return g;
        }

        public string StaticValue(FieldInfo f, out bool global)
        {
            global = false;
            if (f.DeclaringType == typeof(CollectionChecks) && f.Name == nameof(CollectionChecks.Enabled)) return Checks ? "1" : "0";   // 안전 검사 — 번역할 때 접는다
            Type t = f.FieldType;
            if ((t.IsPrimitive || t.IsEnum) && (f.IsInitOnly || f.IsLiteral) && t != typeof(IntPtr) && t != typeof(UIntPtr))
                return Literal(t, f.GetValue(null));
            global = true;
            return Global(f);
        }

        public string StaticAddress(FieldInfo f) => Global(f);

        /// <summary>정적 readonly 정수 · bool 의 값 (상수 분기 접기)</summary>
        public long? ConstOf(FieldInfo f)
        {
            if (f.DeclaringType == typeof(CollectionChecks) && f.Name == nameof(CollectionChecks.Enabled)) return Checks ? 1 : 0;
            Type t = f.FieldType.IsEnum ? Enum.GetUnderlyingType(f.FieldType) : f.FieldType;
            if (!(f.IsInitOnly || f.IsLiteral) || !t.IsPrimitive || t == typeof(float) || t == typeof(double) || t == typeof(IntPtr) || t == typeof(UIntPtr) || t == typeof(long) || t == typeof(ulong)) return null;
            object v = f.GetValue(null);
            if (v is bool b) return b ? 1 : 0;
            return System.Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- 출력
        static readonly Regex s_FailCheck = new Regex(@"/\*FC:(\w+)\*/", RegexOptions.Compiled);

        public string Emit(string entry)
        {
            var byName = m_All.ToDictionary(c => c.CName);
            var sb = new StringBuilder();
            sb.Append("namespace ").Append(Ns).Append(" {\n");
            var types = new StringBuilder();
            Types.Emit(types);
            sb.Append(types);
            sb.Append(m_Globals);
            foreach (BurstMethodCompiler c in m_All) sb.Append(c.Prototype).Append(";\n");
            foreach (BurstMethodCompiler c in m_All)
            {
                string body = s_FailCheck.Replace(c.Body, mm => byName.TryGetValue(mm.Groups[1].Value, out BurstMethodCompiler callee) && callee.MayFail ? " if (nb_failed()) " + c.ReturnDefault : "");
                sb.Append(body);
            }
            sb.Append(entry);
            sb.Append("}\n");
            foreach (var (f, export, g) in RuntimeGlobals)
                sb.Append("extern \"C\" __declspec(dllexport) void ").Append(export).Append("(const void* p) { memcpy(&").Append(Ns).Append("::").Append(g).Append(", p, sizeof(").Append(Ns).Append("::").Append(g).Append(")); }\n");
            return sb.ToString();
        }
    }
}
