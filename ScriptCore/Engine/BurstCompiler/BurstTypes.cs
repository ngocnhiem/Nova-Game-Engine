using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SysUnsafe = System.Runtime.CompilerServices.Unsafe;

namespace NovaEngine.Burst.Compiler
{
    // C# 형식 → C++ 형식. struct 는 .NET 과 같은 배치 (Sequential = C 의 자연 정렬 — 크기를 static_assert 로 확인),
    //  Explicit · Pack · Size 가 맞지 않으면 바이트 배열 + 오프셋 접근 (raw)
    internal sealed class BurstTypes
    {
        internal sealed class StructInfo
        {
            public Type Type;
            public string CName;
            public bool Raw;
            public int Size, Align;
            public List<(FieldInfo Field, int Offset)> Fields = new List<(FieldInfo, int)>();
            public bool Defined;
        }

        readonly Dictionary<Type, StructInfo> m_Structs = new Dictionary<Type, StructInfo>();
        readonly List<StructInfo> m_Order = new List<StructInfo>();
        int m_NextId;

        internal static string Display(Type t) => NovaEngine.Jobs.JobReflectionUtil.NiceName(t);

        static bool ContainsReferences(Type t)
        {
            MethodInfo m = typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences)).MakeGenericMethod(t);
            return (bool)m.Invoke(null, null);
        }

        internal static int SizeOf(Type t)
        {
            if (t.IsPointer || t.IsByRef || t == typeof(IntPtr) || t == typeof(UIntPtr)) return IntPtr.Size;
            if (t.IsEnum) t = Enum.GetUnderlyingType(t);
            return (int)typeof(SysUnsafe).GetMethod(nameof(SysUnsafe.SizeOf)).MakeGenericMethod(t).Invoke(null, null);
        }

        static string Primitive(Type t)
        {
            if (t == typeof(bool)) return "bool";
            if (t == typeof(sbyte)) return "int8_t";
            if (t == typeof(byte)) return "uint8_t";
            if (t == typeof(short)) return "int16_t";
            if (t == typeof(ushort)) return "uint16_t";
            if (t == typeof(char)) return "char16_t";
            if (t == typeof(int)) return "int32_t";
            if (t == typeof(uint)) return "uint32_t";
            if (t == typeof(long)) return "int64_t";
            if (t == typeof(ulong)) return "uint64_t";
            if (t == typeof(float)) return "float";
            if (t == typeof(double)) return "double";
            if (t == typeof(IntPtr)) return "intptr_t";
            if (t == typeof(UIntPtr)) return "uintptr_t";
            if (t == typeof(void)) return "void";
            if (t == typeof(DefaultInterpolatedStringHandler)) return "uint8_t";   // 문자열 보간 — 오류 문구에만, 값 없음
            return null;
        }

        /// <summary>지역 · 인자 · 필드 형식의 C++ 이름 (ref · in · out = 포인터)</summary>
        public string CType(Type t)
        {
            if (t.IsByRef || t.IsPointer)
            {
                Type e = t.GetElementType();
                if (e.IsByRef || e.IsPointer) return CType(e) + "*";
                return (e == typeof(void) ? "void" : CTypeForPointer(e)) + "*";
            }
            if (t.IsEnum) return CType(Enum.GetUnderlyingType(t));
            string p = Primitive(t);
            if (p != null) return p;
            if (t.IsValueType && !t.IsGenericParameter) return Require(t).CName;
            throw new BurstUnsupportedException("the managed type " + Display(t) + " (classes, strings, arrays and delegates can not be used in native Burst code)");
        }

        // 포인터의 대상은 앞 선언만 있어도 된다 (struct 정의 순서와 상관없이)
        string CTypeForPointer(Type e)
        {
            if (e.IsEnum) return CType(Enum.GetUnderlyingType(e));
            string p = Primitive(e);
            if (p != null) return p;
            if (e.IsValueType) return Require(e).CName;
            throw new BurstUnsupportedException("a pointer to the managed type " + Display(e));
        }

        public StructInfo Info(Type t) => Require(t);

        StructInfo Require(Type t)
        {
            if (m_Structs.TryGetValue(t, out StructInfo s)) return s;
            if (!t.IsValueType || t.IsPrimitive || t.IsEnum) throw new BurstUnsupportedException("not a struct: " + Display(t));
            if (t.IsByRefLike) throw new BurstUnsupportedException("ref struct " + Display(t) + " (Span and friends)");
            if (ContainsReferences(t)) throw new BurstUnsupportedException("the struct " + Display(t) + " contains managed references");
            s = new StructInfo { Type = t, CName = "t" + (m_NextId++) + "_" + Sanitize(t.Name) };
            m_Structs[t] = s;
            Layout(s);
            m_Order.Add(s);
            return s;
        }

        internal static string Sanitize(string n)
        {
            var b = new StringBuilder();
            foreach (char c in n) b.Append(char.IsLetterOrDigit(c) && c < 128 ? c : '_');
            return b.Length > 24 ? b.ToString(0, 24) : b.ToString();
        }

        int AlignOf(Type t)
        {
            if (t.IsPointer || t.IsByRef || t == typeof(IntPtr) || t == typeof(UIntPtr)) return IntPtr.Size;
            if (t.IsEnum) t = Enum.GetUnderlyingType(t);
            if (t.IsPrimitive) return Math.Min(SizeOf(t), 8);
            return Require(t).Align;
        }

        void Layout(StructInfo s)
        {
            Type t = s.Type;
            FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).OrderBy(f => f.MetadataToken).ToArray();
            StructLayoutAttribute la = t.StructLayoutAttribute;
            int pack = la != null && la.Pack > 0 ? la.Pack : 8;
            bool explicitLayout = la != null && la.Value == LayoutKind.Explicit;
            if (la != null && la.Value == LayoutKind.Auto) throw new BurstUnsupportedException("auto-layout struct " + Display(t));
            int size = SizeOf(t);
            int offset = 0, align = 1;
            bool natural = !explicitLayout;
            foreach (FieldInfo f in fields)
            {
                Type ft = f.FieldType;
                // 포인터 필드의 대상 struct 도 지금 등록 (앞 선언 목록에 들어가게)
                for (Type e = ft; e.IsPointer; e = e.GetElementType())
                {
                    Type pe = e.GetElementType();
                    if (pe.IsValueType && !pe.IsPrimitive && !pe.IsEnum && !pe.IsPointer && pe != typeof(void)) Require(pe);
                }
                int fa = Math.Min(AlignOf(ft), pack), fs = SizeOf(ft);
                int off;
                if (explicitLayout)
                {
                    var fo = f.GetCustomAttribute<FieldOffsetAttribute>();
                    off = fo?.Value ?? 0;
                }
                else
                {
                    offset = (offset + fa - 1) / fa * fa;
                    off = offset;
                    offset += fs;
                }
                if (fa != AlignOf(ft)) natural = false;   // Pack 이 정렬을 줄였다
                align = Math.Max(align, fa);
                s.Fields.Add((f, off));
            }
            int natSize = (offset + align - 1) / align * align;
            if (fields.Length == 0) natSize = 1;
            s.Align = align;
            s.Size = size;
            s.Raw = !natural || natSize > size;
            // Size= 로 커진 struct (fixed 버퍼) 는 뒤에 채움 바이트를 붙여 typed 로
        }

        /// <summary>앞 선언 + 정의 (값으로 든 필드의 struct 먼저)</summary>
        public void Emit(StringBuilder sb)
        {
            foreach (StructInfo s in m_Order.ToArray()) sb.Append("struct ").Append(s.CName).Append(";\n");
            foreach (StructInfo s in m_Order.ToArray()) Define(s, sb);
        }

        void Define(StructInfo s, StringBuilder sb)
        {
            if (s.Defined) return;
            s.Defined = true;
            foreach (var (f, _) in s.Fields)
            {
                Type ft = f.FieldType.IsEnum ? Enum.GetUnderlyingType(f.FieldType) : f.FieldType;
                if (ft.IsValueType && !ft.IsPrimitive && !ft.IsPointer && m_Structs.TryGetValue(ft, out StructInfo inner)) Define(inner, sb);
            }
            sb.Append("// ").Append(Display(s.Type)).Append('\n');
            sb.Append("struct alignas(").Append(s.Align).Append(") ").Append(s.CName).Append(" {");
            if (s.Raw)
                sb.Append(" uint8_t raw[").Append(s.Size).Append("];");
            else
            {
                int end = 0;
                foreach (var (f, off) in s.Fields)
                {
                    sb.Append(' ').Append(CType(f.FieldType)).Append(' ').Append(FieldName(f)).Append(';');
                    end = Math.Max(end, off + SizeOf(f.FieldType));
                }
                int rounded = (end + s.Align - 1) / s.Align * s.Align;
                if (s.Fields.Count == 0) sb.Append(" uint8_t _empty;");
                else if (s.Size > rounded) sb.Append(" uint8_t _pad[").Append(s.Size - end).Append("];");
            }
            sb.Append(" };\n");
            sb.Append("static_assert(sizeof(").Append(s.CName).Append(") == ").Append(s.Size).Append(", \"layout of ").Append(Display(s.Type).Replace("\"", "")).Append("\");\n");
        }

        internal static string FieldName(FieldInfo f) => "f_" + Sanitize(f.Name.Replace("<", "").Replace(">", "_"));

        /// <summary>필드 접근 식 — obj 가 값 (isPointer false) 또는 포인터</summary>
        public string FieldAccess(string obj, bool isPointer, FieldInfo f)
        {
            Type owner = f.DeclaringType;
            StructInfo s = Require(owner);
            if (!s.Raw) return isPointer ? "(" + obj + ")->" + FieldName(f) : "(" + obj + ")." + FieldName(f);
            int off = s.Fields.First(x => x.Field == f || (x.Field.MetadataToken == f.MetadataToken && x.Field.Module == f.Module)).Offset;
            string baseAddr = isPointer ? "(uint8_t*)(" + obj + ")" : "(uint8_t*)&(" + obj + ")";
            return "(*(" + CType(f.FieldType) + "*)(" + baseAddr + " + " + off + "))";
        }

        // 정적 readonly struct 값 → 바이트
        internal static byte[] Bytes(Type t, object value)
        {
            MethodInfo m = typeof(BurstTypes).GetMethod(nameof(BytesOf), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(t);
            return (byte[])m.Invoke(null, new[] { value });
        }
        static byte[] BytesOf<T>(object value) where T : struct
        {
            T v = (T)value;
            var b = new byte[SysUnsafe.SizeOf<T>()];
            if (b.Length > 0) SysUnsafe.WriteUnaligned(ref b[0], v);
            return b;
        }
    }
}
