using System.Runtime.CompilerServices;
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

        var refusal = (ReplException?)null;
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

        var refusal = (ReplException?)null;
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

        var refusal = (ReplException?)null;
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

        var refusal = (ReplException?)null;
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
    /// A filter path returning zero cannot carry its changed receiver into the paired handler.
    /// </summary>
    /// <param name="zero">The instruction that produces the rejecting result.</param>
    /// <param name="one">The instruction that produces the accepting result.</param>
    [TestMethod]
    [DataRow("ldc.i4.0", "ldc.i4.1")]
    [DataRow("ldc.i4.s 0", "ldc.i4.s 1")]
    [DataRow("ldc.i4 0", "ldc.i4 1")]
    public async Task RejectingFilterPath_DoesNotReachPairedHandler(string zero, string one)
    {
        var lines = ControlFlowReceiverExamples.SelectiveFilterSource(zero, one);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, preview.Diagnostics,
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        session.AddLine("ldnull");
        session.AddLine("ldc.i4.1");
        session.AddLine("newobj instance void SelectiveFilterArgument::.ctor(class SelectiveFilterArgument, bool)");
        session.AddLine("ldfld int32 SelectiveFilterArgument::Value");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// A filter result outside zero and one keeps both unspecified outcomes under consideration.
    /// </summary>
    [TestMethod]
    public async Task UnspecifiedFilterResult_RemainsConservative()
    {
        var lines = ControlFlowReceiverExamples.SelectiveFilterSource(zero: "ldc.i4.2");
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.Contains(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error
            && diagnostic.Message.Contains("through this", StringComparison.Ordinal), preview.Diagnostics);
        Assert.ThrowsExactly<ReplException>(() =>
        {
            foreach (var line in lines)
            {
                session.AddLine(line);
            }
        });
    }

    /// <summary>
    /// A receiver change after filter results merge reaches the paired handler.
    /// </summary>
    [TestMethod]
    public async Task FilterWriteAfterResultMerge_ReachesPairedHandler()
    {
        var lines = ControlFlowReceiverExamples.SelectiveFilterSource(invalidateAfterMerge: true);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.Contains(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error
            && diagnostic.Message.Contains("through this", StringComparison.Ordinal), preview.Diagnostics);
        Assert.ThrowsExactly<ReplException>(() =>
        {
            foreach (var line in lines)
            {
                session.AddLine(line);
            }
        });
        Assert.IsNotNull(session.OpenMethod);
    }

    /// <summary>
    /// An int32 conversion preserves the filter decision associated with each receiver path.
    /// </summary>
    [TestMethod]
    public async Task FilterDecisionConversion_PreservesReceiverPaths()
    {
        var lines = ControlFlowReceiverExamples.SelectiveFilterSource(resultConversion: "conv.i4");
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, preview.Diagnostics,
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        Assert.IsNull(session.OpenType);
    }

    /// <summary>
    /// Local loads preserve filter paths until a writable address invalidates the stored decision.
    /// </summary>
    /// <param name="overwriteThroughAddress">Whether both paths are overwritten with an accepting result.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FilterDecisionLocal_TracksReceiverPaths(bool overwriteThroughAddress)
    {
        var lines = ControlFlowReceiverExamples.LocalFilterSource(overwriteThroughAddress);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(overwriteThroughAddress,
            preview.Diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error),
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var refusal = (ReplException?)null;
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

        Assert.AreEqual(overwriteThroughAddress, refusal is not null, refusal?.Message);
        if (overwriteThroughAddress)
        {
            Assert.Contains("through this", refusal!.Message);
        }
    }

    /// <summary>
    /// A filter retains the receiver-correlated local assigned by the protected region.
    /// </summary>
    [TestMethod]
    public async Task FilterDecisionLocal_FlowsFromTheProtectedRegion()
    {
        var lines = ControlFlowReceiverExamples.ProtectedLocalFilterSource();
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, preview.Diagnostics,
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        session.AddLine("ldnull");
        session.AddLine("ldc.i4.1");
        session.AddLine("newobj instance void ProtectedLocalFilterArgument::.ctor(class ProtectedLocalFilterArgument, bool)");
        session.AddLine("ldfld int32 ProtectedLocalFilterArgument::Value");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Argument loads preserve the receiver path associated with a stored filter decision.
    /// </summary>
    /// <param name="overwriteThroughAddress">Whether both paths are overwritten with an accepting result.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FilterDecisionArgument_TracksReceiverPaths(bool overwriteThroughAddress)
    {
        var lines = ControlFlowReceiverExamples.ArgumentFilterSource(overwriteThroughAddress);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(overwriteThroughAddress,
            preview.Diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error),
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var refusal = (ReplException?)null;
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

        Assert.AreEqual(overwriteThroughAddress, refusal is not null, refusal?.Message);
        if (overwriteThroughAddress)
        {
            Assert.Contains("through this", refusal!.Message);
            return;
        }

        session.AddLine("ldnull");
        session.AddLine("ldc.i4.1");
        session.AddLine("newobj instance void ArgumentFilterArgument::.ctor(class ArgumentFilterArgument, int32)");
        session.AddLine("ldfld int32 ArgumentFilterArgument::Value");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Correlated filter results and receiver assignments stay paired through a shared starg.
    /// </summary>
    [TestMethod]
    public async Task CorrelatedFilterReceiver_ReachesPairedHandler()
    {
        var lines = ControlFlowReceiverExamples.CorrelatedReceiverFilterSource();
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, preview.Diagnostics,
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        session.AddLine("ldnull");
        session.AddLine("ldc.i4.1");
        session.AddLine("newobj instance void CorrelatedFilterArgument::.ctor(class CorrelatedFilterArgument, bool)");
        session.AddLine("ldfld int32 CorrelatedFilterArgument::Value");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// A branch on the filter result keeps each receiver path on its feasible successor.
    /// </summary>
    /// <param name="branchOnTrue">Whether the result is tested with <c>brtrue</c> instead of <c>brfalse</c>.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FilterResultBranch_PreservesReceiverPaths(bool branchOnTrue)
    {
        var lines = ControlFlowReceiverExamples.BranchedFilterDecisionSource(branchOnTrue);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, preview.Diagnostics,
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        session.AddLine("ldnull");
        session.AddLine("ldc.i4.1");
        session.AddLine("newobj instance void BranchedFilterArgument::.ctor(class BranchedFilterArgument, bool)");
        session.AddLine("ldfld int32 BranchedFilterArgument::Value");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// A rejecting filter carries its receiver changes into clauses searched afterward.
    /// </summary>
    /// <param name="throwFromFilter">Whether the filter rejects by throwing instead of returning zero.</param>
    /// <param name="secondFilter">Whether the later clause is an accepting filter instead of a catch.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task RejectingFilterReceiver_ReachesLaterClause(bool throwFromFilter, bool secondFilter)
    {
        var lines = ControlFlowReceiverExamples.SiblingFilterSource(throwFromFilter, secondFilter);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.Contains(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error
            && diagnostic.Message.Contains("through this", StringComparison.Ordinal), preview.Diagnostics);
        Assert.ThrowsExactly<ReplException>(() =>
        {
            foreach (var line in lines)
            {
                session.AddLine(line);
            }
        });
    }

    /// <summary>
    /// Filter path tracking falls back conservatively before many diamonds can stall analysis.
    /// </summary>
    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task ManyFilterPaths_CompletesWithinThePathBound()
    {
        var lines = ControlFlowReceiverExamples.ManyFilterPathsSource(20);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, preview.Diagnostics,
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        Assert.IsNull(session.OpenType);
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

        var refusal = (ReplException?)null;
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
    /// A decoded filter excludes a changed receiver carried only by its zero result.
    /// </summary>
    [TestMethod]
    public void Disassembly_ExcludesARejectingFilterThisArgumentWrite()
    {
        var session = new Session();
        var (_, image, fixture) = CecilFixture.Build((module, type) =>
        {
            var field = new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.InitOnly, module.TypeSystem.Int32);
            type.Fields.Add(field);
            var constructor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            constructor.Parameters.Add(new ParameterDefinition(type));
            constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
            type.Methods.Add(constructor);
            var il = constructor.Body.GetILProcessor();
            var tryStart = il.Create(OpCodes.Ldnull);
            var filterStart = il.Create(OpCodes.Pop);
            var accept = il.Create(OpCodes.Ldc_I4_1);
            var result = il.Create(OpCodes.Endfilter);
            var handlerStart = il.Create(OpCodes.Pop);
            var done = il.Create(OpCodes.Ret);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!)));
            il.Append(tryStart);
            il.Append(il.Create(OpCodes.Throw));
            il.Append(filterStart);
            il.Append(il.Create(OpCodes.Ldarg_2));
            il.Append(il.Create(OpCodes.Brtrue, accept));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Starg, constructor.Body.ThisParameter));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Br, result));
            il.Append(accept);
            il.Append(result);
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
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, diagnostics,
            string.Join("; ", diagnostics.Select(diagnostic => diagnostic.Message)));
        using var oracle = new IlVerificationOracle();
        Assert.IsEmpty(oracle.Verify(image));
        var instance = fixture.GetConstructors()[0].Invoke([null, true]);
        Assert.AreEqual(1, fixture.GetField("Value")!.GetValue(instance));
    }

    /// <summary>
    /// A decoded rejecting filter carries its changed receiver into the next metadata clause.
    /// </summary>
    /// <param name="throwFromFilter">Whether the filter rejects by throwing instead of returning zero.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Disassembly_RejectingFilterReachesLaterCatch(bool throwFromFilter)
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
            var filterHandler = il.Create(OpCodes.Pop);
            var catchHandler = il.Create(OpCodes.Pop);
            var done = il.Create(OpCodes.Ret);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!)));
            il.Append(tryStart);
            il.Append(il.Create(OpCodes.Throw));
            il.Append(filterStart);
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Starg, constructor.Body.ThisParameter));
            if (throwFromFilter)
            {
                il.Append(il.Create(OpCodes.Ldnull));
                il.Append(il.Create(OpCodes.Throw));
            }

            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Endfilter));
            il.Append(filterHandler);
            il.Append(il.Create(OpCodes.Leave, done));
            il.Append(catchHandler);
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
                HandlerStart = filterHandler,
                HandlerEnd = catchHandler,
            });
            constructor.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart,
                TryEnd = filterStart,
                CatchType = module.TypeSystem.Object,
                HandlerStart = catchHandler,
                HandlerEnd = done,
            });
        }, session.Resolver);
        var constructor = fixture.GetConstructors()[0];
        var listing = MethodDisassembler.Disassemble(constructor, session);
        var diagnostics = StackAnalysis.Diagnostics(listing);
        Assert.Contains(diagnostic => diagnostic.Message.Contains("through this", StringComparison.Ordinal), diagnostics);
        var other = RuntimeHelpers.GetUninitializedObject(fixture);
        var instance = constructor.Invoke([other]);
        Assert.AreEqual(0, fixture.GetField("Value")!.GetValue(instance));
        Assert.AreEqual(1, fixture.GetField("Value")!.GetValue(other));
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
