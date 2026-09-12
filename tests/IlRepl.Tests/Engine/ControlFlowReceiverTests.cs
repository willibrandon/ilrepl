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
    /// A non-throwing sizeof instruction does not make its surrounding catch reachable.
    /// </summary>
    [TestMethod]
    public async Task Sizeof_DoesNotReachExceptionHandler()
    {
        var lines = ControlFlowReceiverExamples.NonThrowingSizeofSource();
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
    /// Exception unwinding applies nested finally and fault effects before entering an outer handler.
    /// </summary>
    /// <param name="originalReceiver">Whether the unwind handler stores the original receiver.</param>
    /// <param name="fault">Whether the unwind handler is a fault instead of a finally.</param>
    /// <param name="filter">Whether a filter selects the outer handler.</param>
    [TestMethod]
    [DataRow(true, false, false)]
    [DataRow(false, false, false)]
    [DataRow(true, true, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, true)]
    [DataRow(false, false, true)]
    [DataRow(true, true, true)]
    [DataRow(false, true, true)]
    public async Task ExceptionUnwind_ReachesOuterHandler(bool originalReceiver, bool fault, bool filter)
    {
        var lines = ControlFlowReceiverExamples.ExceptionUnwindSource(originalReceiver, fault, filter);
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
    /// An unwind handler that cannot complete keeps the selected outer catch unreachable.
    /// </summary>
    /// <param name="fault">Whether the noncompleting handler is a fault instead of a finally.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NonCompletingExceptionUnwind_DoesNotReachOuterHandler(bool fault)
    {
        var lines = ControlFlowReceiverExamples.ExceptionUnwindSource(false, fault, false, completes: false);
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
    /// Nested finalizer effects collapse conservatively before their receiver paths multiply past the bound.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ManyFinalizerPaths_CompletesWithinThePathBound()
    {
        var lines = ControlFlowReceiverExamples.BoundedFinalizerPathsSource();
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
    /// More pending unwind contexts than the path limit retain their receiver-to-finalizer correlation.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ManyUnwindContexts_RetainReceiverCorrelation()
    {
        var lines = ControlFlowReceiverExamples.BoundedUnwindContextsSource();
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
    /// More finalizer receiver sources than the path limit remain distinct until applied to the caller.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ManyFinalizerSources_RetainReceiverProvenance()
    {
        var lines = ControlFlowReceiverExamples.BoundedFinalizerSourcesSource();
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
    /// Overflow compression binds each receiver mapping to its distinct unwind effect before merging.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ManyDistinctUnwindEffects_RetainReceiverCorrelation()
    {
        var lines = ControlFlowReceiverExamples.BoundedDistinctUnwindEffectsSource();
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
    /// Widened finalizer effects invalidate locals that could restore a replaced receiver.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WidenedFinalizerLocal_DoesNotRestoreStaleReceiver()
    {
        var lines = ControlFlowReceiverExamples.WidenedFinalizerLocalSource(129);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(
            new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.Contains(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error
            && diagnostic.Message.Contains("through this", StringComparison.Ordinal), preview.Diagnostics);
    }

    /// <summary>
    /// Handler-entry receiver states stay exact when exceptional paths cross the detailed path limit.
    /// </summary>
    /// <param name="paths">The number of exceptional paths and inner finalizers.</param>
    [TestMethod]
    [DataRow(64)]
    [DataRow(65)]
    [DataRow(66)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public void ManyUnwindHandlerEntries_RetainReceiverCorrelation(int paths)
    {
        var session = new Session();
        var constructor = BuildUnwindHandlerEntryFixture(paths, session).GetConstructors()[0];
        var listing = MethodDisassembler.Disassemble(constructor, session);
        var diagnostics = StackAnalysis.Diagnostics(listing);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error,
            diagnostics, string.Join("; ", diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    /// <summary>
    /// A collapsed receiver source never proves equality when it also contains an unknown value.
    /// </summary>
    /// <param name="cases">The number of switch paths feeding the comparison.</param>
    [TestMethod]
    [DataRow(317)]
    [DataRow(318)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public void BoundedUnknownEquality_DoesNotHideUnsafeReceiver(int cases)
    {
        var session = new Session();
        var constructor = BuildUnknownEqualityFixture(cases, session).GetConstructors()[0];
        var listing = MethodDisassembler.Disassemble(constructor, session);
        var diagnostics = StackAnalysis.Diagnostics(listing);
        Assert.Contains(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error
            && diagnostic.Message.Contains("through this", StringComparison.Ordinal),
            diagnostics);
    }

    /// <summary>
    /// Mutually exclusive switch paths retain their receiver mapping beyond the detailed path limit.
    /// </summary>
    /// <param name="cases">The number of explicit switch cases.</param>
    [TestMethod]
    [DataRow(63)]
    [DataRow(64)]
    [DataRow(65)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ManyCorrelatedSwitchPaths_RetainReceiverMapping(int cases)
    {
        var lines = ControlFlowReceiverExamples.BoundedCorrelatedFinalizerSource(cases);
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
    /// Analysis stays responsive when mutually exclusive switch paths exceed the correlation bound.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ExcessCorrelatedSwitchPaths_CompleteWithinTheBound()
    {
        var lines = ControlFlowReceiverExamples.BoundedCorrelatedFinalizerSource(192);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(1, preview.DocumentVersion);
    }

    /// <summary>
    /// Jointly exclusive conditions retain their receiver mapping in the bounded overflow slot.
    /// </summary>
    /// <param name="matching">Whether every joint condition restores its matching receiver.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task JointConditions_RetainReceiverMappingWithinTheBound(bool matching)
    {
        var lines = ControlFlowReceiverExamples.JointConditionFinalizerSource(matching);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(!matching, preview.Diagnostics.Any(diagnostic =>
            diagnostic.Kind == AnalysisDiagnosticKind.Error
            && diagnostic.Message.Contains("through this", StringComparison.Ordinal)),
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

        Assert.AreEqual(matching, refusal is null, refusal?.Message);
        if (!matching)
        {
            Assert.Contains("through this", refusal!.Message);
        }
    }

    /// <summary>
    /// An entered finalizer uses the receiver provenance carried by its incoming local state.
    /// </summary>
    /// <param name="changed">Whether the local should identify a different instance.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FinalizerIncomingLocal_UsesIncomingReceiverProvenance(bool changed)
    {
        var lines = ControlFlowReceiverExamples.FinalizerIncomingLocalSource(changed);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(changed, preview.Diagnostics.Any(diagnostic =>
            diagnostic.Kind == AnalysisDiagnosticKind.Error
            && diagnostic.Message.Contains("through this", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Boolean and switch finalizer branches apply only to their correlated receiver paths.
    /// </summary>
    /// <param name="useSwitch">Whether the finalizer selects its receiver with a switch.</param>
    /// <param name="matching">Whether each branch restores the original receiver.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task CorrelatedFinalizerBranches_PreserveReceiver(bool useSwitch, bool matching)
    {
        var lines = useSwitch ? ControlFlowReceiverExamples.CorrelatedSwitchFinalizerSource(matching)
            : ControlFlowReceiverExamples.CorrelatedFinalizerSource(matching);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(!matching, preview.Diagnostics.Any(diagnostic =>
            diagnostic.Kind == AnalysisDiagnosticKind.Error
            && diagnostic.Message.Contains("through this", StringComparison.Ordinal)),
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

        Assert.AreEqual(matching, refusal is null, refusal?.Message);
        if (!matching)
        {
            Assert.Contains("through this", refusal!.Message);
            return;
        }

        var choices = useSwitch ? new[] { 0, 1, -1, 42 } : [0, 1];
        foreach (var choice in choices)
        {
            session.AddLine("ldnull");
            session.AddLine($"ldc.i4 {choice}");
            var type = useSwitch ? "CorrelatedSwitchFinallyArgument" : "CorrelatedFinallyArgument";
            var parameter = useSwitch ? "int32" : "bool";
            session.AddLine($"newobj instance void {type}::.ctor(class {type}, {parameter})");
            session.AddLine($"ldfld int32 {type}::Value");
        }
        for (var index = 1; index < choices.Length; index++)
        {
            session.AddLine("add");
        }
        Assert.AreEqual(42 * choices.Length, session.Run().Value);
    }

    /// <summary>
    /// Repeated Boolean and switch decisions retain the receiver path selected by the first branch.
    /// </summary>
    /// <param name="useSwitch">Whether the filter uses a switch with a shared fallthrough target.</param>
    /// <param name="matching">Whether only the original-receiver path accepts the exception.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RepeatedFilterCondition_SelectsMatchingReceiver(bool useSwitch, bool matching)
    {
        var lines = useSwitch ? ControlFlowReceiverExamples.SwitchFallthroughConditionSource(matching)
            : ControlFlowReceiverExamples.RepeatedFilterConditionSource(matching);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(!matching, preview.Diagnostics.Any(diagnostic =>
            diagnostic.Kind == AnalysisDiagnosticKind.Error
            && diagnostic.Message.Contains("through this", StringComparison.Ordinal)),
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

        Assert.AreEqual(matching, refusal is null, refusal?.Message);
        if (!matching)
        {
            Assert.Contains("through this", refusal!.Message);
            return;
        }

        var type = useSwitch ? "SwitchFallthroughCondition" : "RepeatedFilterCondition";
        var parameter = useSwitch ? "int32" : "bool";
        session.AddLine("ldnull");
        session.AddLine("ldc.i4.1");
        session.AddLine($"newobj instance void {type}::.ctor(class {type}, {parameter})");
        session.AddLine($"ldfld int32 {type}::Value");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Repeated scalar and reference comparisons keep only the receiver paths their results permit.
    /// </summary>
    /// <param name="shape">The comparison form under test.</param>
    /// <param name="matching">Whether the changed receiver is rejected by the repeated comparison.</param>
    [TestMethod]
    [DataRow("constant", true)]
    [DataRow("constant", false)]
    [DataRow("zero", true)]
    [DataRow("zero", false)]
    [DataRow("direct", true)]
    [DataRow("direct", false)]
    [DataRow("null", true)]
    [DataRow("null", false)]
    [DataRow("source", true)]
    [DataRow("source", false)]
    [DataRow("transitive", true)]
    [DataRow("transitive", false)]
    [DataRow("float", true)]
    [DataRow("float", false)]
    public async Task RepeatedComparisonCondition_SelectsMatchingReceiver(
        string shape, bool matching)
    {
        var lines = shape switch
        {
            "constant" => ControlFlowReceiverExamples.RepeatedConstantComparisonSource(
                matching, directBranch: false),
            "zero" => ControlFlowReceiverExamples.RepeatedConstantComparisonSource(
                matching, directBranch: false, constant: 0),
            "direct" => ControlFlowReceiverExamples.RepeatedConstantComparisonSource(
                matching, directBranch: true),
            "null" => ControlFlowReceiverExamples.RepeatedNullComparisonSource(matching),
            "source" => ControlFlowReceiverExamples.RepeatedSourceComparisonSource(matching),
            "transitive" => ControlFlowReceiverExamples.TransitiveSourceComparisonSource(matching),
            "float" => ControlFlowReceiverExamples.RepeatedFloatingComparisonSource(matching),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(
            new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(!matching, preview.Diagnostics.Any(diagnostic =>
            diagnostic.Kind == AnalysisDiagnosticKind.Error
            && diagnostic.Message.Contains("through this", StringComparison.Ordinal)),
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

        Assert.AreEqual(matching, refusal is null, refusal?.Message);
        if (!matching)
        {
            Assert.Contains("through this", refusal!.Message);
            return;
        }

        AddComparisonConstructorCall(session, shape);
        var type = ComparisonTypeName(shape);
        session.AddLine($"ldfld int32 {type}::Value");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// A switch case that shares its target with fallthrough remains distinct during later filtering.
    /// </summary>
    [TestMethod]
    public async Task SharedSwitchTarget_PreservesEveryIncomingCondition()
    {
        var lines = ControlFlowReceiverExamples.UnsafeSharedSwitchTargetSource();
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.Contains(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error
            && diagnostic.Message.Contains("through this", StringComparison.Ordinal), preview.Diagnostics);
        var error = Assert.ThrowsExactly<ReplException>(() =>
        {
            foreach (var line in lines)
            {
                session.AddLine(line);
            }
        });
        Assert.Contains("through this", error.Message);
    }

    /// <summary>
    /// A conditional finalizer restores only the receiver path selected by the same condition.
    /// </summary>
    /// <param name="matching">Whether the changed path is the path that restores the receiver.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConditionalFinalizerCondition_SelectsMatchingReceiver(bool matching)
    {
        var lines = ControlFlowReceiverExamples.ConditionalFinalizerSource(matching);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(!matching, preview.Diagnostics.Any(diagnostic =>
            diagnostic.Kind == AnalysisDiagnosticKind.Error
            && diagnostic.Message.Contains("through this", StringComparison.Ordinal)),
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

        Assert.AreEqual(matching, refusal is null, refusal?.Message);
        if (!matching)
        {
            Assert.Contains("through this", refusal!.Message);
            return;
        }

        foreach (var choice in new[] { 0, 1 })
        {
            session.AddLine("ldnull");
            session.AddLine($"ldc.i4 {choice}");
            session.AddLine("newobj instance void ConditionalFinalizer::.ctor(class ConditionalFinalizer, bool)");
            session.AddLine("ldfld int32 ConditionalFinalizer::Value");
        }
        session.AddLine("add");
        Assert.AreEqual(84, session.Run().Value);
    }

    /// <summary>
    /// An overwritten selector follows its derived value without retaining stale branch facts.
    /// </summary>
    /// <param name="followsInversion">Whether the finalizer follows the inverted selector.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OverwrittenFinalizerCondition_DropsStaleSelectorCorrelation(bool followsInversion)
    {
        var lines = ControlFlowReceiverExamples.OverwrittenFinalizerConditionSource(followsInversion);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(!followsInversion, preview.Diagnostics.Any(diagnostic =>
            diagnostic.Kind == AnalysisDiagnosticKind.Error
            && diagnostic.Message.Contains("through this", StringComparison.Ordinal)),
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

        Assert.AreEqual(followsInversion, refusal is null, refusal?.Message);
        if (!followsInversion)
        {
            Assert.Contains("through this", refusal!.Message);
            return;
        }

        foreach (var choice in new[] { 0, 1 })
        {
            session.AddLine("ldnull");
            session.AddLine($"ldc.i4 {choice}");
            session.AddLine("newobj instance void OverwrittenFinalizerCondition::.ctor("
                + "class OverwrittenFinalizerCondition, bool)");
            session.AddLine("ldfld int32 OverwrittenFinalizerCondition::Value");
        }
        session.AddLine("add");
        Assert.AreEqual(84, session.Run().Value);
    }

    /// <summary>
    /// One finalizer's argument write reaches a later finalizer that reloads the same slot into argument zero.
    /// </summary>
    [TestMethod]
    public async Task NestedFinalizers_TransformOtherReceiverSlotsInOrder()
    {
        var lines = ControlFlowReceiverExamples.NestedFinalizerArgumentSource();
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
    /// Collapsing irrelevant filter paths preserves the decision correlated with the receiver used by its handler.
    /// </summary>
    [TestMethod]
    public async Task ManyFilterPaths_PreserveLaterReceiverDecision()
    {
        var lines = ControlFlowReceiverExamples.BoundedSelectiveFilterSource();
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
        session.AddLine("newobj instance void BoundedSelectiveFilter::.ctor(class BoundedSelectiveFilter, bool)");
        session.AddLine("ldfld int32 BoundedSelectiveFilter::Value");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Normal control flow skips a fault handler before using argument zero at the leave target.
    /// </summary>
    [TestMethod]
    public async Task NormalLeave_DoesNotApplyFaultEffect()
    {
        var lines = ControlFlowReceiverExamples.FaultLeaveSource();
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
        session.AddLine("newobj instance void FlowFaultLeaveArgument::.ctor(class FlowFaultLeaveArgument)");
        session.AddLine("ldfld int32 FlowFaultLeaveArgument::Value");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Leaving a catch or accepted filter handler applies its trailing finally before the target.
    /// </summary>
    /// <param name="filter">Whether the first handler is selected by a filter.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HandlerLeave_AppliesTrailingFinally(bool filter)
    {
        var lines = ControlFlowReceiverExamples.HandlerLeaveSource(filter);
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
    /// An outer filter runs before nested unwind handlers, while its paired handler runs afterward.
    /// </summary>
    [TestMethod]
    public async Task AcceptingFilter_AppliesPendingUnwindBeforeItsHandler()
    {
        var lines = ControlFlowReceiverExamples.FilterUnwindTimingSource();
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        var receiverErrors = preview.Diagnostics.Where(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error
            && diagnostic.Message.Contains("through this", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(1, receiverErrors);
        Assert.AreEqual(24, receiverErrors[0].Location.Line);
        Assert.ThrowsExactly<ReplException>(() =>
        {
            foreach (var line in lines)
            {
                session.AddLine(line);
            }
        });
    }

    /// <summary>
    /// Each path through an outer filter retains its own pending unwind handlers.
    /// </summary>
    [TestMethod]
    public async Task AcceptingFilter_KeepsPendingUnwindPathsSeparate()
    {
        var lines = ControlFlowReceiverExamples.MixedExceptionUnwindSource();
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
    /// A first filter's accepting branch cannot contaminate the receiver carried by its rejecting branch.
    /// </summary>
    [TestMethod]
    public async Task RejectingFilter_KeepsItsReceiverPathForLaterClauses()
    {
        var lines = ControlFlowReceiverExamples.SelectiveSiblingFilterSource();
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
    /// Exception search reaches later clauses only after each rejecting filter has updated the receiver.
    /// </summary>
    /// <param name="secondFilter">Whether a second filter restores the original receiver.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RejectingFilter_RestoresReceiverBeforeLaterCatch(bool secondFilter)
    {
        var lines = ControlFlowReceiverExamples.RestoringFilterSource(secondFilter);
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
        session.AddLine("ldnull");
        session.AddLine("newobj instance void RestoringFilterArgument::.ctor("
            + "class RestoringFilterArgument, class RestoringFilterArgument)");
        session.AddLine("ldfld int32 RestoringFilterArgument::Value");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// A finally or fault validates readonly stores with the receiver carried from protected code.
    /// </summary>
    /// <param name="fault">Whether the unwind handler is a fault instead of a finally.</param>
    /// <param name="exceptional">Whether the protected region throws instead of leaving normally.</param>
    /// <param name="accepted">Whether the unwind handler is skipped and the declaration is valid.</param>
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, true)]
    [DataRow(true, true, false)]
    public async Task UnwindHandler_UsesIncomingReceiver(bool fault, bool exceptional, bool accepted)
    {
        var lines = ControlFlowReceiverExamples.UnwindHandlerStoreSource(fault, exceptional);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(accepted, !preview.Diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error),
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

        Assert.AreEqual(accepted, refusal is null, refusal?.Message);
        if (!accepted)
        {
            Assert.Contains("through this", refusal!.Message);
            Assert.IsNotNull(session.OpenMethod);
        }
    }

    /// <summary>
    /// All accepting filter paths reach a pending finally with their own receiver provenance.
    /// </summary>
    [TestMethod]
    public async Task FilterPaths_KeepTheirReceiversAtPendingFinally()
    {
        var lines = ControlFlowReceiverExamples.FilterPathUnwindHandlerSource();
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
    /// An exception thrown by a finally carries its incoming receiver into the new exception search.
    /// </summary>
    [TestMethod]
    public async Task ThrowingFinally_CarriesIncomingReceiverToOuterCatch()
    {
        var lines = ControlFlowReceiverExamples.ThrowingUnwindHandlerSource();
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
    /// A finally can restore a receiver saved before protected code replaced argument zero.
    /// </summary>
    [TestMethod]
    public async Task Finally_RestoresSavedOriginalReceiver()
    {
        var lines = ControlFlowReceiverExamples.RestoringFinallySource();
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
        session.AddLine("ldnull");
        session.AddLine("newobj instance void RestoringFinallyArgument::.ctor("
            + "class RestoringFinallyArgument, class RestoringFinallyArgument)");
        session.AddLine("ldfld int32 RestoringFinallyArgument::Value");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// A finally can restore an original receiver saved in a local before entering the protected region.
    /// </summary>
    [TestMethod]
    public async Task Finally_RestoresOriginalReceiverFromLocal()
    {
        var lines = ControlFlowReceiverExamples.LocalRestoringFinallySource();
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
        session.AddLine("newobj instance void LocalRestoringFinallyArgument::.ctor("
            + "class LocalRestoringFinallyArgument)");
        session.AddLine("ldfld int32 LocalRestoringFinallyArgument::Value");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Indirect writes through argument zero inside a finally invalidate it at the leave target.
    /// </summary>
    /// <param name="write">The indirect write instruction.</param>
    [TestMethod]
    [DataRow("stind.ref")]
    [DataRow("stobj")]
    [DataRow("initobj")]
    [DataRow("cpobj")]
    [DataRow("initblk")]
    [DataRow("cpblk")]
    public async Task FinalizerAddressWrite_InvalidatesReceiver(string write)
    {
        var lines = ControlFlowReceiverExamples.FinalizerAddressSource(write);
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
    /// Exposing a saved receiver argument prevents a finally from treating its later reload as unchanged.
    /// </summary>
    [TestMethod]
    public async Task FinalizerAliasedArgument_CannotRestoreReceiver()
    {
        var lines = ControlFlowReceiverExamples.FinalizerAliasedArgumentSource();
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
    /// A catch-all stops exception search before a later filter, while a typed catch might not match.
    /// </summary>
    /// <param name="catchAll">Whether the first clause catches every exception.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CatchBeforeFilter_StopsOnlyWhenItCatchesEverything(bool catchAll)
    {
        var lines = ControlFlowReceiverExamples.CatchBeforeFilterSource(catchAll);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(catchAll, !preview.Diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error),
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

        Assert.AreEqual(catchAll, refusal is null, refusal?.Message);
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
    [Timeout(30_000, CooperativeCancellation = true)]
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
    /// A damaged filter operand can throw and therefore carries conservative state into the next clause.
    /// </summary>
    [TestMethod]
    public void Disassembly_UnknownFilterEffectReachesLaterCatch()
    {
        var session = new Session();
        var (_, image, _) = CecilFixture.Build((module, type) =>
        {
            var field = new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.InitOnly,
                module.TypeSystem.Int32);
            type.Fields.Add(field);
            var constructor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
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
            il.Append(il.Create(OpCodes.Ldtoken, module.ImportReference(typeof(string).GetMethod("Trim", Type.EmptyTypes)!)));
            il.Append(il.Create(OpCodes.Pop));
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
        });
        var patched = (byte[])image.Clone();
        var token = -1;
        for (var index = 0; index + 5 < patched.Length; index++)
        {
            if (patched[index] == 0xD0 && patched[index + 4] == 0x0A && patched[index + 5] == 0x26)
            {
                token = index;
                break;
            }
        }

        Assert.IsGreaterThan(0, token, "the filter's ldtoken bytes should be in the image");
        patched[token + 1] = 0xFF;
        patched[token + 2] = 0xFF;
        patched[token + 3] = 0xFF;
        var assembly = session.Resolver.LoadImage(patched);
        var listing = MethodDisassembler.Disassemble(assembly.GetType("N.Fixture")!.GetConstructors()[0], session);
        var diagnostics = StackAnalysis.Diagnostics(listing);
        Assert.Contains(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Unknown, diagnostics);
        Assert.Contains(diagnostic => diagnostic.Message.Contains("through this", StringComparison.Ordinal), diagnostics);
    }

    /// <summary>
    /// Unknown filter stack effects retain alternatives that do not run a noncompleting finally.
    /// </summary>
    [TestMethod]
    public void Disassembly_UnknownFilterEffectKeepsOptionalUnwindPaths()
    {
        var session = new Session();
        var (_, image, _) = CecilFixture.Build((module, type) =>
        {
            var field = new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.InitOnly,
                module.TypeSystem.Int32);
            type.Fields.Add(field);
            var constructor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
            constructor.Parameters.Add(new ParameterDefinition(type));
            constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
            type.Methods.Add(constructor);
            var il = constructor.Body.GetILProcessor();
            var innerTry = il.Create(OpCodes.Ldnull);
            var finallyStart = il.Create(OpCodes.Br, il.Create(OpCodes.Nop));
            finallyStart.Operand = finallyStart;
            var direct = il.Create(OpCodes.Ldnull);
            var filterStart = il.Create(OpCodes.Pop);
            var handlerStart = il.Create(OpCodes.Pop);
            var done = il.Create(OpCodes.Ret);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!)));
            var outerTry = il.Create(OpCodes.Ldarg_2);
            il.Append(outerTry);
            il.Append(il.Create(OpCodes.Brtrue, direct));
            il.Append(innerTry);
            il.Append(il.Create(OpCodes.Throw));
            il.Append(finallyStart);
            il.Append(direct);
            il.Append(il.Create(OpCodes.Throw));
            il.Append(filterStart);
            il.Append(il.Create(OpCodes.Ldtoken,
                module.ImportReference(typeof(string).GetMethod("Trim", Type.EmptyTypes)!)));
            il.Append(il.Create(OpCodes.Pop));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Starg, constructor.Body.ThisParameter));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Endfilter));
            il.Append(handlerStart);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldc_I4_S, (sbyte)42));
            il.Append(il.Create(OpCodes.Stfld, field));
            il.Append(il.Create(OpCodes.Leave, done));
            il.Append(done);
            constructor.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
            {
                TryStart = innerTry,
                TryEnd = finallyStart,
                HandlerStart = finallyStart,
                HandlerEnd = direct,
            });
            constructor.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Filter)
            {
                TryStart = outerTry,
                TryEnd = filterStart,
                FilterStart = filterStart,
                HandlerStart = handlerStart,
                HandlerEnd = done,
            });
        });
        var patched = (byte[])image.Clone();
        var tokens = new List<int>();
        for (var index = 0; index + 5 < patched.Length; index++)
        {
            if (patched[index] == 0xD0 && patched[index + 4] == 0x0A && patched[index + 5] == 0x26)
            {
                tokens.Add(index);
            }
        }

        Assert.HasCount(1, tokens, "the filter's ldtoken bytes should occur once in the image");
        var token = tokens[0];
        patched[token + 1] = 0xFF;
        patched[token + 2] = 0xFF;
        patched[token + 3] = 0xFF;
        var assembly = session.Resolver.LoadImage(patched);
        var listing = MethodDisassembler.Disassemble(assembly.GetType("N.Fixture")!.GetConstructors()[0], session);
        var diagnostics = StackAnalysis.Diagnostics(listing);
        var messages = string.Join("; ", diagnostics.Select(diagnostic => diagnostic.Kind + ": " + diagnostic.Message))
            + " | " + string.Join("; ", listing.Entries.Select(entry => entry.DisplayText + ":" + entry.EffectUnknown));
        Assert.Contains(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Unknown, diagnostics, messages);
        Assert.Contains(diagnostic => diagnostic.Message.Contains("through this", StringComparison.Ordinal), diagnostics, messages);
    }

    /// <summary>
    /// An unreachable decoded handler cannot escape its region or change an outer handler's receiver.
    /// </summary>
    [TestMethod]
    public void Disassembly_UnreachableHandlerDoesNotEscapeItsRegion()
    {
        var session = new Session();
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var field = new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.InitOnly,
                module.TypeSystem.Int32);
            type.Fields.Add(field);
            var constructor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
            constructor.Parameters.Add(new ParameterDefinition(type));
            type.Methods.Add(constructor);
            var il = constructor.Body.GetILProcessor();
            var done = il.Create(OpCodes.Ret);
            var outerHandler = il.Create(OpCodes.Pop);
            var innerDone = il.Create(OpCodes.Leave, done);
            var innerHandler = il.Create(OpCodes.Pop);
            var innerTry = il.Create(OpCodes.Leave, innerDone);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!)));
            il.Append(innerTry);
            il.Append(innerHandler);
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Starg, constructor.Body.ThisParameter));
            il.Append(il.Create(OpCodes.Ldnull));
            il.Append(il.Create(OpCodes.Throw));
            il.Append(innerDone);
            il.Append(outerHandler);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldc_I4_S, (sbyte)42));
            il.Append(il.Create(OpCodes.Stfld, field));
            il.Append(il.Create(OpCodes.Leave, done));
            il.Append(done);
            constructor.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = innerTry,
                TryEnd = innerHandler,
                CatchType = module.TypeSystem.Object,
                HandlerStart = innerHandler,
                HandlerEnd = innerDone,
            });
            constructor.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = innerTry,
                TryEnd = outerHandler,
                CatchType = module.TypeSystem.Object,
                HandlerStart = outerHandler,
                HandlerEnd = done,
            });
        }, session.Resolver);
        var constructor = fixture.GetConstructors()[0];
        var listing = MethodDisassembler.Disassemble(constructor, session);
        var diagnostics = StackAnalysis.Diagnostics(listing);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, diagnostics,
            string.Join("; ", diagnostics.Select(diagnostic => diagnostic.Message)));
        var instance = constructor.Invoke([null]);
        Assert.AreEqual(0, fixture.GetField("Value")!.GetValue(instance));
    }

    /// <summary>
    /// A decoded typed catch leaves later clauses reachable when its exception type might not match.
    /// </summary>
    [TestMethod]
    public void Disassembly_TypedCatchDoesNotStopExceptionSearch()
    {
        var session = new Session();
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var field = new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.InitOnly,
                module.TypeSystem.Int32);
            type.Fields.Add(field);
            var constructor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
            constructor.Parameters.Add(new ParameterDefinition(type));
            type.Methods.Add(constructor);
            var il = constructor.Body.GetILProcessor();
            var tryStart = il.Create(OpCodes.Ldnull);
            var firstHandler = il.Create(OpCodes.Pop);
            var secondHandler = il.Create(OpCodes.Pop);
            var done = il.Create(OpCodes.Ret);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!)));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Starg, constructor.Body.ThisParameter));
            il.Append(tryStart);
            il.Append(il.Create(OpCodes.Throw));
            il.Append(firstHandler);
            il.Append(il.Create(OpCodes.Leave, done));
            il.Append(secondHandler);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldc_I4_S, (sbyte)42));
            il.Append(il.Create(OpCodes.Stfld, field));
            il.Append(il.Create(OpCodes.Leave, done));
            il.Append(done);
            constructor.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart,
                TryEnd = firstHandler,
                CatchType = module.ImportReference(typeof(ArgumentException)),
                HandlerStart = firstHandler,
                HandlerEnd = secondHandler,
            });
            constructor.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart,
                TryEnd = firstHandler,
                CatchType = module.TypeSystem.Object,
                HandlerStart = secondHandler,
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

    private static Type BuildUnwindHandlerEntryFixture(int paths, Session session)
    {
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var field = new FieldDefinition("Value",
                FieldAttributes.Public | FieldAttributes.InitOnly, module.TypeSystem.Int32);
            type.Fields.Add(field);
            var constructor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
            constructor.Parameters.Add(new ParameterDefinition("other", ParameterAttributes.None, type));
            constructor.Parameters.Add(new ParameterDefinition(
                "choice", ParameterAttributes.None, module.TypeSystem.Int32));
            type.Methods.Add(constructor);
            constructor.Body.InitLocals = true;
            constructor.Body.MaxStackSize = 3;
            var saved = new VariableDefinition(type);
            var candidate = new VariableDefinition(type);
            constructor.Body.Variables.Add(saved);
            constructor.Body.Variables.Add(candidate);
            var il = constructor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call,
                module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Stloc, saved);
            il.Emit(OpCodes.Ldarg, constructor.Parameters[0]);
            il.Emit(OpCodes.Stloc, candidate);
            var outerTryStart = il.Create(OpCodes.Ldarg, constructor.Parameters[1]);
            il.Append(outerTryStart);

            for (var index = 0; index < paths; index++)
            {
                if (index > 0)
                {
                    il.Emit(OpCodes.Ldarg, constructor.Parameters[1]);
                }
                il.Emit(OpCodes.Ldc_I4, index);
                var next = il.Create(OpCodes.Nop);
                il.Emit(OpCodes.Bne_Un, next);
                var tryStart = il.Create(index == 0 ? OpCodes.Ldarg_0 : OpCodes.Ldnull);
                il.Append(tryStart);
                if (index == 0)
                {
                    il.Emit(OpCodes.Stloc, candidate);
                    il.Emit(OpCodes.Ldnull);
                }
                il.Emit(OpCodes.Throw);
                var finallyStart = il.Create(OpCodes.Ldloc, index == 0 ? candidate : saved);
                il.Append(finallyStart);
                il.Emit(OpCodes.Starg, constructor.Body.ThisParameter);
                if (index == 0)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4_S, (sbyte)41);
                    il.Emit(OpCodes.Stfld, field);
                    il.Emit(OpCodes.Ldloc, saved);
                    il.Emit(OpCodes.Starg, constructor.Body.ThisParameter);
                }
                il.Emit(OpCodes.Endfinally);
                il.Append(next);
                constructor.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
                {
                    TryStart = tryStart,
                    TryEnd = finallyStart,
                    HandlerStart = finallyStart,
                    HandlerEnd = next,
                });
            }

            var done = il.Create(OpCodes.Ret);
            il.Emit(OpCodes.Leave, done);
            var filterStart = il.Create(OpCodes.Pop);
            il.Append(filterStart);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Endfilter);
            var handlerStart = il.Create(OpCodes.Pop);
            il.Append(handlerStart);
            il.Emit(OpCodes.Leave, done);
            il.Append(done);
            constructor.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Filter)
            {
                TryStart = outerTryStart,
                TryEnd = filterStart,
                FilterStart = filterStart,
                HandlerStart = handlerStart,
                HandlerEnd = done,
            });
        }, session.Resolver, $"UnwindHandlerEntry{paths}");
        return fixture;
    }

    private static Type BuildUnknownEqualityFixture(int cases, Session session)
    {
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var field = new FieldDefinition("Value",
                FieldAttributes.Public | FieldAttributes.InitOnly, module.TypeSystem.Int32);
            type.Fields.Add(field);
            var constructor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
            constructor.Parameters.Add(new ParameterDefinition("other", ParameterAttributes.None, type));
            constructor.Parameters.Add(new ParameterDefinition(
                "choice", ParameterAttributes.None, module.TypeSystem.Int32));
            type.Methods.Add(constructor);
            constructor.Body.InitLocals = true;
            constructor.Body.MaxStackSize = 2;
            var left = new VariableDefinition(type);
            var right = new VariableDefinition(type);
            constructor.Body.Variables.Add(left);
            constructor.Body.Variables.Add(right);
            var il = constructor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call,
                module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            var tryStart = il.Create(OpCodes.Ldnull);
            il.Append(tryStart);
            il.Emit(OpCodes.Throw);
            var filterStart = il.Create(OpCodes.Pop);
            il.Append(filterStart);
            il.Emit(OpCodes.Ldarg, constructor.Parameters[1]);
            var targets = Enumerable.Range(0, cases).Select(_ => il.Create(OpCodes.Ldarg_0)).ToArray();
            il.Emit(OpCodes.Switch, targets);
            var defaultTarget = il.Create(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Br, defaultTarget);
            var compare = il.Create(OpCodes.Ldloc, left);
            for (var index = 0; index < targets.Length; index++)
            {
                il.Append(targets[index]);
                il.Emit(OpCodes.Stloc, left);
                il.Emit(index == targets.Length - 1 ? OpCodes.Ldnull : OpCodes.Ldarg_0);
                il.Emit(OpCodes.Stloc, right);
                il.Emit(OpCodes.Br, compare);
            }
            il.Append(defaultTarget);
            il.Emit(OpCodes.Stloc, left);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Stloc, right);
            il.Append(compare);
            il.Emit(OpCodes.Ldloc, right);
            il.Emit(OpCodes.Ceq);
            var preserve = il.Create(OpCodes.Ldloc, left);
            il.Emit(OpCodes.Brtrue, preserve);
            il.Emit(OpCodes.Ldarg, constructor.Parameters[0]);
            il.Emit(OpCodes.Starg, constructor.Body.ThisParameter);
            il.Append(preserve);
            il.Emit(OpCodes.Ldloc, right);
            il.Emit(OpCodes.Ceq);
            var reject = il.Create(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Brtrue, reject);
            il.Emit(OpCodes.Ldc_I4_1);
            var result = il.Create(OpCodes.Endfilter);
            il.Emit(OpCodes.Br, result);
            il.Append(reject);
            il.Append(result);
            var handlerStart = il.Create(OpCodes.Pop);
            il.Append(handlerStart);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_S, (sbyte)42);
            il.Emit(OpCodes.Stfld, field);
            var done = il.Create(OpCodes.Ret);
            il.Emit(OpCodes.Leave, done);
            il.Append(done);
            constructor.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Filter)
            {
                TryStart = tryStart,
                TryEnd = filterStart,
                FilterStart = filterStart,
                HandlerStart = handlerStart,
                HandlerEnd = done,
            });
        }, session.Resolver, $"UnknownEquality{cases}");
        return fixture;
    }

    private static void AddComparisonConstructorCall(Session session, string shape)
    {
        var type = ComparisonTypeName(shape);
        session.AddLine("ldnull");
        switch (shape)
        {
            case "constant":
            case "zero":
            case "direct":
                session.AddLine("ldc.i4.1");
                session.AddLine($"newobj instance void {type}::.ctor(class {type}, int32)");
                break;
            case "null":
                session.AddLine("ldstr \"choice\"");
                session.AddLine($"newobj instance void {type}::.ctor(class {type}, object)");
                break;
            case "source":
                session.AddLine("ldstr \"choice\"");
                session.AddLine("ldstr \"sentinel\"");
                session.AddLine($"newobj instance void {type}::.ctor(class {type}, object, object)");
                break;
            case "transitive":
                session.AddLine("ldstr \"a\"");
                session.AddLine("ldstr \"b\"");
                session.AddLine("ldstr \"c\"");
                session.AddLine($"newobj instance void {type}::.ctor(class {type}, object, object, object)");
                break;
            case "float":
                session.AddLine("ldc.r8 1");
                session.AddLine($"newobj instance void {type}::.ctor(class {type}, float64)");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    private static string ComparisonTypeName(string shape) => shape switch
    {
        "constant" or "zero" or "direct" => "RepeatedConstantComparison",
        "null" => "RepeatedNullComparison",
        "source" => "RepeatedSourceComparison",
        "transitive" => "TransitiveSourceComparison",
        "float" => "RepeatedFloatingComparison",
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };
}
