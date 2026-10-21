using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace NovaEngine.Burst.Analyzers
{
    /// <summary>
    /// [BurstCompile] 코드의 HPC# (High Performance C#) 규칙 — Unity Burst 가 거부하는 것과 같은 것을 컴파일 때 경고한다.
    /// 뿌리: [BurstCompile] struct 의 Execute (잡) · [BurstCompile] 클래스의 [BurstCompile] 정적 메서드. 거기서 부르는 같은 프로젝트의
    /// 메서드도 따라가 본다 (Burst 도 부르는 코드를 함께 컴파일한다). 경고만 — 코드는 그대로 JIT 로 돈다 (2 단계에서 네이티브로 못 옮기는 이유).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class BurstAnalyzer : DiagnosticAnalyzer
    {
        const string Category = "Burst";
        static readonly string[] kEndTags = { WellKnownDiagnosticTags.CompilationEnd };

        static DiagnosticDescriptor Rule(string id, string title, string format, bool end = true) =>
            new DiagnosticDescriptor(id, title, format, Category, DiagnosticSeverity.Warning, true, customTags: end ? kEndTags : new string[0]);

        internal static readonly DiagnosticDescriptor ManagedObject = Rule("NB1001", "Managed object in Burst code", "Burst: creating the managed object '{0}' is not supported in [BurstCompile] code (GC allocation){1}");
        internal static readonly DiagnosticDescriptor ManagedArray = Rule("NB1002", "Managed array in Burst code", "Burst: the managed array 'new {0}' is not supported in [BurstCompile] code - use NativeArray<T> (Allocator.Temp) or stackalloc{1}");
        internal static readonly DiagnosticDescriptor Boxing = Rule("NB1003", "Boxing in Burst code", "Burst: boxing '{0}' to '{1}' is not supported in [BurstCompile] code{2}");
        internal static readonly DiagnosticDescriptor Delegate = Rule("NB1004", "Delegate in Burst code", "Burst: delegates and lambdas are not supported in [BurstCompile] code{0}");
        internal static readonly DiagnosticDescriptor StaticField = Rule("NB1005", "Static field in Burst code", "Burst: accessing the non-readonly static field '{0}' is not supported in [BurstCompile] code - use SharedStatic<T> or pass the value in{1}");
        internal static readonly DiagnosticDescriptor TryCatch = Rule("NB1006", "try/catch in Burst code", "Burst: try/catch is not supported in [BurstCompile] code (try/finally is){0}");
        internal static readonly DiagnosticDescriptor ManagedMember = Rule("NB1007", "Managed type in Burst code", "Burst: '{0}' uses the managed type '{1}' - not supported in [BurstCompile] code{2}");
        internal static readonly DiagnosticDescriptor StringBuild = Rule("NB1008", "String in Burst code", "Burst: building strings is not supported in [BurstCompile] code (only literals and interpolation in Debug.Log and exceptions) - use FixedString{0}");
        internal static readonly DiagnosticDescriptor Lock = Rule("NB1009", "lock in Burst code", "Burst: the lock statement is not supported in [BurstCompile] code - use Interlocked{0}");
        internal static readonly DiagnosticDescriptor ManagedField = Rule("NB1010", "Managed field in a Burst job", "Burst: the field '{0}' of the [BurstCompile] struct '{1}' has the managed type '{2}' - jobs may only hold unmanaged data (NativeArray<T>, NativeReference<T>, ...)", false);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(ManagedObject, ManagedArray, Boxing, Delegate, StaticField, TryCatch, ManagedMember, StringBuild, Lock, ManagedField);

        sealed class Finding
        {
            public Location Location;
            public DiagnosticDescriptor Rule;
            public object[] Args;   // 마지막 칸에 " (reached from ...)" 을 붙인다
        }

        sealed class MethodFacts
        {
            public readonly List<Finding> Findings = new List<Finding>();
            public readonly HashSet<IMethodSymbol> Callees = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        }

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                INamedTypeSymbol burst = start.Compilation.GetTypeByMetadataName("NovaEngine.Burst.BurstCompileAttribute");
                if (burst == null) return;
                INamedTypeSymbol exception = start.Compilation.GetTypeByMetadataName("System.Exception");
                var facts = new ConcurrentDictionary<IMethodSymbol, MethodFacts>(SymbolEqualityComparer.Default);

                start.RegisterSymbolAction(s => CheckJobFields((INamedTypeSymbol)s.Symbol, burst, s.ReportDiagnostic), SymbolKind.NamedType);

                start.RegisterOperationBlockStartAction(block =>
                {
                    if (!(block.OwningSymbol is IMethodSymbol method)) return;
                    MethodFacts f = facts.GetOrAdd(method.OriginalDefinition, _ => new MethodFacts());
                    block.RegisterOperationAction(o => Inspect(o.Operation, f, exception),
                        OperationKind.ObjectCreation, OperationKind.ArrayCreation, OperationKind.Conversion, OperationKind.DelegateCreation,
                        OperationKind.FieldReference, OperationKind.Try, OperationKind.Invocation, OperationKind.PropertyReference,
                        OperationKind.Binary, OperationKind.InterpolatedString, OperationKind.Lock);
                });

                start.RegisterCompilationEndAction(end =>
                {
                    // 뿌리에서 닿는 메서드의 발견만 알린다
                    var reached = new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default);
                    var queue = new Queue<IMethodSymbol>();
                    foreach (IMethodSymbol m in facts.Keys)
                        if (IsRoot(m, burst)) { reached[m] = null; queue.Enqueue(m); }
                    while (queue.Count > 0)
                    {
                        IMethodSymbol m = queue.Dequeue();
                        if (!facts.TryGetValue(m, out MethodFacts f)) continue;
                        string root = reached[m] ?? Display(m);
                        IMethodSymbol[] callees;
                        lock (f) callees = f.Callees.ToArray();
                        foreach (IMethodSymbol c in callees)
                            if (!reached.ContainsKey(c)) { reached[c] = root; queue.Enqueue(c); }
                    }
                    foreach (var kv in reached)
                    {
                        if (!facts.TryGetValue(kv.Key, out MethodFacts f)) continue;
                        string suffix = kv.Value == null ? "" : " (reached from " + kv.Value + ")";
                        Finding[] list;
                        lock (f) list = f.Findings.ToArray();
                        foreach (Finding x in list)
                        {
                            var args = new object[x.Args.Length + 1];
                            x.Args.CopyTo(args, 0);
                            args[args.Length - 1] = suffix;
                            end.ReportDiagnostic(Diagnostic.Create(x.Rule, x.Location, args));
                        }
                    }
                });
            });
        }

        static string Display(IMethodSymbol m) => m.ContainingType.Name + "." + m.Name;

        static bool HasAttr(ISymbol s, INamedTypeSymbol attr) =>
            s.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attr));

        static bool IsRoot(IMethodSymbol m, INamedTypeSymbol burst)
        {
            INamedTypeSymbol t = m.ContainingType;
            if (t == null || !HasAttr(t, burst)) return false;
            if (t.TypeKind == TypeKind.Struct && !m.IsStatic && m.Name == "Execute") return true;   // 잡
            return m.IsStatic && HasAttr(m, burst);                                                 // 함수 포인터
        }

        static void CheckJobFields(INamedTypeSymbol type, INamedTypeSymbol burst, System.Action<Diagnostic> report)
        {
            if (type.TypeKind != TypeKind.Struct || !HasAttr(type, burst)) return;
            foreach (ISymbol member in type.GetMembers())
                if (member is IFieldSymbol field && !field.IsStatic && !field.IsConst && field.Type.IsReferenceType && field.Locations.Length > 0 && field.Locations[0].IsInSource)
                    report(Diagnostic.Create(ManagedField, field.Locations[0], field.Name, type.Name, field.Type.ToDisplayString()));
        }

        static void Add(MethodFacts f, IOperation op, DiagnosticDescriptor rule, params object[] args)
        {
            lock (f) f.Findings.Add(new Finding { Location = op.Syntax.GetLocation(), Rule = rule, Args = args });
        }

        // throw 식 안, 또는 Debug.Log* · 예외 생성의 인자 안이면 문자열 · 예외 객체를 허락 (Burst 와 같다)
        static bool InAllowedStringContext(IOperation op, INamedTypeSymbol exception)
        {
            for (IOperation p = op.Parent; p != null; p = p.Parent)
            {
                if (p is IThrowOperation) return true;
                if (p is IInvocationOperation inv && IsDebugLog(inv.TargetMethod)) return true;
                if (p is IObjectCreationOperation oc && DerivesFrom(oc.Type, exception)) return true;
                if (p is IBlockOperation) break;   // 문장 밖으로는 볼 것 없다
            }
            return false;
        }

        static bool IsDebugLog(IMethodSymbol m) =>
            m.ContainingType != null && m.ContainingType.Name == "Debug" && m.ContainingType.ContainingNamespace?.ToDisplayString() == "NovaEngine";

        static bool DerivesFrom(ITypeSymbol t, INamedTypeSymbol baseType)
        {
            for (ITypeSymbol x = t; x != null; x = x.BaseType)
                if (SymbolEqualityComparer.Default.Equals(x, baseType)) return true;
            return false;
        }

        static bool IsManaged(ITypeSymbol t) => t != null && t.IsReferenceType && t.TypeKind != TypeKind.Error;

        static void Inspect(IOperation op, MethodFacts f, INamedTypeSymbol exception)
        {
            // 메서드에 붙은 특성 ([BurstCompile] 등) 의 인자는 코드가 아니다
            if (op.Syntax.FirstAncestorOrSelf<Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax>() != null) return;
            switch (op)
            {
                case IObjectCreationOperation oc:
                    if (IsManaged(oc.Type) && !(DerivesFrom(oc.Type, exception) && InAllowedStringContext(oc, exception)))
                        Add(f, op, ManagedObject, oc.Type.ToDisplayString());
                    break;
                case IArrayCreationOperation ac:
                    Add(f, op, ManagedArray, ac.Type is IArrayTypeSymbol at ? at.ElementType.ToDisplayString() + "[]" : "T[]");
                    break;
                case IConversionOperation cv:
                    // 문자열 잇기 안의 박싱은 NB1008 하나로
                    if (cv.Operand?.Type != null && cv.Operand.Type.IsValueType && IsManaged(cv.Type) && cv.GetConversion().IsBoxing && !InAllowedStringContext(cv, exception)
                        && !(cv.Parent is IBinaryOperation sb && sb.Type?.SpecialType == SpecialType.System_String))
                        Add(f, op, Boxing, cv.Operand.Type.ToDisplayString(), cv.Type.ToDisplayString());
                    break;
                case IDelegateCreationOperation _:
                    Add(f, op, Delegate);
                    break;
                case IFieldReferenceOperation fr:
                    if (fr.Field.IsStatic && !fr.Field.IsReadOnly && !fr.Field.IsConst)
                        Add(f, op, StaticField, fr.Field.ContainingType.Name + "." + fr.Field.Name);
                    else if (fr.Instance != null && IsManaged(fr.Instance.Type))
                        Add(f, op, ManagedMember, fr.Field.ContainingType.Name + "." + fr.Field.Name, fr.Instance.Type.ToDisplayString());
                    break;
                case ITryOperation tr:
                    if (tr.Catches.Length > 0) Add(f, op, TryCatch);
                    break;
                case IInvocationOperation inv:
                {
                    IMethodSymbol m = inv.TargetMethod;
                    if (m.OriginalDefinition.Locations.Any(l => l.IsInSource))
                        lock (f) f.Callees.Add(m.OriginalDefinition);
                    if (IsDebugLog(m) || InAllowedStringContext(inv, exception)) break;
                    if (inv.Instance != null && IsManaged(inv.Instance.Type))
                        Add(f, op, ManagedMember, Display(m), inv.Instance.Type.ToDisplayString());
                    else if (IsManaged(m.ReturnType) && !m.ReturnsVoid)
                        Add(f, op, ManagedMember, Display(m), m.ReturnType.ToDisplayString());
                    else
                        foreach (IParameterSymbol p in m.Parameters)
                            if (IsManaged(p.Type)) { Add(f, op, ManagedMember, Display(m), p.Type.ToDisplayString()); break; }
                    break;
                }
                case IPropertyReferenceOperation pr:
                    if (pr.Instance != null && IsManaged(pr.Instance.Type))
                        Add(f, op, ManagedMember, pr.Property.ContainingType.Name + "." + pr.Property.Name, pr.Instance.Type.ToDisplayString());
                    else if (pr.Instance == null && IsManaged(pr.Property.Type))
                        Add(f, op, ManagedMember, pr.Property.ContainingType.Name + "." + pr.Property.Name, pr.Property.Type.ToDisplayString());
                    break;
                case IBinaryOperation bin:
                    if (bin.Type?.SpecialType == SpecialType.System_String && !InAllowedStringContext(bin, exception) && !(bin.Parent is IBinaryOperation pb && pb.Type?.SpecialType == SpecialType.System_String))
                        Add(f, op, StringBuild);
                    break;
                case IInterpolatedStringOperation _:
                    if (!InAllowedStringContext(op, exception)) Add(f, op, StringBuild);
                    break;
                case ILockOperation _:
                    Add(f, op, Lock);
                    break;
            }
        }
    }
}
