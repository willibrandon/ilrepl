using System.Text.Json;
using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Appended returns retain complete validation, producer locations, and executable behavior.
/// </summary>
[TestClass]
public sealed class IncrementalReturnTests
{
    /// <summary>
    /// Supplies the cancellation token for complete control-flow analysis.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A cell or method return preserves the full analysis and executes its original producer chain.
    /// </summary>
    /// <param name="method">Whether the body belongs to a named method.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Return_PreservesProducerLocationsAndExecution(bool method)
    {
        using var core = new ReplCore();
        var session = core.Session;
        if (method)
        {
            session.AddLine(".method int32 Answer() {");
        }

        Add(session, "ldc.i4.s 40", "ldc.i4.2", "add");
        var result = AppendAndCompare(session.State);
        Assert.AreSequenceEqual<int>([2], result.Before[^2]!.Values!.Single().Origins);
        Assert.AreEqual(2, result.MaxStack);
        if (method)
        {
            Add(session, "}", "call int32 Answer()");
        }

        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Empty returns and unreachable returns retain distinct reachability while remaining executable.
    /// </summary>
    /// <param name="unreachable">Whether a previous return makes the appended return unreachable.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Return_PreservesVoidAndUnreachableStates(bool unreachable)
    {
        using var core = new ReplCore();
        var session = core.Session;
        Add(session, ".method void Empty() {", "nop");
        if (unreachable)
        {
            session.AddLine("ret");
        }

        var result = AppendAndCompare(session.State);
        Assert.AreEqual(unreachable, result.Before[^2] is null);
        Assert.IsNull(result.End);
        Add(session, "}", "call void Empty()");
        Assert.IsTrue(session.Run().IsVoid);
    }

    /// <summary>
    /// Returning an argument address preserves its source identity and subsequent dereference.
    /// </summary>
    [TestMethod]
    public void Return_PreservesManagedAddress()
    {
        using var core = new ReplCore();
        var session = core.Session;
        Add(session, ".method int32& Pass(int32& value) {", "ldarg.0");
        var result = AppendAndCompare(session.State);
        var returned = result.Before[^2]!.Values!.Single();
        Assert.AreEqual(typeof(int).MakeByRefType(), returned.Type);
        Assert.AreSequenceEqual<int>([0], returned.Origins);
        Add(session, "}", ".locals init (int32 value)", "ldc.i4.s 42", "stloc.0", "ldloca.s 0",
            "call int32& Pass(int32&)", "ldind.i4");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Constructor returns preserve unverifiable diagnostics before initialization and the initialized receiver afterward.
    /// </summary>
    [TestMethod]
    public void Return_RequiresConstructorInitialization()
    {
        using var core = new ReplCore();
        var session = core.Session;
        Add(session, ".class public ReturnOwner {", ".method public instance void .ctor() {", "nop");
        var entry = ReturnEntry(session.State);
        Assert.IsFalse(RuntimeFlowAnalysis.TryAppend(session.State, session.State.Analysis, entry, out _));
        var uninitialized = RuntimeFlowAnalysis.Run(session.State, [.. session.State.Entries, entry], TestContext.CancellationToken);
        Assert.Contains(diagnostic => diagnostic.Code == "FLOW007" && diagnostic.Kind == AnalysisDiagnosticKind.Unverifiable
            && diagnostic.Message.Contains("ret", StringComparison.Ordinal), uninitialized.Diagnostics);
        Assert.HasCount(1, session.State.Entries);
        Add(session, "ldarg.0", "call instance void object::.ctor()");
        var result = AppendAndCompare(session.State);
        Assert.AreEqual(ConstructorThisState.Initialized, result.Before[^2]!.ConstructorState);
        Add(session, "}", "}", "newobj instance void ReturnOwner::.ctor()");
        Assert.AreEqual("ReturnOwner", session.Run().Value!.GetType().Name);
    }

    /// <summary>
    /// Invalid returns retain the complete diagnostic and leave accepted source and stack unchanged.
    /// </summary>
    /// <param name="header">The enclosing method declaration.</param>
    /// <param name="source">The value pushed before the invalid return.</param>
    /// <param name="message">The required explanation.</param>
    [TestMethod]
    [DataRow(".method int32 Wrong() {", "ldstr \"wrong\"", "ret needs int32")]
    [DataRow(".method void Wrong() {", "ldc.i4.1", "needs an empty stack")]
    [DataRow(".method int32 Wrong() {", "nop", "stack is empty")]
    public void Return_InvalidStackUsesCompleteDiagnostic(string header, string source, string message)
    {
        using var core = new ReplCore();
        Add(core.Session, header, source);
        AssertFallback(core.Session.State, message);
    }

    /// <summary>
    /// Extra return values retain the complete error and the same body executes after explicitly removing one value.
    /// </summary>
    /// <param name="method">Whether the body belongs to a named method.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Return_ExtraValueRequiresExplicitRemoval(bool method)
    {
        using var core = new ReplCore();
        var session = core.Session;
        if (method)
        {
            session.AddLine(".method int32 Answer() {");
        }

        Add(session, "ldc.i4.s 42", "ldc.i4.1");
        AssertFallback(session.State, method ? "exactly one int32" : "stack must hold 0 or 1 value");
        session.AddLine("pop");
        AppendAndCompare(session.State);
        if (method)
        {
            Add(session, "}", "call int32 Answer()");
        }

        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Protected returns and unfinished tail calls keep the complete graph validation path.
    /// </summary>
    /// <param name="tail">Whether the source ends in a tail call instead of an open try.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Return_ProtectedRegionAndTailPrefixRequireFullAnalysis(bool tail)
    {
        using var core = new ReplCore();
        var session = core.Session;
        if (tail)
        {
            Add(session, "ldc.i4.s -42", "tail.", "call int32 [System.Runtime]System.Math::Abs(int32)");
            AssertFallback(session.State, "must return object directly");
        }
        else
        {
            Add(session, ".method void Guarded() {", ".try {", "nop");
            AssertFallback(session.State, "protected region");
        }
    }

    private static FlowResult<Type> AppendAndCompare(CellState state)
    {
        var entry = ReturnEntry(state);
        Assert.IsTrue(RuntimeFlowAnalysis.TryAppend(state, state.Analysis, entry, out var incremental));
        var complete = RuntimeFlowAnalysis.Run(state, [.. state.Entries, entry]);
        Assert.AreEqual(complete.MaxStack, incremental.MaxStack);
        Assert.AreSequenceEqual(complete.Diagnostics, incremental.Diagnostics);
        Compare(complete.Before, incremental.Before);
        Compare(complete.After, incremental.After);
        state.Apply(NormalizedLine.FromText("ret") with { Location = entry.Location });
        return incremental;
    }

    private static void Compare(FlowState<Type>?[] expected, FlowState<Type>?[] actual)
    {
        Assert.HasCount(expected.Length, actual);
        for (var index = 0; index < expected.Length; index++)
        {
            var left = expected[index];
            var right = actual[index];
            if (left is null)
            {
                Assert.IsNull(right);
                continue;
            }

            Assert.IsNotNull(right);
            Assert.AreEqual(left.Kind, right.Kind);
            Assert.AreEqual(left.ThisArgumentIsOriginal, right.ThisArgumentIsOriginal);
            Assert.AreEqual(left.ConstructorState, right.ConstructorState);
            Assert.AreEqual(left.IsCorrelationOnly, right.IsCorrelationOnly);
            Assert.AreEquivalent(left.FilterPaths, right.FilterPaths);
            Assert.IsNotNull(left.Values);
            Assert.IsNotNull(right.Values);
            Assert.HasCount(left.Values.Length, right.Values);
            for (var slot = 0; slot < left.Values.Length; slot++)
            {
                Assert.AreEqual(left.Values[slot].Type, right.Values[slot].Type);
                Assert.AreEqual(left.Values[slot].IsThis, right.Values[slot].IsThis);
                Assert.AreEqual(left.Values[slot].IsReadOnly, right.Values[slot].IsReadOnly);
                Assert.AreEqual(left.Values[slot].IsKnownZero, right.Values[slot].IsKnownZero);
                Assert.AreSequenceEqual(left.Values[slot].Origins, right.Values[slot].Origins);
            }
        }
    }

    private static void AssertFallback(CellState state, string message)
    {
        var entries = state.Entries.ToArray();
        var stack = state.StackText;
        var entry = ReturnEntry(state);
        Assert.IsFalse(RuntimeFlowAnalysis.TryAppend(state, state.Analysis, entry, out _));
        var complete = RuntimeFlowAnalysis.Run(state, [.. entries, entry]);
        var expected = complete.Diagnostics.First(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error);
        var error = Assert.ThrowsExactly<ReplException>(() =>
            state.Apply(NormalizedLine.FromText("ret") with { Location = entry.Location }));
        Assert.Contains(message, error.Message);
        Assert.AreEqual(JsonSerializer.Serialize(expected, ProtocolJsonContext.Default.AnalysisDiagnostic),
            JsonSerializer.Serialize(error.Diagnostics.Single(), ProtocolJsonContext.Default.AnalysisDiagnostic));
        Assert.AreSequenceEqual(entries, state.Entries);
        Assert.AreEqual(stack, state.StackText);
    }

    private static CellEntry ReturnEntry(CellState state) => new()
    {
        Kind = EntryKind.Instruction,
        Source = "ret",
        Instruction = InstructionParser.Parse("ret", state.Context),
        Location = new AnalysisLocation("return-test", state.Entries.Count, 0, 3),
    };

    private static void Add(Session session, params string[] lines)
    {
        foreach (var line in lines)
        {
            session.AddLine(line);
        }
    }
}
