using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Rechecks receiver provenance at earlier instructions when later edges complete the graph.
/// </summary>
[TestClass]
public sealed class ControlFlowReceiverTests
{
    /// <summary>
    /// Supplies cancellation for symbolic analysis.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Readonly stores require the original receiver on every incoming path in preview and live submission.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LaterEdge_RechecksEarlierStore(bool originalReceiver)
    {
        var lines = ControlFlowReceiverExamples.Source(originalReceiver);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 10, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(originalReceiver, !preview.Diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error),
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        if (!originalReceiver)
        {
            Assert.Contains(diagnostic => diagnostic.Location.Line == 10
                && diagnostic.Message.Contains("through this", StringComparison.Ordinal),
                preview.Diagnostics);
        }

        for (var line = 0; line < lines.Length; line++)
        {
            if (line == 13 && !originalReceiver)
            {
                var error = Assert.ThrowsExactly<ReplException>(() => session.AddLine(lines[line]));
                Assert.Contains("through this", error.Message);
                Assert.IsNotNull(session.OpenMethod);
                return;
            }

            session.AddLine(lines[line]);
        }

        session.AddLine("ldnull");
        session.AddLine("newobj instance void FlowReceiver::.ctor(class FlowReceiver)");
        session.AddLine("ldfld int32 FlowReceiver::Value");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Replacing argument zero removes its original-receiver provenance on direct and merged paths.
    /// </summary>
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task ArgumentWrite_ChangesReceiverProvenance(bool originalReceiver, bool branch)
    {
        var lines = ControlFlowReceiverExamples.ArgumentSource(originalReceiver, branch);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(originalReceiver, !preview.Diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error),
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));

        ReplException? refusal = null;
        foreach (var line in lines)
        {
            try
            {
                session.AddLine(line);
            }
            catch (ReplException error)
            {
                refusal = error;
                break;
            }
        }

