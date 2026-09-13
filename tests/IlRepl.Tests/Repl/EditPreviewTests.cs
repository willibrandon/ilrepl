using System.Reflection;
using System.Runtime.CompilerServices;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tests.Engine;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Edit documents analyze and complete against their pinned original context without publishing speculative definitions.
/// </summary>
[TestClass]
public sealed class EditPreviewTests
{
    /// <summary>
    /// Supplies cancellation for real analysis and completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A complete unsent document can access private instance fields and helpers and then execute unchanged when committed.
    /// </summary>
    [TestMethod]
    public async Task Analyze_PrivateInstanceContextMatchesRealCommitAndCompletion()
    {
        var session = Vault();
        var core = new ReplCore(session, new ReplOptions());
        await using var engine = new InProcessEngine(core);
        var document = Prepare(core, "instance int32 Vault::Read()");
        var lines = document.Source.Split('\n');
        var generation = session.Generation;
        var ret = Find(lines, ": ret");

        var analysis = await engine.AnalyzeAsync(new AnalysisRequest(lines, ret, 0, 1), TestContext.CancellationToken);
        var fields = await Complete(engine, Prefix(lines, "::_value", "::_v"));
        var methods = await Complete(engine, Prefix(lines, "::Bump()", "::Bu"));

        AssertNoErrors(analysis);
        Assert.AreEqual("[int32]", analysis.Stack!.Render());
        Assert.Contains(item => item.InsertText.Contains("::_value", StringComparison.Ordinal), fields.Items);
        Assert.Contains(item => item.InsertText.Contains("::Bump()", StringComparison.Ordinal), methods.Items);
        Assert.AreEqual(generation, session.Generation);
        Assert.IsNull(session.Edits.Single().Method);
        Assert.IsEmpty(session.Methods);
        using (var editing = new EditingSession(session))
        {
            var closed = editing.Speculate(lines, lines.Length, cancellationToken: TestContext.CancellationToken);
            Assert.IsEmpty(closed.SkippedLines);
            Assert.IsNull(closed.OpenMethod);
            Assert.IsEmpty(closed.Snapshot.SessionMethods);
        }

        Replay(core, document.Source);
        var method = session.Edits.Single().Method!;
        var receiver = Activator.CreateInstance(method.DeclaringType!, [40]);
        Assert.AreEqual(82, method.Invoke(receiver, null));
    }

    /// <summary>
    /// Struct instance edits expose this as a managed reference in both prefix views and complete control-flow analysis.
    /// </summary>
    [TestMethod]
    public async Task Analyze_ValueTypeThisRetainsManagedReferenceAndReceiverProvenance()
    {
        var session = IlLines.Load(".class public sequential sealed Meter extends [System.Runtime]System.ValueType {",
            ".field private int32 _value",
            ".method public instance int32 Read() { ldarg.0; ldfld int32 Meter::_value; ret }", "}");
        var core = new ReplCore(session, new ReplOptions());
        await using var engine = new InProcessEngine(core);
        var document = Prepare(core, "instance int32 Meter::Read()");
        var lines = document.Source.Split('\n');
        var field = Find(lines, "ldfld");
        using var editing = new EditingSession(session);

        var view = editing.Speculate(lines, field, cancellationToken: TestContext.CancellationToken);
        var analysis = await engine.AnalyzeAsync(new AnalysisRequest(lines, field, 0, 1), TestContext.CancellationToken);

        Assert.IsEmpty(view.SkippedLines);
        Assert.AreEqual(0, view.Scope.ThisIndex);
        var receiver = view.Scope.Arguments[0].Type;
        Assert.AreEqual(TypeSymbolKind.ByRef, receiver.Kind);
        Assert.AreEqual("Meter", receiver.Element!.Name);
        Assert.AreEqual(receiver, view.Stack.Single());
        Assert.IsTrue(view.ThisSlots.Single());
        AssertNoErrors(analysis);
        Assert.Contains("Meter&", analysis.Stack!.Render());
        Replay(core, document.Source);
        var method = session.Edits.Single().Method!;
        var value = RuntimeHelpers.GetUninitializedObject(method.DeclaringType!);
        method.DeclaringType!.GetField("_value", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, 42);
        Assert.AreEqual(42, method.Invoke(value, null));
    }

