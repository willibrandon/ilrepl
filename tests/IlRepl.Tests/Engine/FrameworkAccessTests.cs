using System.Reflection;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using Mono.Cecil;
using Mono.Cecil.Cil;
using FA = Mono.Cecil.FieldAttributes;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Compares contextual external-member eligibility against independent runtime execution.
/// </summary>
/// <remarks>
/// A cell and a session type reach members of other assemblies only as far as the runtime lets
/// them, because their assemblies skip access checks for session assemblies alone. This harness
/// builds an assembly with every access word on fields, methods, nested types, and a generic
/// method instantiated over each nested type, references each from the cell and from a session
/// type derived from the target, and requires the rule's verdict to equal the runtime's: the
/// reference compiles and runs, or the JIT refuses it.
/// </remarks>
[TestClass]
public sealed class FrameworkAccessTests
{
    private static readonly (string Word, FA Field, MA Method, TA Nested)[] Categories =
    [
        ("public", FA.Public, MA.Public, TA.NestedPublic),
        ("assembly", FA.Assembly, MA.Assembly, TA.NestedAssembly),
        ("family", FA.Family, MA.Family, TA.NestedFamily),
        ("famandassem", FA.FamANDAssem, MA.FamANDAssem, TA.NestedFamANDAssem),
        ("famorassem", FA.FamORAssem, MA.FamORAssem, TA.NestedFamORAssem),
        ("private", FA.Private, MA.Private, TA.NestedPrivate),
        ("privatescope", FA.CompilerControlled, MA.CompilerControlled, TA.NestedPrivate),
    ];

    private static readonly string[] Kinds = ["SF", "F", "SM", "M", "NT", "GM"];

    /// <summary>
    /// Every category of every kind agrees between the runtime and the rule, from the cell and from a derived session type.
    /// </summary>
    [TestMethod]
    public void RuleAndRuntimeAgree_FromTheCellAndFromADerivedSessionType()
    {
        var name = "IlReplAccessTarget" + Guid.NewGuid().ToString("N");
        var session = new Session();
        var target = session.Resolver.LoadImage(BuildTarget(name));
        var t = target.GetType("T")!;
        foreach (var line in IlLines.Expand(".class public Derived extends [" + name + "]T {",
            ".method public instance void .ctor() { ldarg.0; call instance void [" + name + "]T::.ctor(); ret }", "}"))
        {
            session.AddLine(line);
        }

        var derivedType = session.Types.Single(x => x.FullName == "Derived").RuntimeType!;

        using var snapshot = BindingSnapshot.Capture(session);
        var scope = new SnapshotBindingScope(snapshot);
        var facts = AccessFacts.From(scope);
        var cell = AccessContext.Cell;
        var derived = new AccessContext(RuntimeSymbolImporter.Import(derivedType), "class Derived");
        var mismatches = new List<string>();
        var count = 0;
        foreach (var kind in Kinds)
        {
            foreach (var (word, _, _, _) in Categories)
            {
                foreach (var fromDerived in new[] { false, true })
                {
                    count++;
                    var runtime = RuntimeVerdict(session, name, kind, word, fromDerived, count);
                    var rule = RuleVerdict(t, kind, word, fromDerived ? derived : cell, facts);
                    if ((runtime is null) != (rule is null))
                    {
                        mismatches.Add(
                            $"{(fromDerived ? "derived" : "cell")} {kind} {word}: runtime {runtime ?? "ok"}, rule {rule ?? "ok"}");
                    }
                }
            }
        }

        Assert.AreEqual(Kinds.Length * Categories.Length * 2, count);
        Assert.IsEmpty(mismatches, string.Join("\n", mismatches));
    }

