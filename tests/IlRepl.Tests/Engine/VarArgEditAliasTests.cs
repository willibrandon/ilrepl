using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Host;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Edit aliases retain fixed and optional vararg signatures through live binding, preview, and exported call sites.
/// </summary>
[TestClass]
public sealed class VarArgEditAliasTests
{
    /// <summary>
    /// Supplies cancellation for actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Runtime and snapshot binding keep optional types and modifiers separate from the fixed parameter list.
    /// </summary>
    /// <param name="snapshot">Whether binding uses the metadata snapshot.</param>
    /// <param name="optionalCount">The number of optional call-site arguments.</param>
    /// <returns>The completed binding, export, and supported runtime assertions.</returns>
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 1)]
    [DataRow(false, 2)]
    [DataRow(true, 0)]
    [DataRow(true, 1)]
    [DataRow(true, 2)]
    public async Task Bind_VarargAlias_PreservesOptionalSignature(bool snapshot, int optionalCount)
    {
        var session = new Session();
        session.Resolver.Load(typeof(IsLong).Assembly.FullName!);
        var (assembly, image, owner) = CecilFixture.Build(DefineCounter, session.Resolver);
        session.TypeTable.MethodAliases.Add("Copy", owner.GetMethod("Read")!);
        session.ClearCell();
        using var captured = BindingSnapshot.Capture(session.InspectionContext);
        IBindingScope scope = snapshot ? new SnapshotBindingScope(captured) : new RuntimeBindingScope(session.InspectionContext);
        var optional = string.Join(", ", Enumerable.Repeat("int32 modopt(System.Runtime.CompilerServices.IsLong)", optionalCount));
        var reference = "vararg int32 Copy(int32, ..." + (optional.Length == 0 ? "" : ", " + optional) + ")";

        var bound = SymbolBinder.BindMethodReference(CilSyntaxParser.ParseMethodReference(reference), scope, wantConstructor: false);

        Assert.IsTrue(bound.Method.IsVarArg);
        Assert.HasCount(1, bound.Method.Parameters);
        Assert.HasCount(optionalCount, bound.OptionalParameterTypes!);
        Assert.HasCount(optionalCount, bound.ExactOptionalParameterTypes!);
        foreach (var parameter in bound.ExactOptionalParameterTypes!)
        {
            Assert.AreEqual(TypeSymbolKind.Modified, parameter.Kind);
            Assert.AreEqual("IsLong", parameter.Modifier!.Name);
        }

        session.AddLine("ldc.i4.s 41");
        for (var index = 0; index < optionalCount; index++)
        {
            session.AddLine("ldc.i4.7");
        }

        session.AddLine("call " + reference);
        foreach (var exportedImage in new[] { AssemblyExporter.Write(session, "vararg-alias"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            using var module = ModuleDefinition.ReadModule(new MemoryStream(exportedImage));
            var call = module.Types.Single(type => type.FullName == "IlRepl.Cell").Methods.Single(method => method.Name == "Run")
                .Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>()
                .Single(method => method.Name == "Read");
            Assert.AreEqual(MethodCallingConvention.VarArg, call.CallingConvention);
            Assert.HasCount(1 + optionalCount, call.Parameters);
            for (var index = 0; index < optionalCount; index++)
            {
                var parameter = call.Parameters[index + 1].ParameterType;
                Assert.AreEqual(index == 0, parameter.IsSentinel);
                var annotated = index == 0 ? ((SentinelType)parameter).ElementType : parameter;
                Assert.AreEqual("System.Runtime.CompilerServices.IsLong", ((OptionalModifierType)annotated).ModifierType.FullName);
            }

            if (OperatingSystem.IsWindows())
            {
                var context = new AssemblyLoadContext("vararg-alias-export", isCollectible: true);
                try
                {
                    context.LoadFromStream(new MemoryStream(image));
                    var exported = context.LoadFromStream(new MemoryStream(exportedImage));
                    Assert.AreEqual(41 + optionalCount, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                }
                finally
                {
                    context.Unload();
                }
            }
        }

        var edit = session.PrepareEdit("vararg int32 [" + assembly.GetName().Name + "]N.Fixture::Read(int32)", "Edited");
        if (OperatingSystem.IsWindows())
        {
            session.CommitEdit(edit.Name, edit.Source.Replace("add", "add\nldc.i4.1\nadd", StringComparison.Ordinal));
            session.ClearCell();
            session.AddLine("ldc.i4.s 41");
            session.AddLine("ldc.i4.7");
            session.AddLine("call vararg int32 Edited(int32, ..., int32)");
            Assert.AreEqual(43, session.Run().Value);
            foreach (var line in IlLines.Expand(".method int32 Scenario() { ldc.i4.s 41; ldc.i4.7; "
                + "call vararg int32 Edited(int32, ..., int32); ret }"))
            {
                session.AddLine(line);
            }

            var comparison = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Edited using Scenario"),
                TestContext.CancellationToken);
            Assert.AreEqual("different", comparison.Outcome, comparison.Original.Detail + "; " + comparison.Edited.Detail);
            Assert.AreEqual("42", comparison.Original.Result!.Value);
            Assert.AreEqual("43", comparison.Edited.Result!.Value);
            Assert.AreEqual("7", comparison.Edited.Invocations.Single().Inputs.Single(member => member.Name == "argument 1").Value.Value);

            var direct = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Edited (41)"),
                TestContext.CancellationToken);
            Assert.AreEqual("different", direct.Outcome, direct.Original.Detail + "; " + direct.Edited.Detail);
            Assert.AreEqual("41", direct.Original.Result!.Value);
            Assert.AreEqual("42", direct.Edited.Result!.Value);
        }
        else
        {
            Assert.IsNotEmpty(edit.Problems);
        }
    }

    /// <summary>
    /// A vararg spelling cannot change an ordinary alias's calling convention.
    /// </summary>
    [TestMethod]
    public void Bind_NonVarargAlias_RejectsVarargCallingConvention()
    {
        var session = IlLines.Load(".method int32 Read(int32 value) { ldarg.0; ret }");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);

        var exception = Assert.ThrowsExactly<ReplException>(() =>
            MemberResolver.ResolveMethod("vararg int32 Copy(int32, ..., int32)", session.InspectionContext, wantConstructor: false));

        Assert.Contains("use its declared invocation convention", exception.Message);
    }

    /// <summary>
    /// Emits a method whose result depends on its fixed input and the number of optional arguments.
    /// </summary>
    /// <param name="module">The real assembly module.</param>
    /// <param name="owner">The declaring type for the counter.</param>
    internal static void DefineCounter(ModuleDefinition module, TypeDefinition owner)
    {
        var read = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32)
        {
            CallingConvention = MethodCallingConvention.VarArg,
        };
        read.Parameters.Add(new ParameterDefinition("first", ParameterAttributes.None, module.TypeSystem.Int32));
        owner.Methods.Add(read);
        read.Body.InitLocals = true;
        read.Body.Variables.Add(new VariableDefinition(module.ImportReference(typeof(ArgIterator))));
        var il = read.Body.GetILProcessor();
        il.Emit(OpCodes.Arglist);
        il.Emit(OpCodes.Newobj, module.ImportReference(typeof(ArgIterator).GetConstructor([typeof(RuntimeArgumentHandle)])!));
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldloca_S, read.Body.Variables[0]);
        il.Emit(OpCodes.Call, module.ImportReference(typeof(ArgIterator).GetMethod(nameof(ArgIterator.GetRemainingCount))!));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);
    }
}
