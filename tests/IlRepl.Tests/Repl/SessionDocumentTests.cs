using System.Reflection;
using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Session documents retain original source and historical results while reopening without executing user code.
/// </summary>
[TestClass]
public sealed class SessionDocumentTests
{
    /// <summary>
    /// Supplies cancellation for asynchronous completion and recall requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Runtime metadata records the tool's shared informational version rather than the assembly binding version.
    /// </summary>
    [TestMethod]
    public void CaptureSession_RecordsToolInformationalVersion()
    {
        using var core = new ReplCore();

        var document = core.CaptureSession(new SessionEditor());

        var version = typeof(ReplCore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.AreEqual(version, document.Runtime.IlreplVersion);
        var tool = Assembly.Load("ilrepl");
        Assert.AreEqual(tool.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, version);
        Assert.AreNotEqual(typeof(ReplCore).Assembly.GetName().Version!.ToString(), document.Runtime.IlreplVersion);
    }

    /// <summary>
    /// Source lines share a journal entry until a command boundary while snapshots and per-line undo remain intact.
    /// </summary>
    [TestMethod]
    public void CaptureSession_GroupsSourceWithoutChangingSnapshotsOrUndo()
    {
        using var core = new ReplCore();
        Submit(core, ".args (int32 number = 1)");
        var first = core.CaptureSession(new SessionEditor());
        Submit(core, "ldarg number");
        var second = core.CaptureSession(new SessionEditor());
        Submit(core, "ldc.i4 99", ".undo", "ldc.i4 41", "add", "ret");

        var document = core.CaptureSession(new SessionEditor());

        Assert.AreSequenceEqual([SessionEntryKind.Source, SessionEntryKind.Undo, SessionEntryKind.Source, SessionEntryKind.Run],
            document.Entries.Select(entry => entry.Kind));
        Assert.AreEqual(first.Entries[0].Identity, document.Entries[0].Identity);
        Assert.AreSequenceEqual([".args (int32 number = 1)"], Assert.ContainsSingle(first.Entries).Source);
        Assert.AreSequenceEqual([".args (int32 number = 1)", "ldarg number"], Assert.ContainsSingle(second.Entries).Source);
        Assert.AreSequenceEqual([".args (int32 number = 1)", "ldarg number", "ldc.i4 99"], document.Entries[0].Source);
        Assert.AreSequenceEqual([".args (int32 number = 1)", "ldarg number", "ldc.i4 41", "add", "ret"], document.Cells[0].Source);
        using var reopened = Reopen(document);
        Assert.AreSequenceEqual(SessionCodec.Write(document), SessionCodec.Write(reopened.CaptureSession(new SessionEditor())));
        Submit(reopened, "ldarg number", "ret");
        Assert.AreEqual("  = 1 : int32", reopened.Transcript.Lines.Last(line => line.Kind == LineKind.Result).PlainText);
        using var replay = new ReplCore();
        Assert.IsTrue(replay.RunSession(document, [], CancellationToken.None).Succeeded, Transcript(replay));
        Assert.AreEqual("  = 42 : int32", replay.Transcript.Lines.Last(line => line.Kind == LineKind.Result).PlainText);
    }

    /// <summary>
    /// A frontend rollback can interrupt an unsaved source group without dropping its history or restoring withdrawn instructions.
    /// </summary>
    [TestMethod]
    public void CaptureSession_FlushesGroupedSourceAcrossFrontendRollback()
    {
        using var core = new ReplCore();
        Submit(core, "ldc.i4.1");
        var mark = core.Status.Mark;
        Submit(core, "ldc.i4 99");
        Assert.IsTrue(core.Rollback(mark).Succeeded);
        Submit(core, "ldc.i4 41", "add", "ret");

        var document = core.CaptureSession(new SessionEditor());

        Assert.AreSequenceEqual([SessionEntryKind.Source, SessionEntryKind.Rollback, SessionEntryKind.Source, SessionEntryKind.Run],
            document.Entries.Select(entry => entry.Kind));
        Assert.AreSequenceEqual(["ldc.i4.1", "ldc.i4 99"], document.Entries[0].Source);
        Assert.AreSequenceEqual(["ldc.i4.1", "ldc.i4 41", "add", "ret"], Assert.ContainsSingle(document.Cells).Source);
        using var reopened = Reopen(document);
        Assert.AreSequenceEqual(SessionCodec.Write(document), SessionCodec.Write(reopened.CaptureSession(new SessionEditor())));
        using var replay = new ReplCore();
        Assert.IsTrue(replay.RunSession(document, [], CancellationToken.None).Succeeded, Transcript(replay));
        Assert.AreEqual("  = 42 : int32", replay.Transcript.Lines.Last(line => line.Kind == LineKind.Result).PlainText);
    }

    /// <summary>
    /// Reopening and replay reject an occupied engine through the same user-facing exception contract.
    /// </summary>
    /// <param name="run">Whether the document is explicitly replayed rather than reopened.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SessionReplacement_RequiresFreshEngine(bool run)
    {
        using var core = new ReplCore();
        Submit(core, "ldc.i4 42");
        var before = core.CaptureSession(new SessionEditor());

        var exception = Assert.ThrowsExactly<ReplException>(() =>
        {
            if (run)
            {
                _ = core.RunSession(new SessionDocument(), [], CancellationToken.None);
            }
            else
            {
                _ = core.ReopenSession(new SessionDocument());
            }
        });

        Assert.Contains("requires a fresh execution host", exception.Message);
        Assert.AreSequenceEqual(SessionCodec.Write(before), SessionCodec.Write(core.CaptureSession(new SessionEditor())));
    }

    /// <summary>
    /// Repeating an earlier assembly load records that assembly rather than the most recently added different dependency.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public Task CaptureSession_RepeatedAssemblyLoadRetainsTheRequestedImage() =>
        IsolatedTestProcess.WithDirectoryAsync(TestContext, directory =>
        {
            using var fixture = new SessionDependencyFixture();
            var first = fixture.AssemblyName + "First";
            var second = fixture.AssemblyName + "Second";
            fixture.WritePackage(first, "1.0.0", 21);
            fixture.WritePackage(second, "1.0.0", 42);
            var firstImage = fixture.PackageImage(first, "1.0.0");
            var secondImage = fixture.PackageImage(second, "1.0.0");
            var firstPath = Path.Combine(directory, first + ".dll");
            var secondPath = Path.Combine(directory, second + ".dll");
            File.WriteAllBytes(firstPath, firstImage);
            File.WriteAllBytes(secondPath, secondImage);
            using var core = new ReplCore();

            Submit(core, ".load \"" + firstPath + "\"", ".load \"" + secondPath + "\"", ".load \"" + firstPath + "\"");

            var document = core.CaptureSession(new SessionEditor());
            Assert.HasCount(3, document.References);
            Assert.AreEqual(firstPath, document.References[2].Request);
            Assert.AreEqual(SessionCodec.Hash(firstImage), Assert.ContainsSingle(document.References[2].Assets).Hash);
            Assert.AreEqual(document.References[2].Identity, document.Entries[2].Reference);
            Assert.HasCount(2, document.Assets);
            return Task.CompletedTask;
        });

    /// <summary>
    /// Standard output and error produced before an exception remain historical across a file round trip without replay.
    /// </summary>
    [TestMethod]
    public void ReopenSession_RetainsOutputBeforeThrowWithoutRepeatingSideEffects()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-output-history-").FullName;
        var marker = Path.Combine(directory, "executed.txt");
        var path = Path.Combine(directory, "failed.ilrepl.json");
        try
        {
            using var core = new ReplCore();
            Submit(core, "ldstr " + LiteralParser.Escape(marker), "ldstr \"once\"", "call File::AppendAllText(string, string)",
                "ldstr \"before failure\"", "call Console::WriteLine(string)", "call Console::get_Error()",
                "ldstr \"error before failure\"", "callvirt System.IO.TextWriter::WriteLine(string)",
                "ldstr \"expected failure\"", "newobj InvalidOperationException::.ctor(string)", "throw");
            Assert.IsFalse(core.Handle("ret").Succeeded);
            Assert.AreEqual("once", File.ReadAllText(marker));
            var original = Assert.ContainsSingle(core.CaptureSession(new SessionEditor()).Cells);
            Assert.AreEqual("failed", original.State);
            Assert.Contains(line => line.Kind == LineKind.Output && line.PlainText.Contains("before failure", StringComparison.Ordinal)
                && line.Spans.Any(span => span.Style == SpanStyle.Output), original.Output);
            Assert.Contains(line => line.Kind == LineKind.Output
                && line.PlainText.Contains("error before failure", StringComparison.Ordinal)
                && line.Spans.Any(span => span.Style == SpanStyle.Error), original.Output);
            File.WriteAllBytes(path, SessionCodec.Write(core.CaptureSession(new SessionEditor())));

            using var reopened = Reopen(SessionCodec.Read(File.ReadAllBytes(path)));
            var historical = Assert.ContainsSingle(reopened.CaptureSession(new SessionEditor()).Cells);

            Assert.AreEqual("once", File.ReadAllText(marker));
            Assert.AreEqual("failed", historical.State);
            Assert.AreSequenceEqual(original.Output.Select(line => line.PlainText), historical.Output.Select(line => line.PlainText));
            Assert.AreSequenceEqual(original.Output.SelectMany(line => line.Spans).Select(span => span.Style),
                historical.Output.SelectMany(line => line.Spans).Select(span => span.Style));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Recalling a completed cell excludes instructions explicitly withdrawn before its successful execution.
    /// </summary>
    /// <param name="source">The experiment containing a withdrawal.</param>
    /// <param name="expected">The retained executable source.</param>
    [TestMethod]
    [DataRow("ldc.i4.1\n.clear\nldc.i4 42\nret", "ldc.i4 42\nret")]
    [DataRow("ldc.i4 40\nldc.i4 99\n.undo\nldc.i4.2\nadd\nret", "ldc.i4 40\nldc.i4.2\nadd\nret")]
    public async Task RecallCell_ExcludesWithdrawnInstructions(string source, string expected)
    {
        var token = TestContext.CancellationToken;
        await using var original = new InProcessEngine();
        foreach (var line in source.Split('\n'))
        {
            Assert.IsTrue((await original.HandleAsync(line, token)).Succeeded, line);
        }

        var recalled = await original.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Cell, Numbers = [1] },
        }, token);