    private static string? RuleVerdict(Type t, string kind, string word, AccessContext where, AccessFacts facts)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
            | BindingFlags.DeclaredOnly;
        switch (kind)
        {
            case "SF":
            case "F":
                return MemberEligibility.AccessProblem(RuntimeSymbolImporter.Import(t.GetField($"{kind}_{word}", all)!), where, facts);
            case "SM":
            case "M":
                return MemberEligibility.AccessProblem(RuntimeSymbolImporter.Import(t.GetMethod($"{kind}_{word}", all)!), where, facts);
            case "NT":
                return MemberEligibility.AccessProblem(RuntimeSymbolImporter.Import(t.GetNestedType($"NT_{word}", all)!), where, facts);
            default:
            {
                var echo = RuntimeSymbolImporter.Import(t.GetMethod("Echo", all)!);
                var nested = RuntimeSymbolImporter.Import(t.GetNestedType($"NT_{word}", all)!);
                return MemberEligibility.AccessProblem(SymbolRelations.Instantiate(echo, echo.DeclaringType, [nested]), where, facts);
            }
        }
    }

    /// <summary>
    /// Executes a reference from a cell or derived session type and returns any runtime refusal.
    /// </summary>
    /// <remarks>
    /// Compiles and runs the reference the way a user would send it: in the cell, or in a method of
    /// a class derived from the target. Null when it ran; the failure otherwise.
    /// </remarks>
    private static string? RuntimeVerdict(Session session, string assembly, string kind, string word, bool fromDerived, int index)
    {
        var target = $"[{assembly}]T";
        string[] body = kind switch
        {
            "SF" => [$"ldsfld int32 {target}::SF_{word}"],
            "F" => fromDerived ? [$"ldarg.0", $"ldfld int32 {target}::F_{word}"] : [$"newobj instance void {target}::.ctor()",
                $"ldfld int32 {target}::F_{word}"],
            "SM" => [$"call int32 {target}::SM_{word}()"],
            "M" => fromDerived ? [$"ldarg.0", $"call instance int32 {target}::M_{word}()"] : [$"newobj instance void {target}::.ctor()",
                $"call instance int32 {target}::M_{word}()"],
            "NT" => [$"call int32 {target}/NT_{word}::Hello()"],
            _ => [$"call int32 {target}::Echo<class {target}/NT_{word}>()"],
        };
        try
        {
            if (fromDerived)
            {
                var className = $"Probe{index}";
                foreach (var line in IlLines.Expand($".class public {className} extends {target} {{",
                    $".method public instance void .ctor() {{ ldarg.0; call instance void {target}::.ctor(); ret }}"))
                {
                    session.AddLine(line);
                }

                session.AddLine(".method public instance int32 Probe() {");
                foreach (var line in body)
                {
                    session.AddLine(line);
                }

                session.AddLine("ret");
                session.AddLine("}");
                session.AddLine("}");
                session.AddLine($"newobj instance void {className}::.ctor()");
                session.AddLine($"call instance int32 {className}::Probe()");
            }
            else
            {
                foreach (var line in body)
                {
                    session.AddLine(line);
                }
            }

            session.AddLine("ret");
            var result = session.Run();
            return result.Value is int ? null : "no value";
        }
        catch (Exception ex) when (
            ex is ReplException or CellException or MemberAccessException or TypeLoadException or InvalidProgramException
                or TypeInitializationException)
        {
            session.ClearCell();
            if (session.OpenType is not null)
            {
                session.AbandonType();
            }

            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static byte[] BuildTarget(string name)
    {
        var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0, 0, 0)), "target",
            ModuleKind.Dll);
        var module = assembly.MainModule;
        var t = new TypeDefinition("", "T", TA.Public | TA.Class, module.ImportReference(typeof(object)));
        module.Types.Add(t);
        var ctor = new MethodDefinition(".ctor", MA.Public | MA.HideBySig | MA.SpecialName | MA.RTSpecialName, module.TypeSystem.Void);
        var ctorIl = ctor.Body.GetILProcessor();
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
        ctorIl.Emit(OpCodes.Ret);
        t.Methods.Add(ctor);

        var echo = new MethodDefinition("Echo", MA.Public | MA.Static, module.TypeSystem.Int32);
        echo.GenericParameters.Add(new GenericParameter("T", echo));
        EmitReturnOne(echo);
        t.Methods.Add(echo);

        foreach (var (word, fieldAccess, methodAccess, nestedVisibility) in Categories)
        {
            t.Fields.Add(new FieldDefinition("SF_" + word, fieldAccess | FA.Static, module.TypeSystem.Int32));
            t.Fields.Add(new FieldDefinition("F_" + word, fieldAccess, module.TypeSystem.Int32));
            var sm = new MethodDefinition("SM_" + word, methodAccess | MA.Static, module.TypeSystem.Int32);
            var m = new MethodDefinition("M_" + word, methodAccess, module.TypeSystem.Int32);
            EmitReturnOne(sm);
            EmitReturnOne(m);
            t.Methods.Add(sm);
            t.Methods.Add(m);
            var nt = new TypeDefinition("", "NT_" + word, nestedVisibility | TA.Class | TA.Abstract | TA.Sealed, module.ImportReference(
                typeof(object)));
            var hello = new MethodDefinition("Hello", MA.Public | MA.Static, module.TypeSystem.Int32);
            EmitReturnOne(hello);
            nt.Methods.Add(hello);
            t.NestedTypes.Add(nt);
        }

        using var stream = new MemoryStream();
        assembly.Write(stream);
        return stream.ToArray();
    }

    private static void EmitReturnOne(MethodDefinition method)
    {
        var il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
    }
}