    /// <summary>
    /// Owner and method parameters retain distinct identities and reference constraints while the edit is analyzed.
    /// </summary>
    [TestMethod]
    public async Task Analyze_GenericOwnerAndMethodKeepOriginalConstraints()
    {
        var session = IlLines.Load(".class public Generic`1<class T> {", ".field private !0 _value",
            ".method public instance void .ctor(!0 value) {", "ldarg.0", "call instance void Object::.ctor()",
            "ldarg.0", "ldarg.1", "stfld !0 class Generic`1<!0>::_value", "ret", "}",
            ".method public instance !!0 Choose<class U>(!!0 candidate) {", "ldarg.0",
            "ldfld !0 class Generic`1<!0>::_value", "pop", "ldarg.1", "brtrue PRESENT", "ldnull", "ret",
            "PRESENT: ldarg.1", "ret", "}", "}");
        var core = new ReplCore(session, new ReplOptions());
        await using var engine = new InProcessEngine(core);
        var document = Prepare(core, "instance !!0 Generic`1::Choose<[1]>(!!0)");
        var lines = document.Source.Split('\n');
        var branch = Find(lines, "brtrue");
        using var editing = new EditingSession(session);

        var view = editing.Speculate(lines, branch, cancellationToken: TestContext.CancellationToken);
        var analysis = await engine.AnalyzeAsync(new AnalysisRequest(lines, branch, 0, 1), TestContext.CancellationToken);
        var fields = await Complete(engine, Prefix(lines, "::_value", "::_v"));

        Assert.IsEmpty(view.SkippedLines);
        var typeParameter = view.Scope.Generics.TypeArguments.Single();
        var methodParameter = view.Scope.Generics.MethodArguments.Single();
        Assert.AreEqual(TypeSymbolKind.TypeParameter, typeParameter.Kind);
        Assert.AreEqual(TypeSymbolKind.MethodParameter, methodParameter.Kind);
        Assert.AreEqual("T", typeParameter.Name);
        Assert.AreEqual("U", methodParameter.Name);
        Assert.AreNotEqual(typeParameter.Owner, methodParameter.Owner);
        Assert.IsTrue(typeParameter.ParameterAttributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint));
        Assert.IsTrue(methodParameter.ParameterAttributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint));
        Assert.AreEqual(methodParameter, view.Stack.Single());
        AssertNoErrors(analysis);
        Assert.Contains(item => item.InsertText.Contains("::_value", StringComparison.Ordinal), fields.Items);
        Replay(core, document.Source);
        var owner = session.Edits.Single().Method!.DeclaringType!;
        Assert.ThrowsExactly<ArgumentException>(() => owner.MakeGenericType(typeof(int)));
        var closed = owner.MakeGenericType(typeof(string));
        var selected = closed.GetMethod("Choose")!;
        Assert.ThrowsExactly<ArgumentException>(() => selected.MakeGenericMethod(typeof(int)));
        var method = selected.MakeGenericMethod(typeof(string));
        var receiver = Activator.CreateInstance(closed, ["owner"]);
        Assert.AreEqual("candidate", method.Invoke(receiver, ["candidate"]));
        Assert.IsNull(method.Invoke(receiver, [null]));
    }

    /// <summary>
    /// Redefining a source class cannot replace the private members or owner identity used to analyze its captured edit.
    /// </summary>
    [TestMethod]
    public async Task Analyze_SourceRedefinitionKeepsOriginalPrivateContextPinned()
    {
        var session = Vault();
        var core = new ReplCore(session, new ReplOptions());
        await using var engine = new InProcessEngine(core);
        var document = Prepare(core, "instance int32 Vault::Read()");
        var original = session.Edits.Single().Original.Method.DeclaringType!;
        foreach (var line in IlLines.Expand(".class public Vault {", ".field private int32 _replacement",
            ".method public instance int32 Read() { ldc.i4 99; ret }", "}"))
        {
            Assert.IsTrue(core.Handle(line).Succeeded, Transcript(core));
        }

        var lines = document.Source.Split('\n');
        var analysis = await engine.AnalyzeAsync(new AnalysisRequest(lines, Find(lines, ": ret"), 0, 1),
            TestContext.CancellationToken);
        var completion = await Complete(engine, Prefix(lines, "::_value", "::"));

        AssertNoErrors(analysis);
        Assert.Contains(item => item.InsertText.Contains("::_value", StringComparison.Ordinal), completion.Items);
        Assert.DoesNotContain(item => item.InsertText.Contains("::_replacement", StringComparison.Ordinal), completion.Items);
        Assert.AreSame(original, session.Edits.Single().Original.Method.DeclaringType);
        Replay(core, document.Source);
        var method = session.Edits.Single().Method!;
        Assert.AreEqual(82, method.Invoke(Activator.CreateInstance(method.DeclaringType!, [40]), null));
    }

    /// <summary>
    /// Invalid edits report their actual return line and clear restores the cell's previous locals, stack, and binding context.
    /// </summary>
    [TestMethod]
    public async Task Analyze_InvalidBodyAndClearPreserveCellContext()
    {
        var session = IlLines.Load(".method int32 Value() { ldc.i4.1; ret }", ".locals init (int32 saved)", "ldc.i4 42");
        var core = new ReplCore(session, new ReplOptions());
        await using var engine = new InProcessEngine(core);
        var document = Prepare(core, "Value");
        var invalid = document.Source.Replace("ldc.i4.1", "ldstr \"wrong\"", StringComparison.Ordinal).Split('\n');
        var ret = Find(invalid, ": ret");
        var analysis = await engine.AnalyzeAsync(new AnalysisRequest(invalid, ret, 0, 1), TestContext.CancellationToken);

        Assert.Contains(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error && diagnostic.Location.Line == ret
            && diagnostic.Message.Contains("ret needs", StringComparison.Ordinal), analysis.Diagnostics);
        using var editing = new EditingSession(session);
        string[] cleared = [invalid[0], invalid[1], ".locals init (string temporary)", ".clear", ""];
        var restored = editing.Speculate(cleared, cleared.Length - 1, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(restored.SkippedLines);
        Assert.IsNull(restored.OpenMethod);
        Assert.IsNull(restored.Scope.Access.Type);
        Assert.HasCount(1, restored.Scope.Locals);
        Assert.AreEqual("saved", restored.Scope.Locals[0].Name);
        Assert.AreEqual(TypeSymbol.Primitive("int32"), restored.Stack.Single());
        foreach (var line in cleared[..^2])
        {
            Assert.IsTrue(core.Handle(line).Succeeded, Transcript(core));
        }

        Assert.IsTrue(core.Handle(".clear").Succeeded, Transcript(core));
        Assert.AreEqual(0, core.Status.OpenDepth);
        Assert.AreEqual(0, session.Edits.Single().Revision);
        Assert.IsNull(session.Edits.Single().Method);
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Analysis and completion combine accepted edit lines with the remaining unsent text and clear removes that context.
    /// </summary>
    [TestMethod]
    public async Task Analyze_AcceptedEditPrefixSeedsRemainingInputAndClearRestoresNormalAnalysis()
    {
        var session = Vault();
        var core = new ReplCore(session, new ReplOptions());
        await using var engine = new InProcessEngine(core);
        var document = Prepare(core, "instance int32 Vault::Read()");
        var lines = document.Source.Split('\n');
        var field = Find(lines, "ldfld");
        foreach (var line in lines[..field])
        {
            var accepted = await engine.HandleAsync(line, TestContext.CancellationToken);
            Assert.IsTrue(accepted.Succeeded, Transcript(core));
        }

        var remaining = lines[field..];
        var analysis = await engine.AnalyzeAsync(new AnalysisRequest(remaining, 0, 0, 1), TestContext.CancellationToken);
        var completion = await Complete(engine, Prefix(remaining, "::_value", "::_v"));

        AssertNoErrors(analysis);
        Assert.Contains("Vault", analysis.Stack!.Render());
        Assert.Contains(item => item.InsertText.Contains("::_value", StringComparison.Ordinal), completion.Items);
        Assert.IsTrue((await engine.HandleAsync(".clear", TestContext.CancellationToken)).Succeeded);
        var restored = await engine.AnalyzeAsync(new AnalysisRequest(["ldc.i4 42"], 1, 0, 2), TestContext.CancellationToken);
        AssertNoErrors(restored);
        Assert.AreEqual("[int32]", restored.Stack!.Render());
        Assert.IsNull(session.Edits.Single().Method);
        Assert.AreEqual(0, core.Status.OpenDepth);
    }

    private static Session Vault() => IlLines.Load(".class public Vault {", ".field private int32 _value",
        ".method public instance void .ctor(int32 value) {", "ldarg.0", "call instance void Object::.ctor()",
        "ldarg.0", "ldarg.1", "stfld int32 Vault::_value", "ret", "}",
        ".method private instance int32 Bump() { ldarg.0; ldfld int32 Vault::_value; ldc.i4.2; add; ret }",
        ".method public instance int32 Read() {", "ldarg.0", "ldfld int32 Vault::_value", "ldarg.0",
        "call instance int32 Vault::Bump()", "add", "ret", "}", "}");

    private static EditDocument Prepare(ReplCore core, string reference)
    {
        var result = core.Handle(".edit " + reference + " as Copy");
        Assert.IsTrue(result.Succeeded, Transcript(core));
        Assert.IsNotNull(result.EditDocument);
        return result.EditDocument;
    }

    private Task<CompletionReply> Complete(InProcessEngine engine, (string[] Lines, int Line) document) =>
        engine.CompleteAsync(new CompletionRequest(document.Lines, document.Line, document.Lines[document.Line].Length, null, []),
            TestContext.CancellationToken);

    private static (string[] Lines, int Line) Prefix(string[] source, string full, string partial)
    {
        var line = Find(source, full);
        var lines = source.ToArray();
        lines[line] = lines[line][..lines[line].IndexOf(full, StringComparison.Ordinal)] + partial;
        return (lines, line);
    }

    private static int Find(string[] lines, string text) => Array.FindIndex(lines, line => line.Contains(text, StringComparison.Ordinal));

    private static void AssertNoErrors(AnalysisReply reply) =>
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, reply.Diagnostics,
            string.Join("\n", reply.Diagnostics.Select(diagnostic => diagnostic.Message)));

    private static void Replay(ReplCore core, string source)
    {
        foreach (var line in source.Split('\n'))
        {
            Assert.IsTrue(core.Handle(line).Succeeded, Transcript(core));
        }
    }

    private static string Transcript(ReplCore core) => string.Join("\n", core.Transcript.Lines.Select(line => line.PlainText));
}
