using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace NovaEngine.Burst.Compiler
{
    // [BurstCompile] 2 단계: IL → C++ 번역기 (에디터에서, 게임 어셈블리를 읽은 뒤). 못 옮기면 이 예외 — 그 잡은 JIT 로 돈다
    internal sealed class BurstUnsupportedException : Exception
    {
        public BurstUnsupportedException(string message) : base(message) { }
    }

    internal sealed class ILInstruction
    {
        public int Offset;
        public int Index;
        public OpCode OpCode;
        public object Operand;       // int · long · float · double · MemberInfo · Type · string · int[] (switch 대상) · byte/short (지역 · 인자 번호)
        public int Target = -1;      // 분기 대상 (offset)
        public int[] Targets;        // switch
        public Type Constrained;     // constrained. 접두
        public int Size;
    }

    internal static class ILReader
    {
        static readonly Dictionary<short, OpCode> s_OpCodes = new Dictionary<short, OpCode>();

        static ILReader()
        {
            foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.GetValue(null) is OpCode op) s_OpCodes[op.Value] = op;
        }

        public static List<ILInstruction> Read(MethodBase method)
        {
            MethodBody body = method.GetMethodBody() ?? throw new BurstUnsupportedException(Name(method) + " has no IL body (extern / abstract)");
            byte[] il = body.GetILAsByteArray();
            Module module = method.Module;
            Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
            Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            var list = new List<ILInstruction>();
            int pos = 0;
            Type constrained = null;
            int prefixStart = -1;   // 접두 (constrained. · readonly. …) 가 있으면 분기 대상 offset 은 접두의 자리
            while (pos < il.Length)
            {
                int start = pos;
                short value = il[pos++];
                if (value == 0xFE) value = (short)(0xFE00 | il[pos++]);
                if (!s_OpCodes.TryGetValue(value, out OpCode op)) throw new BurstUnsupportedException("unknown IL opcode 0x" + value.ToString("X") + " in " + Name(method));
                var ins = new ILInstruction { Offset = start, OpCode = op };
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget: { sbyte d = (sbyte)il[pos]; pos += 1; ins.Target = pos + d; break; }
                    case OperandType.InlineBrTarget: { int d = BitConverter.ToInt32(il, pos); pos += 4; ins.Target = pos + d; break; }
                    case OperandType.ShortInlineI: ins.Operand = op == OpCodes.Ldc_I4_S ? (int)(sbyte)il[pos] : (int)il[pos]; pos += 1; break;
                    case OperandType.ShortInlineVar: ins.Operand = (int)il[pos]; pos += 1; break;
                    case OperandType.InlineVar: ins.Operand = (int)BitConverter.ToUInt16(il, pos); pos += 2; break;
                    case OperandType.InlineI: ins.Operand = BitConverter.ToInt32(il, pos); pos += 4; break;
                    case OperandType.InlineI8: ins.Operand = BitConverter.ToInt64(il, pos); pos += 8; break;
                    case OperandType.ShortInlineR: ins.Operand = BitConverter.ToSingle(il, pos); pos += 4; break;
                    case OperandType.InlineR: ins.Operand = BitConverter.ToDouble(il, pos); pos += 8; break;
                    case OperandType.InlineString: ins.Operand = module.ResolveString(BitConverter.ToInt32(il, pos)); pos += 4; break;
                    case OperandType.InlineSwitch:
                    {
                        int n = BitConverter.ToInt32(il, pos); pos += 4;
                        var rel = new int[n];
                        for (int k = 0; k < n; k++) { rel[k] = BitConverter.ToInt32(il, pos); pos += 4; }
                        ins.Targets = new int[n];
                        for (int k = 0; k < n; k++) ins.Targets[k] = pos + rel[k];
                        break;
                    }
                    case OperandType.InlineMethod:
                    case OperandType.InlineField:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                    {
                        int token = BitConverter.ToInt32(il, pos); pos += 4;
                        try
                        {
                            if (op.OperandType == OperandType.InlineMethod) ins.Operand = module.ResolveMethod(token, typeArgs, methodArgs);
                            else if (op.OperandType == OperandType.InlineField) ins.Operand = module.ResolveField(token, typeArgs, methodArgs);
                            else if (op.OperandType == OperandType.InlineType) ins.Operand = module.ResolveType(token, typeArgs, methodArgs);
                            else ins.Operand = module.ResolveMember(token, typeArgs, methodArgs);
                        }
                        catch (Exception e) { throw new BurstUnsupportedException("can not resolve token in " + Name(method) + ": " + e.Message); }
                        break;
                    }
                    case OperandType.InlineSig: throw new BurstUnsupportedException("calli (function pointer call) in " + Name(method));
                    default: throw new BurstUnsupportedException("operand type " + op.OperandType + " in " + Name(method));
                }
                ins.Size = pos - start;
                if (op == OpCodes.Constrained) { constrained = (Type)ins.Operand; if (prefixStart < 0) prefixStart = start; continue; }
                if (op == OpCodes.Readonly || op == OpCodes.Volatile || op == OpCodes.Tailcall || op == OpCodes.Unaligned) { if (prefixStart < 0) prefixStart = start; continue; }   // 무시해도 되는 접두
                if (prefixStart >= 0) { ins.Offset = prefixStart; prefixStart = -1; }
                ins.Constrained = constrained;
                constrained = null;
                ins.Index = list.Count;
                list.Add(ins);
            }
            return list;
        }

        public static string Name(MethodBase m) => (m.DeclaringType != null ? BurstTypes.Display(m.DeclaringType) + "." : "") + m.Name;
    }
}
