using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using IlRepl.Engine;
using Mono.Cecil;
using Mono.Cecil.Cil;
using FA = Mono.Cecil.FieldAttributes;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;

namespace IlRepl.Tests.Engine;

/// <summary>
/// The REPL enforces accessibility on session members itself, because consumers skip the
/// runtime's checks for session assemblies. This harness proves the rule matches the runtime:
/// every access category is exercised from every accessor context in ordinary assemblies, and
/// the runtime's verdict on that reference pair must equal <see cref="MemberAccess"/>'s verdict.
/// The session is one logical assembly, which the pair models with InternalsVisibleTo. The
/// contexts include a derived type reaching a family member through a base-typed receiver,
/// which the runtime allows: the receiver rule of ECMA II.10.5.3 is the verifier's, not the loader's.
/// </summary>
[TestClass]
public sealed class AccessibilityEquivalenceTests
{
    private const string TargetName = "ilrepl.harness.target";
    private const string ConsumerName = "ilrepl.harness.consumer";

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

    /// <summary>
    /// The accessor contexts: where the probe lives and what receiver it uses.
    /// </summary>
    private static readonly (string Name, bool InTarget, bool Derived, bool ThisReceiver)[] Contexts =
    [
        ("same", true, false, true),
        ("nested", true, false, false),
        ("derived", false, true, true),
        ("derivedNew", false, true, false),
        ("unrelated", false, false, false),
    ];

    private static readonly string[] Kinds = ["SF", "F", "SM", "M", "NT"];