        Assert.IsNotNull(recalled.Reply.SessionEditor);
        Assert.AreSequenceEqual(expected.Split('\n'), recalled.Reply.SessionEditor.Lines);
        await using var fresh = new InProcessEngine();
        HandleReply? result = null;
        foreach (var line in recalled.Reply.SessionEditor.Lines)
        {
            result = await fresh.HandleAsync(line, token);
            Assert.IsTrue(result.Succeeded, string.Join('\n', result.Lines.Select(item => item.PlainText)));
        }

        Assert.IsNotNull(result);
        Assert.Contains(line => line.Kind == LineKind.Result && line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal),
            result.Lines);
    }

    /// <summary>
    /// Command completion offers session verbs and dependency flags without submitting the draft.
    /// </summary>
    /// <param name="line">The source at the completion caret.</param>
    /// <param name="expected">The exact offered words.</param>
    [TestMethod]
    [DataRow(".session ", "cell,cells,open,restart,restore,run,save")]
    [DataRow(".session re", "restart,restore")]
    [DataRow(".session save --e", "--embed")]
    [DataRow(".session open --f", "--force")]
    [DataRow(".session restore --b", "--build")]
    [DataRow(".load --", "--configuration,--framework,--no-build,--reload")]
    public async Task CompleteSessionCommands_OffersExpectedOptionsWithoutMutation(string line, string expected)
    {
        await using var engine = new InProcessEngine();
        var before = engine.Status;

        var result = await engine.CompleteAsync(new CompletionRequest([line], 0, line.Length, null, []),
            TestContext.CancellationToken);

        Assert.AreSequenceEqual(expected.Split(','), result.Items.Select(item => item.InsertText).Order(StringComparer.Ordinal));
        Assert.AreEqual(before, engine.Status);
        Assert.AreEqual(line.LastIndexOf(' ') + 1, result.ReplaceStart);
        Assert.AreEqual(line.Length - result.ReplaceStart, result.ReplaceLength);
    }

    /// <summary>
    /// Original source and results survive scrollback trimming and preserve visible submission numbers.
    /// </summary>
    [TestMethod]
    public void CaptureSession_RetainsOriginalSourceAndResultsBeyondScrollback()
    {
        using var core = new ReplCore(new Session(), new ReplOptions { MaxTranscriptLines = 2 });
        string[] definition = ["  // café 日本語", "", ".method string Greeting() {", "  ldstr \"héllo 世界\"", "ret", "}"];
        Submit(core, definition);
        Submit(core, "call Greeting", "ret");
        Submit(core, ".show", ".methods");

        var document = core.CaptureSession(new SessionEditor());

        Assert.HasCount(2, document.Cells);
        Assert.AreEqual(1, document.Cells[0].Number);
        Assert.AreEqual("definition", document.Cells[0].Kind);
        Assert.AreSequenceEqual(definition, document.Cells[0].Source);
        Assert.AreEqual(2, document.Cells[1].Number);
        Assert.AreEqual("cell", document.Cells[1].Kind);
        Assert.AreSequenceEqual(["call Greeting", "ret"], document.Cells[1].Source);
        Assert.AreEqual("succeeded", document.Cells[1].State);
        Assert.Contains(line => line.Kind == LineKind.Result && line.PlainText.Contains("héllo 世界", StringComparison.Ordinal),
            document.Cells[1].Output);
        Assert.DoesNotContain(line => line.Kind == LineKind.Result, core.Transcript.Lines);
        Assert.HasCount(2, core.Transcript.Lines);

        using var reopened = Reopen(document);
        Assert.AreEqual(3, reopened.CellNumber);
        Assert.HasCount(1, reopened.Session.Methods);
        Assert.IsTrue(reopened.Session.State.IsEmpty);
        Assert.IsEmpty(reopened.Transcript.Lines);
        var capturedAgain = reopened.CaptureSession(new SessionEditor());
        Assert.AreSequenceEqual(document.Cells.Select(cell => cell.Identity), capturedAgain.Cells.Select(cell => cell.Identity));
        Assert.AreSequenceEqual(document.Entries.Select(entry => entry.Identity), capturedAgain.Entries.Select(entry => entry.Identity));
        Submit(reopened, "call Greeting", "ret");
        Assert.Contains("héllo 世界", Transcript(reopened));
    }

    /// <summary>
    /// Editor drafts remain separate from accepted input and preserve their selection and revision.
    /// </summary>
    [TestMethod]
    public void CaptureSession_PreservesUnsentEditorWithoutSubmittingIt()
    {
        using var core = new ReplCore();
        Submit(core, "ldc.i4.1");
        var editor = new SessionEditor { Lines = ["  ldc.i4 42", "// draft λ", ""], Caret = 12, Anchor = 2, Revision = 19 };

        var document = core.CaptureSession(editor);
        using var reopened = Reopen(document);
        var captured = reopened.CaptureSession(document.Editor);

        Assert.AreSequenceEqual(editor.Lines, captured.Editor.Lines);
        Assert.AreEqual(12, captured.Editor.Caret);
        Assert.AreEqual(2, captured.Editor.Anchor);
        Assert.AreEqual(19L, captured.Editor.Revision);
        Assert.AreSequenceEqual(["ldc.i4.1"], reopened.Session.BodyLines);
        Assert.IsEmpty(captured.Cells);
        Submit(reopened, "ret");
        Assert.Contains("= 1 : int32", Transcript(reopened));
    }

    /// <summary>
    /// An unfinished declaration retains its accepted source and can be completed after reopening.
    /// </summary>
    /// <param name="source">The unfinished declaration.</param>
    /// <param name="method">The expected open method name.</param>
    /// <param name="type">The expected open type name.</param>
    /// <param name="completion">The source that completes the declaration.</param>
    [TestMethod]
    [DataRow(".method int32 Value() {\nldc.i4 42", "Value", null, "ret\n}")]
    [DataRow(".class public Box {\n.field public int32 Value", null, "Box", "}")]
    public void ReopenSession_PreservesUnfinishedDeclarations(string source, string? method, string? type, string completion)
    {
        using var core = new ReplCore();
        Submit(core, source.Split('\n'));
        var document = core.CaptureSession(new SessionEditor { Lines = completion.Split('\n'), Caret = 1, Anchor = 1 });

        using var reopened = Reopen(document);

        Assert.AreEqual(method, reopened.Status.OpenMethod);
        Assert.AreEqual(type, reopened.Status.OpenType);
        Assert.AreEqual(core.Status.OpenDepth, reopened.Status.OpenDepth);
        Assert.AreEqual(1, reopened.CellNumber);
        Assert.IsEmpty(document.Cells);
        Submit(reopened, completion.Split('\n'));
        Assert.AreEqual(0, reopened.Status.OpenDepth);
        Assert.AreEqual(2, reopened.CellNumber);
    }

    /// <summary>
    /// Uncommitted edit source remains open while the original callable method remains available.
    /// </summary>
    [TestMethod]
    public void ReopenSession_PreservesUnfinishedEdit()
    {
        using var core = new ReplCore();
        Submit(core, ".method int32 Value() {", "ldc.i4.1", "ret", "}");
        var prepared = core.Handle(".edit Value as Changed");
        Assert.IsNotNull(prepared.EditDocument);
        var lines = prepared.EditDocument.Source.Replace("ldc.i4.1", "ldc.i4.2", StringComparison.Ordinal).Split('\n');
        Submit(core, lines[..^1]);
        var document = core.CaptureSession(new SessionEditor { Lines = [lines[^1]] });

        using var reopened = Reopen(document);

        Assert.AreEqual("Changed", reopened.Status.OpenEdit);
        Assert.AreEqual(core.Status.OpenDepth, reopened.Status.OpenDepth);
        Assert.IsNull(Assert.ContainsSingle(reopened.Session.Edits).Method);
        Submit(reopened, lines[^1]);
        Assert.IsNull(reopened.Status.OpenEdit);
        Submit(reopened, "call Changed", "ret");
        Assert.Contains("= 2 : int32", Transcript(reopened));
    }

    /// <summary>
    /// Reopened argument literals and generic bindings apply to new explicit execution as well as historical cells.
    /// </summary>
    [TestMethod]
    public void ReopenSession_PreservesArgumentsAndTypeArguments()
    {
        using var core = new ReplCore();
        string[] declarations = [".typeparams (T)", ".typeargs (int32)", ".args (int32 number = 42)"];
        Submit(core, declarations);
        Submit(core, "ldarg number", "box int32", "unbox.any !!T", "box !!T", "ret", "ldarg number");
        var document = core.CaptureSession(new SessionEditor());

        using var reopened = Reopen(document);

        var historical = Assert.ContainsSingle(document.Cells);
        foreach (var declaration in declarations)
        {
            Assert.Contains(declaration, historical.Inputs);
        }

        Assert.AreSequenceEqual(core.Session.DeclarationLines, reopened.Session.DeclarationLines);
        Assert.AreSequenceEqual([typeof(int)], reopened.Session.TypeArguments!);
        Assert.AreEqual(2, reopened.CellNumber);
        Submit(reopened, "ret");
        Assert.Contains("= 42 : int32", Transcript(reopened));
    }

    /// <summary>
    /// Clearing and undoing source reconstruct the accepted current body without reviving withdrawn instructions.
    /// </summary>
    [TestMethod]
    public void ReopenSession_AppliesClearAndUndoTransitions()
    {
        using var core = new ReplCore();
        Submit(core, ".args (int32 number = 42)", "ldc.i4.1", ".clear", "ldarg number", "ldc.i4.2", ".undo");
        var document = core.CaptureSession(new SessionEditor());

        using var reopened = Reopen(document);

        Assert.AreSequenceEqual(["ldarg number"], reopened.Session.BodyLines);
        Assert.Contains(entry => entry.Kind == SessionEntryKind.Clear, document.Entries);
        Assert.Contains(entry => entry.Kind == SessionEntryKind.Undo, document.Entries);
        Submit(reopened, "ret");
        Assert.Contains("= 42 : int32", Transcript(reopened));
    }

    /// <summary>
    /// Reset clears active definitions and inputs while preserving numbered history and loaded references.
    /// </summary>
    [TestMethod]
    public void ReopenSession_AppliesResetWithoutDeletingHistoryOrReferences()
    {
        using var core = new ReplCore();
        Submit(core, ".load System.Xml.ReaderWriter", ".method int32 Old() {", "ldc.i4.1", "ret", "}");
        Submit(core, ".typeparams (T)", ".typeargs (int32)", ".args (int32 number = 42)", ".reset", "ldc.i4.3");
        var document = core.CaptureSession(new SessionEditor());

        using var reopened = Reopen(document);

        Assert.HasCount(1, document.Cells);
        Assert.HasCount(1, reopened.References);
        Assert.Contains(assembly => assembly.GetName().Name == "System.Xml.ReaderWriter", reopened.Session.Resolver.LoadedAssemblies);
        Assert.IsEmpty(reopened.Session.Methods);
        Assert.IsEmpty(reopened.Session.DeclarationLines);
        Assert.IsNull(reopened.Session.TypeArguments);
        Assert.AreEqual(2, reopened.CellNumber);
        Submit(reopened, "ret");
        Assert.Contains("= 3 : int32", Transcript(reopened));
    }

    /// <summary>
    /// Rolled-back and rejected provisional input remains historical source without reappearing as accepted instructions.
    /// </summary>
    [TestMethod]
    public void ReopenSession_AppliesRollbackAndRetainsRejectedDraft()
    {
        using var core = new ReplCore();
        Submit(core, "ldc.i4.1");
        var mark = core.Status.Mark;
        Submit(core, ".method int32 Broken() {", "ldstr \"wrong\"");
        Assert.IsFalse(core.Handle("}").Succeeded);
        Assert.IsTrue(core.Rollback(mark).Succeeded, Transcript(core));
        var editor = new SessionEditor { Lines = [".method int32 Broken() {", "ldstr \"wrong\"", "}"] };
        var document = core.CaptureSession(editor);

        using var reopened = Reopen(document);

        Assert.IsNull(reopened.Status.OpenMethod);
        Assert.IsEmpty(reopened.Session.Methods);
        Assert.AreSequenceEqual(["ldc.i4.1"], reopened.Session.BodyLines);
        Assert.Contains(entry => entry.Kind == SessionEntryKind.Rejected && entry.Source[0] == "}", document.Entries);
        Assert.Contains(entry => entry.Kind == SessionEntryKind.Rollback, document.Entries);
        Assert.AreSequenceEqual(editor.Lines, reopened.CaptureSession(document.Editor).Editor.Lines);
        Submit(reopened, "ret");
        Assert.Contains("= 1 : int32", Transcript(reopened));
    }

    /// <summary>
    /// Redefinition after an edit does not replace the captured edit original or its stable identity during reopening.
    /// </summary>
    [TestMethod]
    public void ReopenSession_PreservesRedefinitionAndEditOriginalIdentity()
    {
        using var core = new ReplCore();
        Submit(core, ".method int32 Value() {", "ldc.i4.1", "ret", "}");
        var prepared = core.Handle(".edit Value as Changed");
        Assert.IsNotNull(prepared.EditDocument);
        Submit(core, prepared.EditDocument.Source.Replace("ldc.i4.1", "ldc.i4.2", StringComparison.Ordinal).Split('\n'));
        Submit(core, ".method int32 Value() {", "ldc.i4.3", "ret", "}");
        var originalFingerprint = Assert.ContainsSingle(core.Session.Edits).Fingerprint;
        var document = core.CaptureSession(new SessionEditor());

        using var reopened = Reopen(document);

        var edit = Assert.ContainsSingle(reopened.Session.Edits);
        Assert.AreEqual(originalFingerprint, edit.Fingerprint);
        var preparedAgain = reopened.Handle(".edit Changed");
        Assert.IsNotNull(preparedAgain.EditDocument);
        Assert.AreEqual(prepared.EditDocument.Identity, preparedAgain.EditDocument.Identity);
        Assert.AreEqual(1, edit.Revision);
        Submit(reopened, ".dis Changed --original");
        Assert.Contains("ldc.i4.1", Transcript(reopened));
        var difference = reopened.Handle(".diff Changed");
        Assert.IsNotNull(difference.Diff);
        Assert.Contains(row => row.Kind == "removed" && row.Original == "ldc.i4.1", difference.Diff.Rows);
        Assert.Contains(row => row.Kind == "added" && row.Edited == "ldc.i4.2", difference.Diff.Rows);
        Submit(reopened, "call Value", "ret", "call Changed", "ret");
        Assert.Contains("= 3 : int32", Transcript(reopened));
        Assert.Contains("= 2 : int32", Transcript(reopened));
    }

    /// <summary>
    /// Recalling a committed edit repeatedly preserves a single original instead of creating duplicate named edits.
    /// </summary>
    [TestMethod]
    public void ReopenSession_RepeatedEditRecallRetainsOneOriginal()
    {
        using var core = new ReplCore();
        Submit(core, ".method int32 Value() {", "ldc.i4.1", "ret", "}");
        var prepared = core.Handle(".edit Value as Changed");
        Assert.IsNotNull(prepared.EditDocument);
        Submit(core, prepared.EditDocument.Source.Split('\n'));
        Submit(core, ".edit Changed", ".edit Changed");
        var document = core.CaptureSession(new SessionEditor());

        using var reopened = Reopen(document);

        var edit = Assert.ContainsSingle(reopened.Session.Edits);
        Assert.AreEqual("Changed", edit.Name);
        Assert.AreEqual(1, edit.Revision);
        Submit(reopened, "call Changed", "ret");
        Assert.Contains("= 1 : int32", Transcript(reopened));
    }

    /// <summary>
    /// Reopening previously executed code preserves its output without repeating side effects or running the current cell.
    /// </summary>
    [TestMethod]
    public void ReopenSession_DoesNotExecuteHistoricalOrCurrentCells()
    {
        var marker = Path.Combine(Path.GetTempPath(), "ilrepl-session-cell-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var core = new ReplCore();
            Submit(core, "ldstr \"" + marker.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"", "ldstr \"once\"",
                "call void System.IO.File::AppendAllText(string, string)", "ret", "ldc.i4 42");
            Assert.AreEqual("once", File.ReadAllText(marker));
            var document = core.CaptureSession(new SessionEditor());
            File.Delete(marker);

            using var reopened = Reopen(document);

            Assert.IsFalse(File.Exists(marker), "reopening executed the historical cell");
            Assert.AreEqual(2, reopened.CellNumber);
            Assert.AreEqual(1, reopened.Status.Instructions);
            Assert.IsTrue(reopened.Session.DeferActivation);
            Submit(reopened, "ret");
            Assert.Contains("= 42 : int32", Transcript(reopened));
            Assert.IsFalse(File.Exists(marker), "running the current cell replayed its predecessor");
        }
        finally
        {
            File.Delete(marker);
        }
    }

    /// <summary>
    /// Captured dependency images reopen and remain inspectable without invoking their module or type initializers.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public Task ReopenSession_LoadsCapturedReferenceWithoutInitializingIt() =>
        IsolatedTestProcess.WithDirectoryAsync(TestContext, directory =>
        {
            var marker = Path.Combine(directory, "marker");
            var path = Path.Combine(directory, "source.dll");
            File.WriteAllBytes(path, ModuleInitializerFixture.Create(true, marker));
            using var core = new ReplCore();
            Submit(core, ".load " + path, ".method int32 Read() {", "call int32 Owner::Read()", "ret", "}");
            var document = core.CaptureSession(new SessionEditor());
            File.Delete(marker);
            File.Move(path, path + ".previous");

            using var reopened = Reopen(document);

            Assert.IsFalse(File.Exists(marker), "reopening activated a dependency through definition compilation");
            Submit(reopened, ".dis Read", ".il");
            Assert.IsFalse(File.Exists(marker), "inspection activated the loaded image");
            Submit(reopened, "call Read", "ret");
            Assert.AreEqual("initialized\n", File.ReadAllText(marker));
            Assert.Contains("= 142 : int32", Transcript(reopened));
            return Task.CompletedTask;
        });

    /// <summary>
    /// Runtime failures retain their source and error output and do not execute again during reopening.
    /// </summary>
    [TestMethod]
    public void CaptureSession_RetainsFailedExecution()
    {
        using var core = new ReplCore();
        Submit(core, "ldc.i4.1", "ldc.i4.0", "div");
        Assert.IsFalse(core.Handle("ret").Succeeded);

        var document = core.CaptureSession(new SessionEditor());
        var cell = Assert.ContainsSingle(document.Cells);

        Assert.AreEqual("failed", cell.State);
        Assert.AreSequenceEqual(["ldc.i4.1", "ldc.i4.0", "div", "ret"], cell.Source);
        Assert.Contains(line => line.Kind == LineKind.Error && line.PlainText.Contains("DivideByZero", StringComparison.Ordinal),
            cell.Output);
        using var reopened = Reopen(document);
        Assert.AreEqual(2, reopened.CellNumber);
        Assert.IsTrue(reopened.Session.State.IsEmpty);
        Assert.IsEmpty(reopened.Transcript.Lines);
    }

    /// <summary>
    /// Opening a document uses the recipient's presentation preferences rather than recorded quiet or timing commands.
    /// </summary>
    [TestMethod]
    public void ReopenSession_DoesNotImportPresentationSettings()
    {
        using var core = new ReplCore();
        Submit(core, ".quiet", ".time", "ldc.i4.1");
        var document = core.CaptureSession(new SessionEditor());
        using var reopened = new ReplCore(new Session(), new ReplOptions { EchoStack = true, ShowTiming = false });

        Assert.IsEmpty(reopened.ReopenSession(document));

        Assert.IsTrue(reopened.Options.EchoStack);
        Assert.IsFalse(reopened.Options.ShowTiming);
        Assert.DoesNotContain(entry => entry.Source.Contains(".quiet") || entry.Source.Contains(".time"), document.Entries);
    }

    /// <summary>
    /// Session aliases return typed operations even while an unfinished method owns ordinary source input.
    /// </summary>
    /// <param name="command">The command entered by the user.</param>
    /// <param name="operation">The typed operation expected by the frontend.</param>
    /// <param name="path">The unquoted path expected in the operation.</param>
    [TestMethod]
    [DataRow(".session", SessionOperation.Summary, null)]
    [DataRow(".session save \"my work.ilrepl.json\" --embed", SessionOperation.Save, "my work.ilrepl.json")]
    [DataRow(".save \"my work.ILREPL.JSON\"", SessionOperation.Save, "my work.ILREPL.JSON")]
    [DataRow(".load \"my work.ilrepl.json\" --force", SessionOperation.Open, "my work.ilrepl.json")]
    [DataRow(".session open arbitrary.name", SessionOperation.Open, "arbitrary.name")]
    public void Handle_SessionCommandsAreTypedInsideUnfinishedBlocks(string command, SessionOperation operation, string? path)
    {
        using var core = new ReplCore();
        Submit(core, ".method int32 Pending() {", "ldc.i4.1");
        var before = core.CaptureSession(new SessionEditor());

        var reply = core.Handle(command);

        Assert.IsTrue(reply.Succeeded, Transcript(core));
        Assert.IsNotNull(reply.SessionAction);
        Assert.AreEqual(operation, reply.SessionAction.Operation);
        Assert.AreEqual(path, reply.SessionAction.Path);
        Assert.AreEqual(command.Contains("--embed", StringComparison.Ordinal), reply.SessionAction.Embed);
        Assert.AreEqual(command.Contains("--force", StringComparison.Ordinal), reply.SessionAction.Force);
        Assert.AreEqual("Pending", core.Status.OpenMethod);
        Assert.HasCount(before.Entries.Length, core.CaptureSession(new SessionEditor()).Entries);
    }

    /// <summary>
    /// Comments and string literals containing session command text never produce frontend actions.
    /// </summary>
    [TestMethod]
    public void Handle_CommentsAndStringLiteralsDoNotRequestSessionActions()
    {
        using var core = new ReplCore();
        foreach (var source in new[]
        {
            "// .session open stolen.ilrepl.json",
            "/*",
            ".save hidden.ilrepl.json",
            "*/",
            "ldstr \".session open text.ilrepl.json\"",
        })
        {
            var reply = core.Handle(source);
            Assert.IsTrue(reply.Succeeded, Transcript(core));
            Assert.IsNull(reply.SessionAction, source);
        }

        Submit(core, "ret");
        Assert.Contains(".session open text.ilrepl.json", Transcript(core));
    }

    /// <summary>
    /// Saving during an unfinished edit requests frontend persistence without adding the command to the edit body.
    /// </summary>
    [TestMethod]
    public void Handle_SessionSaveDoesNotBecomeEditSource()
    {
        using var core = new ReplCore();
        Submit(core, ".method int32 Value() {", "ldc.i4.1", "ret", "}");
        var prepared = core.Handle(".edit Value as Changed");
        Assert.IsNotNull(prepared.EditDocument);
        var lines = prepared.EditDocument.Source.Split('\n');
        Submit(core, lines[..^1]);

        var reply = core.Handle(".session save draft.ilrepl.json");

        Assert.IsTrue(reply.Succeeded, Transcript(core));
        Assert.IsNotNull(reply.SessionAction);
        Assert.AreEqual(SessionOperation.Save, reply.SessionAction.Operation);
        Assert.AreEqual("Changed", core.Status.OpenEdit);
        Assert.DoesNotContain(entry => entry.Source.Contains(".session save draft.ilrepl.json"),
            core.CaptureSession(new SessionEditor()).Entries);
        Submit(core, lines[^1], "call Changed", "ret");
        Assert.Contains("= 1 : int32", Transcript(core));
    }

    /// <summary>
    /// Selected visible numbers expand deterministically and invalid or duplicated selections return errors.
    /// </summary>
    [TestMethod]
    public void Handle_SessionRunParsesNumbersAndRejectsInvalidSelections()
    {
        using var core = new ReplCore();
        var selected = core.Handle(".session run 7 2-4");
        Assert.IsTrue(selected.Succeeded, Transcript(core));
        Assert.IsNotNull(selected.SessionAction);
        Assert.AreEqual(SessionOperation.Run, selected.SessionAction.Operation);
        Assert.AreSequenceEqual([2, 3, 4, 7], selected.SessionAction.Numbers);

        foreach (var command in new[]
        {
            ".session run 0",
            ".session run 3-2",
            ".session run 2 1-3",
            ".session cell 1-2",
            ".session save a b",
            ".session open",
            ".session run --force",
        })
        {
            var rejected = core.Handle(command);
            Assert.IsFalse(rejected.Succeeded, command);
            Assert.IsNull(rejected.SessionAction, command);
        }
    }

    private static ReplCore Reopen(SessionDocument document)
    {
        var core = new ReplCore();
        var diagnostics = core.ReopenSession(document);
        Assert.IsEmpty(diagnostics, string.Join('\n', diagnostics));
        return core;
    }

    private static void Submit(ReplCore core, params string[] lines)
    {
        foreach (var line in lines)
        {
            Assert.IsTrue(core.Handle(line).Succeeded, line + "\n" + Transcript(core));
        }
    }

    private static string Transcript(ReplCore core) => string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText));
}
