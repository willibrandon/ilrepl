using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Host;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Non-public vararg edits retain optional arguments without changing copied member visibility.
/// </summary>
[TestClass]
public sealed class PrivateVarArgEditTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Runtime and snapshot binding recognize private aliases while qualified member references retain ordinary access checks.
    /// </summary>
    /// <param name="nested">Whether the selected method belongs to a private nested type.</param>
    /// <param name="isPublic">Whether the selected method itself is public.</param>
    /// <param name="snapshot">Whether the alias is bound from a metadata snapshot.</param>
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, true)]
    [DataRow(true, false, true)]
    [DataRow(true, true, true)]
    public void Bind_PrivateVarargAliasRetainsAccess(bool nested, bool isPublic, bool snapshot)
    {
        var session = new Session();
        var selected = DefineOriginal(session, nested, isPublic);
        var family = ImportedMethodFamily.Capture("Copy", selected, session);
        var writer = new CecilWriter(SessionAssemblyKind.Types);
        var definitions = family.Write(writer);
        var definition = writer.Load();
        var method = definition.Assembly.ManifestModule.ResolveMethod(definitions[family.Selected.Method].MetadataToken.ToInt32())!;
        foreach (var type in definition.Assembly.GetTypes())
        {
            session.TypeTable.Add(type.FullName!.Replace('+', '/'), type);
        }

        session.TypeTable.MethodAliases.Add("Copy", method);
        session.ClearCell();
        const string call = "vararg int32 Copy(int32, ..., int32, int64)";
        using var captured = BindingSnapshot.Capture(session.InspectionContext);
        IBindingScope scope = snapshot ? new SnapshotBindingScope(captured) : new RuntimeBindingScope(session.InspectionContext);
        var bound = SymbolBinder.BindMethodReference(CilSyntaxParser.ParseMethodReference(call), scope, wantConstructor: false);
        Assert.IsTrue(bound.IsAlias);
        Assert.HasCount(2, bound.OptionalParameterTypes!);
        var resolved = MemberResolver.ResolveMethod(call, session.InspectionContext, wantConstructor: false);
        Assert.IsTrue(resolved.IsAlias);
        Assert.IsNull(MemberAccess.MethodVerdict(resolved, new AccessScope(null, "method Scenario"), session.TypeTable));
        Assert.IsNotNull(MemberAccess.MethodVerdict(new ResolvedMethod(method, null), AccessScope.Cell, session.TypeTable));
        foreach (var line in new[] { "ldc.i4.s 41", "ldc.i4.7", "ldc.i8 9", "call " + call })
        {
            session.AddLine(line);
        }

        Assert.AreEqual(1, session.State.Stack.Count);
    }

    /// <summary>
    /// Exported private vararg definitions remain direct call targets and grant access within the standalone assembly.
    /// </summary>
    /// <param name="nested">Whether the selected method belongs to a private nested type.</param>
    /// <param name="isPublic">Whether the selected method itself is public.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void Export_PrivateVarargPreservesCallsAndAccess(bool nested, bool isPublic)
    {
        var session = new Session();
        var selected = DefineOriginal(session, nested, isPublic);
        var family = ImportedMethodFamily.Capture("Copy", selected, session);
        var writer = new CecilWriter("private-vararg-export");
        var definitions = family.Write(writer);
        var target = (MethodDefinition)definitions[family.Selected.Method];
        Assert.AreEqual(isPublic, target.IsPublic);
        Assert.AreEqual(nested, target.DeclaringType.IsNestedPrivate);
        Assert.AreEqual(MethodCallingConvention.VarArg, target.CallingConvention);
        Assert.HasCount(1, target.DeclaringType.Methods);

        var probe = new MethodDefinition("AccessProbe", MethodAttributes.Private | MethodAttributes.Static, writer.Module.TypeSystem.Int32);
        target.DeclaringType.Methods.Add(probe);
        probe.Body.GetILProcessor().Emit(OpCodes.Ldc_I4, 42);
        probe.Body.GetILProcessor().Emit(OpCodes.Ret);
        var owner = new TypeDefinition("N", "Caller", TypeAttributes.Public, writer.Object);
        writer.Module.Types.Add(owner);
        var access = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, writer.Module.TypeSystem.Int32);
        owner.Methods.Add(access);
        access.Body.GetILProcessor().Emit(OpCodes.Call, probe);
        access.Body.GetILProcessor().Emit(OpCodes.Ret);

        var scenario = new MethodDefinition("Scenario", MethodAttributes.Public | MethodAttributes.Static, writer.Module.TypeSystem.Int32);
        owner.Methods.Add(scenario);
        var call = new MethodReference(target.Name, target.ReturnType, target.DeclaringType)
        {
            CallingConvention = MethodCallingConvention.VarArg,
        };

        call.Parameters.Add(new ParameterDefinition(writer.Module.TypeSystem.Int32));
        call.Parameters.Add(new ParameterDefinition(new SentinelType(writer.Module.TypeSystem.Int32)));
        call.Parameters.Add(new ParameterDefinition(writer.Module.TypeSystem.Int64));
        var il = scenario.Body.GetILProcessor();
        il.Emit(OpCodes.Ldc_I4, 41);
        il.Emit(OpCodes.Ldc_I4_7);
        il.Emit(OpCodes.Ldc_I8, 9L);
        il.Emit(OpCodes.Call, call);
        il.Emit(OpCodes.Ret);
        var image = writer.Write();
        using var moduleStream = new MemoryStream(image);
        using var module = ModuleDefinition.ReadModule(moduleStream);
        var exportedCall = (MethodReference)module.GetType("N.Caller").Methods.Single(method => method.Name == "Scenario")
            .Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Call).Operand;
        Assert.AreEqual(MethodCallingConvention.VarArg, exportedCall.CallingConvention);
        Assert.AreEqual(target.DeclaringType.FullName, exportedCall.DeclaringType.FullName);
        Assert.HasCount(3, exportedCall.Parameters);
        Assert.IsTrue(exportedCall.Parameters[1].ParameterType.IsSentinel);
        Assert.AreEqual(MetadataType.Int64, exportedCall.Parameters[2].ParameterType.MetadataType);

        var context = new AssemblyLoadContext("private-vararg-export-" + Guid.NewGuid(), isCollectible: true);
        try
        {
            var assembly = context.LoadImage(image);
            Assert.AreEqual(42, assembly.GetType("N.Caller")!.GetMethod("Read")!.Invoke(null, null));
            if (OperatingSystem.IsWindows())
            {
                Assert.AreEqual(43, assembly.GetType("N.Caller")!.GetMethod("Scenario")!.Invoke(null, null));
            }
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Aliases, rebuilt callers, standalone exports, and comparison observations retain every optional argument on supported runtimes.
    /// </summary>
    /// <param name="nested">Whether the selected method belongs to a private nested type.</param>
    /// <param name="isPublic">Whether the selected method itself is public.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Alias_PrivateVarargRunsExportsAndCompares(bool nested, bool isPublic)
    {
        var session = new Session();
        var selected = DefineOriginal(session, nested, isPublic);
        var edit = session.PrepareEdit("vararg int32 [" + selected.Module.Assembly.GetName().Name + "]"
            + selected.DeclaringType!.FullName!.Replace('+', '/') + "::Read(int32)", "Copy");
        if (!OperatingSystem.IsWindows())
        {
            Assert.IsNotEmpty(edit.Problems);
            return;
        }

        session.CommitEdit(edit.Name, edit.Source.Replace("add", "add\nldc.i4.1\nadd", StringComparison.Ordinal));
        Assert.AreEqual(isPublic, edit.Method!.IsPublic);
        Assert.AreEqual(nested, edit.Method.DeclaringType!.IsNestedPrivate);
        foreach (var line in IlLines.Expand(".method int32 Scenario() { ldc.i4.s 41; ldc.i4.7; ldc.i8 9; "
            + "call vararg int32 Copy(int32, ..., int32, int64); ret }"))
        {
            session.AddLine(line);
        }

        session.AddLine("call Scenario");
        foreach (var image in new[] { AssemblyExporter.Write(session, "private-vararg"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("private-vararg-" + Guid.NewGuid(), isCollectible: true);
            try
            {
                var assembly = context.LoadImage(image);
                Assert.AreEqual(44, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }

        Assert.AreEqual(44, session.Run().Value);
        var compared = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", compared.Outcome, compared.Original.Detail + "; " + compared.Edited.Detail);
        Assert.AreEqual("43", compared.Original.Result!.Value);
        Assert.AreEqual("44", compared.Edited.Result!.Value);
        foreach (var side in new[] { compared.Original, compared.Edited })
        {
            var inputs = side.Invocations.Single().Inputs;
            Assert.AreEqual("7", inputs.Single(member => member.Name == "argument 1").Value.Value);
            Assert.AreEqual("9", inputs.Single(member => member.Name == "argument 2").Value.Value);
        }

        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.1", "ldc.i4.2", StringComparison.Ordinal));
        session.AddLine("call Scenario");
        Assert.AreEqual(45, session.Run().Value);
    }

    private static MethodInfo DefineOriginal(Session session, bool nested, bool isPublic)
    {
        var (_, _, owner) = CecilFixture.Build((module, root) =>
        {
            var declaring = root;
            if (nested)
            {
                declaring = new TypeDefinition("", "Hidden", TypeAttributes.NestedPrivate, module.TypeSystem.Object);
                root.NestedTypes.Add(declaring);
            }

            VarArgEditAliasTests.DefineCounter(module, declaring);
            declaring.Methods.Single().Attributes = (isPublic ? MethodAttributes.Public : MethodAttributes.Private)
                | MethodAttributes.Static;
        }, session.Resolver);

        return (nested ? owner.GetNestedType("Hidden", BindingFlags.NonPublic)! : owner)
            .GetMethod("Read", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
    }
}