    private sealed class ConsumerContext(Assembly target) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => assemblyName.Name == TargetName ? target : null;
    }

    /// <summary>
    /// Every category from every context agrees between the runtime and the REPL's rule.
    /// </summary>
    [TestMethod]
    public void RuntimeAndReplAgree_OnEveryCategoryAndContext()
    {
        var targetContext = new AssemblyLoadContext("harness-target", isCollectible: true);
        var target = targetContext.LoadFromStream(new MemoryStream(BuildTarget()));
        var consumer = new ConsumerContext(target).LoadFromStream(new MemoryStream(BuildConsumer()));
        var t = target.GetType("T")!;
        var n = t.GetNestedType("N", BindingFlags.Public | BindingFlags.NonPublic)!;
        var derived = consumer.GetType("Derived")!;
        var unrelated = consumer.GetType("Unrelated")!;
        var mismatches = new List<string>();
        var count = 0;
        foreach (var (contextName, _, _, _) in Contexts)
        {
            var host = contextName switch
            {
                "same" => t,
                "nested" => n,
                "derived" or "derivedNew" => derived,
                _ => unrelated,
            };
            foreach (var kind in Kinds)
            {
                foreach (var (word, _, _, _) in Categories)
                {
                    count++;
                    var probe = host.GetMethod($"P_{contextName}_{kind}_{word}", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)!;
                    var runtime = RuntimeVerdict(probe, host);
                    var repl = ReplVerdict(t, host, kind, word);
                    if ((runtime is null) != (repl is null))
                    {
                        mismatches.Add($"{contextName} {kind} {word}: runtime {runtime ?? "ok"}, repl {repl ?? "ok"}");
                    }
                }
            }
        }

        Assert.AreEqual(Contexts.Length * Kinds.Length * Categories.Length, count);
        Assert.IsEmpty(mismatches, string.Join("\n", mismatches));
    }

    private static string? RuntimeVerdict(MethodInfo probe, Type host)
    {
        try
        {
            var instance = probe.IsStatic ? null : Activator.CreateInstance(host);
            probe.Invoke(instance, null);
            return null;
        }
        catch (TargetInvocationException e)
        {
            return e.InnerException!.GetType().Name;
        }
    }

    private static string? ReplVerdict(Type t, Type host, string kind, string word)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var scope = new AccessScope(host, host.Name);
        return kind switch
        {
            "SF" or "F" => MemberAccess.FieldVerdict(t.GetField($"{kind}_{word}", all)!, scope, TypeTable.Empty, judgeAll: true),
            "SM" or "M" => MemberAccess.MethodVerdict(new ResolvedMethod(t.GetMethod($"{kind}_{word}", all)!, null), scope, TypeTable.Empty, judgeAll: true),
            _ => MemberAccess.TypeVerdict(t.GetNestedType($"NT_{word}", all)!, scope, TypeTable.Empty, judgeAll: true),
        };
    }

    private static byte[] Write(AssemblyDefinition assembly)
    {
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

    private static MethodDefinition Probe(ModuleDefinition module, string name, bool isStatic)
    {
        var attributes = MA.Public | (isStatic ? MA.Static : 0);
        return new MethodDefinition(name, attributes, module.TypeSystem.Int32);
    }

    /// <summary>
    /// Emits the probe body: load the receiver (this, or a new T), then the access.
    /// </summary>
    private static void EmitProbe(MethodDefinition probe, string kind, MethodReference ctor, FieldReference? field, MethodReference? method, TypeReference? nested, bool thisReceiver)
    {
        var il = probe.Body.GetILProcessor();
        switch (kind)
        {
            case "SF":
                il.Emit(OpCodes.Ldsfld, field!);
                break;
            case "F":
                if (thisReceiver)
                {
                    il.Emit(OpCodes.Ldarg_0);
                }
                else
                {
                    il.Emit(OpCodes.Newobj, ctor);
                }

                il.Emit(OpCodes.Ldfld, field!);
                break;
            case "SM":
                il.Emit(OpCodes.Call, method!);
                break;
            case "M":
                if (thisReceiver)
                {
                    il.Emit(OpCodes.Ldarg_0);
                }
                else
                {
                    il.Emit(OpCodes.Newobj, ctor);
                }

                il.Emit(OpCodes.Call, method!);
                break;
            default:
                il.Emit(OpCodes.Call, new MethodReference("Hello", nested!.Module.TypeSystem.Int32, nested) { HasThis = false });
                break;
        }

        il.Emit(OpCodes.Ret);
    }

    private static byte[] BuildTarget()
    {
        var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(TargetName, new Version(1, 0, 0, 0)), "target", ModuleKind.Dll);
        var module = assembly.MainModule;
        var ivt = new CustomAttribute(module.ImportReference(typeof(InternalsVisibleToAttribute).GetConstructor([typeof(string)])!));
        ivt.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, ConsumerName));
        assembly.CustomAttributes.Add(ivt);

        var t = new TypeDefinition("", "T", TA.Public | TA.Class, module.ImportReference(typeof(object)));
        module.Types.Add(t);
        var ctor = new MethodDefinition(".ctor", MA.Public | MA.HideBySig | MA.SpecialName | MA.RTSpecialName, module.TypeSystem.Void);
        var ctorIl = ctor.Body.GetILProcessor();
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
        ctorIl.Emit(OpCodes.Ret);
        t.Methods.Add(ctor);

        var n = new TypeDefinition("", "N", TA.NestedPublic | TA.Class | TA.Abstract | TA.Sealed, module.ImportReference(typeof(object)));
        t.NestedTypes.Add(n);

        var fields = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        var methods = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
        var nestedTypes = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var (word, fieldAccess, methodAccess, nestedVisibility) in Categories)
        {
            var sf = new FieldDefinition("SF_" + word, fieldAccess | FA.Static, module.TypeSystem.Int32);
            var f = new FieldDefinition("F_" + word, fieldAccess, module.TypeSystem.Int32);
            t.Fields.Add(sf);
            t.Fields.Add(f);
            fields[sf.Name] = sf;
            fields[f.Name] = f;
            var sm = new MethodDefinition("SM_" + word, methodAccess | MA.Static, module.TypeSystem.Int32);
            var m = new MethodDefinition("M_" + word, methodAccess, module.TypeSystem.Int32);
            EmitReturnOne(sm);
            EmitReturnOne(m);
            t.Methods.Add(sm);
            t.Methods.Add(m);
            methods[sm.Name] = sm;
            methods[m.Name] = m;
            var nt = new TypeDefinition("", "NT_" + word, nestedVisibility | TA.Class | TA.Abstract | TA.Sealed, module.ImportReference(typeof(object)));
            var hello = new MethodDefinition("Hello", MA.Public | MA.Static, module.TypeSystem.Int32);
            EmitReturnOne(hello);
            nt.Methods.Add(hello);
            t.NestedTypes.Add(nt);
            nestedTypes[nt.Name] = nt;
        }

        foreach (var (contextName, inTarget, _, thisReceiver) in Contexts)
        {
            if (!inTarget)
            {
                continue;
            }

            var host = contextName == "same" ? t : n;
            foreach (var kind in Kinds)
            {
                foreach (var (word, _, _, _) in Categories)
                {
                    var isStatic = !(thisReceiver && kind is "F" or "M");
                    var probe = Probe(module, $"P_{contextName}_{kind}_{word}", isStatic);
                    EmitProbe(probe, kind, ctor, fields.GetValueOrDefault($"{kind}_{word}"), methods.GetValueOrDefault($"{kind}_{word}"), nestedTypes.GetValueOrDefault($"NT_{word}"), thisReceiver);
                    host.Methods.Add(probe);
                }
            }
        }

        return Write(assembly);
    }

    private static byte[] BuildConsumer()
    {
        var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(ConsumerName, new Version(1, 0, 0, 0)), "consumer", ModuleKind.Dll);
        var module = assembly.MainModule;
        var targetRef = new AssemblyNameReference(TargetName, new Version(1, 0, 0, 0));
        module.AssemblyReferences.Add(targetRef);
        var t = new TypeReference("", "T", module, targetRef);
        var ctor = new MethodReference(".ctor", module.TypeSystem.Void, t) { HasThis = true };
        var objectCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);

        TypeDefinition Host(string name, bool derived)
        {
            var type = new TypeDefinition("", name, TA.Public | TA.Class, derived ? t : module.ImportReference(typeof(object)));
            module.Types.Add(type);
            var hostCtor = new MethodDefinition(".ctor", MA.Public | MA.HideBySig | MA.SpecialName | MA.RTSpecialName, module.TypeSystem.Void);
            var il = hostCtor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, derived ? ctor : objectCtor);
            il.Emit(OpCodes.Ret);
            type.Methods.Add(hostCtor);
            return type;
        }

        var derivedHost = Host("Derived", derived: true);
        var unrelatedHost = Host("Unrelated", derived: false);
        foreach (var (contextName, inTarget, isDerived, thisReceiver) in Contexts)
        {
            if (inTarget)
            {
                continue;
            }

            var host = isDerived ? derivedHost : unrelatedHost;
            foreach (var kind in Kinds)
            {
                foreach (var (word, _, _, _) in Categories)
                {
                    var isStatic = !(thisReceiver && kind is "F" or "M");
                    var probe = Probe(module, $"P_{contextName}_{kind}_{word}", isStatic);
                    var field = kind is "SF" or "F" ? new FieldReference($"{kind}_{word}", module.TypeSystem.Int32, t) : null;
                    var method = kind is "SM" or "M" ? new MethodReference($"{kind}_{word}", module.TypeSystem.Int32, t) { HasThis = kind == "M" } : null;
                    var nested = kind == "NT" ? new TypeReference("", $"NT_{word}", module, targetRef) { DeclaringType = t } : null;
                    EmitProbe(probe, kind, ctor, field, method, nested, thisReceiver);
                    host.Methods.Add(probe);
                }
            }
        }

        return Write(assembly);
    }
}