        Assert.AreEqual(originalReceiver, refusal is null, refusal?.Message);
        if (!originalReceiver)
        {
            Assert.Contains("through this", refusal!.Message);
            Assert.IsNotNull(session.OpenMethod);
        }
    }

    /// <summary>
    /// A handler sees the receiver provenance carried into its protected region.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ArgumentWrite_ReachesExceptionHandlers(bool originalReceiver)
    {
        var lines = ControlFlowReceiverExamples.HandlerSource(originalReceiver);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(originalReceiver, !preview.Diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error),
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));

        ReplException? refusal = null;
        foreach (var line in lines)
        {
            try
            {
                session.AddLine(line);
            }
            catch (ReplException error)
            {
                refusal = error;
                break;
            }
        }

        Assert.AreEqual(originalReceiver, refusal is null, refusal?.Message);
        if (!originalReceiver)
        {
            Assert.Contains("through this", refusal!.Message);
        }
    }

    /// <summary>
    /// A leave carries receiver changes made by its finally handler to the instruction at the target.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FinallyWrite_ReachesLeaveTarget(bool originalReceiver)
    {
        var lines = ControlFlowReceiverExamples.FinallySource(originalReceiver);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 13, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(originalReceiver, !preview.Diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error),
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));

        ReplException? refusal = null;
        foreach (var line in lines)
        {
            try
            {
                session.AddLine(line);
            }
            catch (ReplException error)
            {
                refusal = error;
                break;
            }
        }

        Assert.AreEqual(originalReceiver, refusal is null, refusal?.Message);
        if (!originalReceiver)
        {
            Assert.Contains("through this", refusal!.Message);
            Assert.IsNotNull(session.OpenMethod);
        }
    }

    /// <summary>
    /// An accepting filter carries the receiver it stored into its paired handler.
    /// </summary>
    /// <param name="originalReceiver">Whether the filter stores the original receiver.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FilterWrite_ReachesPairedHandler(bool originalReceiver)
    {
        var lines = ControlFlowReceiverExamples.FilterSource(originalReceiver);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 18, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(originalReceiver, !preview.Diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error),
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));

        ReplException? refusal = null;
        foreach (var line in lines)
        {
            try
            {
                session.AddLine(line);
            }
            catch (ReplException error)
            {
                refusal = error;
                break;
            }
        }

        Assert.AreEqual(originalReceiver, refusal is null, refusal?.Message);
        if (!originalReceiver)
        {
            Assert.Contains("through this", refusal!.Message);
            Assert.IsNotNull(session.OpenMethod);
        }
    }

    /// <summary>
    /// Completing a nested finally does not make a noncompleting outer finally reach its leave target.
    /// </summary>
    [TestMethod]
    public async Task NestedFinallyEnd_DoesNotCompleteOuterFinally()
    {
        var lines = ControlFlowReceiverExamples.NestedNonCompletingFinallySource();
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 19, 0, 1), TestContext.CancellationToken);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, preview.Diagnostics,
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        Assert.IsNull(session.OpenMethod);
        Assert.IsNull(session.OpenType);
    }

    /// <summary>
    /// Exposing argument zero by address invalidates this for every supported indirect write form.
    /// </summary>
    [TestMethod]
    [DataRow("stind.ref")]
    [DataRow("stobj")]
    [DataRow("initobj")]
    [DataRow("cpobj")]
    [DataRow("initblk")]
    [DataRow("cpblk")]
    public async Task ArgumentAddress_InvalidatesReceiverProvenance(string write)
    {
        var lines = ControlFlowReceiverExamples.AddressSource(write);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.Contains(diagnostic => diagnostic.Message.Contains("through this", StringComparison.Ordinal), preview.Diagnostics);

        ReplException? refusal = null;
        foreach (var line in lines)
        {
            try
            {
                session.AddLine(line);
            }
            catch (ReplException error)
            {
                refusal = error;
                break;
            }
        }

        Assert.Contains("through this", refusal!.Message);
        Assert.IsNotNull(session.OpenMethod);
    }

    /// <summary>
    /// Decoded constructors use the same readonly receiver rule as live and symbolic source bodies.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Disassembly_ChecksTheReadonlyReceiver(bool originalReceiver)
    {
        var session = new Session();
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var field = new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.InitOnly, module.TypeSystem.Int32);
            type.Fields.Add(field);
            var constructor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            constructor.Parameters.Add(new ParameterDefinition(type));
            type.Methods.Add(constructor);
            var il = constructor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            il.Emit(originalReceiver ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);
        }, session.Resolver);
        var listing = MethodDisassembler.Disassemble(fixture.GetConstructors()[0], session);
        var diagnostics = StackAnalysis.Diagnostics(listing);
        Assert.AreEqual(originalReceiver, !diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error));
        if (!originalReceiver)
        {
            Assert.Contains(diagnostic => diagnostic.Message.Contains("through this", StringComparison.Ordinal), diagnostics);
        }
    }

    /// <summary>
    /// Decoded argument writes invalidate this before a later readonly field store is analyzed.
    /// </summary>
    [TestMethod]
    public void Disassembly_TracksAnOverwrittenThisArgument()
    {
        var session = new Session();
        var (_, image, fixture) = CecilFixture.Build((module, type) =>
        {
            var field = new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.InitOnly, module.TypeSystem.Int32);
            type.Fields.Add(field);
            var constructor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            constructor.Parameters.Add(new ParameterDefinition(type));
            type.Methods.Add(constructor);
            var il = constructor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Starg, constructor.Body.ThisParameter);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);
        }, session.Resolver);
        var listing = MethodDisassembler.Disassemble(fixture.GetConstructors()[0], session);
        var diagnostics = StackAnalysis.Diagnostics(listing);
        Assert.Contains(diagnostic => diagnostic.Message.Contains("through this", StringComparison.Ordinal), diagnostics);
        using var oracle = new IlVerificationOracle();
        Assert.IsEmpty(oracle.Verify(image));
    }

    /// <summary>
    /// A decoded filter carries its argument write into the paired handler.
    /// </summary>
    [TestMethod]
    public void Disassembly_TracksAFilterThisArgumentWrite()
    {
        var session = new Session();
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var field = new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.InitOnly, module.TypeSystem.Int32);
            type.Fields.Add(field);
            var constructor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            constructor.Parameters.Add(new ParameterDefinition(type));
            type.Methods.Add(constructor);
            var il = constructor.Body.GetILProcessor();
            var tryStart = il.Create(OpCodes.Ldnull);
            var filterStart = il.Create(OpCodes.Pop);
            var handlerStart = il.Create(OpCodes.Pop);
            var done = il.Create(OpCodes.Ret);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!)));
            il.Append(tryStart);
            il.Append(il.Create(OpCodes.Throw));
            il.Append(filterStart);
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Starg, constructor.Body.ThisParameter));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Endfilter));
            il.Append(handlerStart);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Stfld, field));
            il.Append(il.Create(OpCodes.Leave, done));
            il.Append(done);
            constructor.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Filter)
            {
                TryStart = tryStart,
                TryEnd = filterStart,
                FilterStart = filterStart,
                HandlerStart = handlerStart,
                HandlerEnd = done,
            });
        }, session.Resolver);
        var listing = MethodDisassembler.Disassemble(fixture.GetConstructors()[0], session);
        var diagnostics = StackAnalysis.Diagnostics(listing);
        Assert.Contains(diagnostic => diagnostic.Message.Contains("through this", StringComparison.Ordinal), diagnostics);
    }

    /// <summary>
    /// Decoded writable addresses invalidate this even when ILVerification retains its receiver tag.
    /// </summary>
    [TestMethod]
    public void Disassembly_TracksAnIndirectThisArgumentWrite()
    {
        var session = new Session();
        var (_, image, fixture) = CecilFixture.Build((module, type) =>
        {
            var field = new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.InitOnly, module.TypeSystem.Int32);
            type.Fields.Add(field);
            var constructor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            constructor.Parameters.Add(new ParameterDefinition(type));
            type.Methods.Add(constructor);
            var il = constructor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            il.Emit(OpCodes.Ldarga, constructor.Body.ThisParameter);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stind_Ref);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);
        }, session.Resolver);
        var listing = MethodDisassembler.Disassemble(fixture.GetConstructors()[0], session);
        var diagnostics = StackAnalysis.Diagnostics(listing);
        Assert.Contains(diagnostic => diagnostic.Message.Contains("through this", StringComparison.Ordinal), diagnostics);
        using var oracle = new IlVerificationOracle();
        Assert.IsEmpty(oracle.Verify(image));
    }
}
