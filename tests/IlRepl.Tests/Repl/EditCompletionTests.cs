using System.Globalization;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tests.Engine;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Edit completion produces usable source edits, includes recoverable drafts, and preserves cursor and replacement contracts.
/// </summary>
[TestClass]
public sealed class EditCompletionTests
{
    private static readonly string[] CopyNames = ["Copy", "CopyDraft"];

    /// <summary>
    /// Supplies cancellation for real completion and command requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Scenario completion quotes names so the accepted command resolves and executes the declared method.
    /// </summary>
    /// <param name="name">The scenario's metadata name.</param>
    /// <param name="prefix">The typed prefix to complete.</param>
    [TestMethod]
    [DataRow("My Scenario", "My")]
    [DataRow("Owner's Scenario", "Ow")]
    public async Task Scenarios_InsertUsableQuotedIdentifiers(string name, string prefix)
    {
        var session = Value();
        Commit(session, "Copy");
        var identifier = TypeNameFormatter.IlAsmIdentifier(name);
        foreach (var source in IlLines.Expand(".method int32 " + identifier + "() {", "ldc.i4.s 21", "call Copy", "ret", "}"))
        {
            session.AddLine(source);
        }

        await using var engine = new InProcessEngine(new ReplCore(session, new ReplOptions()));
        var line = ".compare Copy using " + prefix + "Suffix --assert";
        var result = await Complete(engine, line, ".compare Copy using ".Length + prefix.Length);
        Assert.HasCount(1, result.Items);
        Assert.AreEqual(identifier, result.Items[0].InsertText);
        var accepted = Apply(line, result, result.Items[0]);
        Assert.AreEqual(".compare Copy using " + identifier + " --assert", accepted);
        var package = ComparisonCapture.Create(session, accepted[".compare ".Length..]);
        Assert.AreEqual(name, package.Original.EntryMethod);
        var comparison = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
        Assert.AreEqual("match", comparison.Outcome, comparison.Original.Detail + "; " + comparison.Edited.Detail);
        Assert.AreEqual("42", comparison.Original.Result!.Value);
        Assert.AreEqual("42", comparison.Edited.Result!.Value);
    }

    /// <summary>
    /// Edit-name completion includes committed copies and drafts while replacing the whole token and preserving its suffix.
    /// </summary>
    /// <param name="command">The command that accepts an edit name.</param>
    [TestMethod]
    [DataRow(".diff")]
    [DataRow(".compare")]
    [DataRow(".methods")]
    public async Task Names_IncludeDraftsAndReplaceTheWholeToken(string command)
    {
        var session = Value();
        Commit(session, "Copy");
        session.PrepareEdit("Value", "CopyDraft");
        var core = new ReplCore(session, new ReplOptions());
        await using var engine = new InProcessEngine(core);
        var line = command + "  CoSuffix // retain this comment";
        var start = line.IndexOf("CoSuffix", StringComparison.Ordinal);

        var result = await Complete(engine, line, start + 2);

        Assert.AreSequenceEqual(CopyNames, result.Items.Select(item => item.InsertText));
        Assert.AreEqual(start, result.ReplaceStart);
        Assert.AreEqual("CoSuffix".Length, result.ReplaceLength);
        Assert.AreEqual(command + "  Copy // retain this comment", Apply(line, result, result.Items[0]));
        Assert.IsNull(session.Edits.Single(edit => edit.Name == "CopyDraft").Method);
        Assert.AreEqual(42, session.Edits.Single(edit => edit.Name == "Copy").Method!.Invoke(null, [21]));
    }

    /// <summary>
    /// A committed alias completes to directly callable CIL while an uncommitted draft is absent from instruction candidates.
    /// </summary>
    [TestMethod]
    public async Task CallAlias_InsertsExecutableReferenceAndExcludesDrafts()
    {
        var session = Value();
        Commit(session, "Copy");
        session.PrepareEdit("Value", "CopyDraft");
        session.AddLine("ldc.i4 21");
        var core = new ReplCore(session, new ReplOptions());
        await using var engine = new InProcessEngine(core);
        const string line = "call CopSuffix // preserve";

        var result = await Complete(engine, line, "call Cop".Length);

        var alias = result.Items.Single(item => item.InsertText == "Copy");
        Assert.DoesNotContain(item => item.Name == "CopyDraft", result.Items);
        Assert.AreEqual("call ".Length, result.ReplaceStart);
        Assert.AreEqual("CopSuffix".Length, result.ReplaceLength);
        var accepted = Apply(line, result, alias);
        Assert.AreEqual("call Copy // preserve", accepted);
        Assert.IsTrue(core.Handle(accepted).Succeeded, Transcript(core));
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Framework member completion retains the requested alias and produces a document that can be committed and executed.
    /// </summary>
    [TestMethod]
    public async Task EditMember_ReplacesTheFullReferenceAndRetainsAlias()
    {
        var core = new ReplCore();
        await using var engine = new InProcessEngine(core);
        const string line = ".edit Math::MaSuffix as Chosen";

        var result = await Complete(engine, line, ".edit Math::Ma".Length);

        var maximum = result.Items.Single(item => item.InsertText.EndsWith("Max(int32, int32)", StringComparison.Ordinal));
        Assert.AreEqual(".edit ".Length, result.ReplaceStart);
        Assert.AreEqual("Math::MaSuffix".Length, result.ReplaceLength);
        var accepted = Apply(line, result, maximum);
        Assert.EndsWith(" as Chosen", accepted);
        var prepared = core.Handle(accepted);
        Assert.IsTrue(prepared.Succeeded, Transcript(core));
        Assert.IsNotNull(prepared.EditDocument);
        Assert.AreEqual("Chosen", prepared.EditDocument.Name);
        foreach (var source in prepared.EditDocument.Source.Split('\n'))
        {
            Assert.IsTrue(core.Handle(source).Succeeded, Transcript(core));
        }

        Assert.AreEqual(42, core.Session.Edits.Single().Method!.Invoke(null, [17, 42]));
    }

    /// <summary>
    /// Command option completion replaces only the option token and leaves arguments and trailing comments intact.
    /// </summary>
    /// <param name="command">The command and preceding arguments.</param>
    /// <param name="prefix">The option prefix before the caret.</param>
    /// <param name="expected">The complete option spelling.</param>
    [TestMethod]
    [DataRow(".diff Copy", "--ra", "--raw")]
    [DataRow(".compare Copy (21)", "--as", "--assert")]
    [DataRow(".compare Copy (21)", "--ti", "--timeout")]
    [DataRow(".compare Copy (21)", "--st", "--stdin")]
    [DataRow(".compare Copy (21)", "--fi", "--files")]
    [DataRow(".dis Copy", "--or", "--original")]
    [DataRow(".disassemble Copy", "--or", "--original")]
    public async Task Options_ReplaceOnlyTheCurrentToken(string command, string prefix, string expected)
    {
        var session = Value();
        Commit(session, "Copy");
        var core = new ReplCore(session, new ReplOptions());
        await using var engine = new InProcessEngine(core);
        var line = command + " " + prefix + "Suffix // retain";

        var result = await Complete(engine, line, command.Length + 1 + prefix.Length);

        Assert.HasCount(1, result.Items);
        Assert.AreEqual(expected, result.Items[0].InsertText);
        Assert.AreEqual(command.Length + 1, result.ReplaceStart);
        Assert.AreEqual(prefix.Length + "Suffix".Length, result.ReplaceLength);
        Assert.AreEqual(command + " " + expected + " // retain", Apply(line, result, result.Items[0]));
    }

    /// <summary>
    /// Scenario completion selects parameterless methods and inserts the exact name accepted by the real comparison capture.
    /// </summary>
    [TestMethod]
    public async Task Scenario_InsertsAUsableNameAndExcludesMethodsRequiringArguments()
    {
        var session = Value();
        Commit(session, "Copy");
        foreach (var source in IlLines.Expand(".method int32 Scenario() { ldc.i4 21; call Copy; ret }",
            ".method int32 ScenarioNeedsArgument(int32 value) { ldarg.0; ret }"))
        {
            session.AddLine(source);
        }

        var core = new ReplCore(session, new ReplOptions());
        await using var engine = new InProcessEngine(core);
        const string line = ".compare Copy using ScSuffix --assert";

        var result = await Complete(engine, line, ".compare Copy using Sc".Length);

        Assert.HasCount(1, result.Items);
        Assert.AreEqual("Scenario", result.Items[0].InsertText);
        Assert.AreEqual(".compare Copy using ".Length, result.ReplaceStart);
        Assert.AreEqual("ScSuffix".Length, result.ReplaceLength);
        var accepted = Apply(line, result, result.Items[0]);
        Assert.AreEqual(".compare Copy using Scenario --assert", accepted);
        var package = ComparisonCapture.Create(session, accepted[".compare ".Length..]);
        Assert.AreEqual("Scenario", package.Original.EntryMethod);
        Assert.AreEqual("Scenario", package.Edited.EntryMethod);
        Assert.IsTrue(package.Assert);
        Assert.AreEqual(42, session.Methods.Single(method => method.Signature.Name == "Scenario").Version.Body.Invoke(null, null));
    }

    /// <summary>
    /// Every draft remains reachable across pages and preparing another draft invalidates the old continuation cursor.
    /// </summary>
    [TestMethod]
    public async Task Names_PageAllDraftsAndRejectACursorAfterMutation()
    {
        // Other sessions can update shared assembly bindings and invalidate a paging cursor.
        var session = Value();
        var names = Enumerable.Range(0, CompletionReply.PageSize + 5)
            .Select(index => "Edit" + index.ToString("D3", CultureInfo.InvariantCulture)).ToArray();
        foreach (var name in names)
        {
            session.PrepareEdit("Value", name);
        }

        var core = new ReplCore(session, new ReplOptions());
        await using var engine = new InProcessEngine(core);
        const string line = ".diff Edit";
        var request = new CompletionRequest([line], 0, line.Length, null, [], true);

        var first = await engine.CompleteAsync(request, TestContext.CancellationToken);
        Assert.HasCount(CompletionReply.PageSize, first.Items);
        Assert.IsNotNull(first.Cursor);
        var second = await engine.CompleteAsync(request with { Cursor = first.Cursor }, TestContext.CancellationToken);

        Assert.HasCount(5, second.Items);
        Assert.IsNull(second.Cursor);
        Assert.AreEqual(first.QueryId, second.QueryId);
        Assert.AreEqual(first.BindingEpoch, second.BindingEpoch);
        Assert.AreSequenceEqual(names, first.Items.Concat(second.Items).Select(item => item.InsertText));
        Assert.AreEqual(names.Length, second.Total);
        var mutation = await engine.HandleAsync(".edit Value as EditNew", TestContext.CancellationToken);
        Assert.IsTrue(mutation.Succeeded, Transcript(core));
        var stale = await engine.CompleteAsync(request with { Cursor = first.Cursor }, TestContext.CancellationToken);
        Assert.AreEqual(-1, stale.Total);
        Assert.IsEmpty(stale.Items);
        Assert.HasCount(names.Length + 1, session.Edits);
    }

    private static Session Value() => IlLines.Load(".method int32 Value(int32 value) { ldarg.0; ldc.i4.2; mul; ret }");

    private static void Commit(Session session, string name)
    {
        var edit = session.PrepareEdit("Value", name);
        session.CommitEdit(name, edit.Source);
    }

    private Task<CompletionReply> Complete(InProcessEngine engine, string line, int caret) =>
        engine.CompleteAsync(new CompletionRequest([line], 0, caret, null, [], true), TestContext.CancellationToken);

    private static string Apply(string line, CompletionReply reply, CompletionItem item) =>
        line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..];

    private static string Transcript(ReplCore core) => string.Join("\n", core.Transcript.Lines.Select(line => line.PlainText));
}
