using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Explicit session replay validates source before execution and retains a stable history across fresh runtimes.
/// </summary>
[TestClass]
public sealed class SessionReplayTests
{
    /// <summary>
    /// A cell entered after replay retains only its own source when recalled or saved.
    /// </summary>
    [TestMethod]
    public void RunSession_FollowingCellDoesNotRecallEarlierSource()
    {
        using var source = new ReplCore();
        Submit(source, "ldc.i4 21", "ret", "ldc.i4 42", "ret");
        using var replay = new ReplCore();
        Assert.IsTrue(replay.RunSession(source.CaptureSession(new SessionEditor()), [], CancellationToken.None).Succeeded);

        Submit(replay, "ldc.i4 84", "ret");

        var document = replay.CaptureSession(new SessionEditor());
        Assert.HasCount(3, document.Cells);
        Assert.AreSequenceEqual(["ldc.i4 84", "ret"], document.Cells[2].Source);
        Assert.AreSequenceEqual(["  = 21 : int32", "  = 42 : int32", "  = 84 : int32"], Results(replay));
    }

    /// <summary>
    /// Whole-experiment execution applies definitions, replacements, and resets in their recorded source order.
    /// </summary>
    [TestMethod]
    public void RunSession_ReplaysDefinitionsRedefinitionsAndResetInOrder()
    {
        using var source = new ReplCore();
        DefineAndRun(source, 1);
        DefineAndRun(source, 2);
        Submit(source, ".reset");
        DefineAndRun(source, 3);
        var document = source.CaptureSession(new SessionEditor());
        using var replay = new ReplCore();

        var result = replay.RunSession(document, [], CancellationToken.None);

        Assert.IsTrue(result.Succeeded, Transcript(replay));
        Assert.AreSequenceEqual(["  = 1 : int32", "  = 2 : int32", "  = 3 : int32"], Results(replay));
        Assert.AreEqual(7, replay.CellNumber);
        Assert.HasCount(1, replay.Session.Methods);
        AssertRetainedSource(document, replay.CaptureSession(new SessionEditor()));
        Submit(replay, "call Value", "ret");
        Assert.AreEqual("  = 3 : int32", Results(replay).Last());
    }

    /// <summary>
    /// Selected prompt numbers execute in source order while skipped cells never contribute runtime state.
    /// </summary>
    [TestMethod]
    public void RunSession_SelectedCellsUseHistoricalDefinitionsInSourceOrder()
    {
        using var source = new ReplCore();
        DefineAndRun(source, 1);
        DefineAndRun(source, 2);
        Submit(source, ".reset");
        DefineAndRun(source, 3);
        var document = source.CaptureSession(new SessionEditor());
        using var replay = new ReplCore();

        var result = replay.RunSession(document, [6, 2], CancellationToken.None);

        Assert.IsTrue(result.Succeeded, Transcript(replay));
        Assert.AreSequenceEqual(["  = 1 : int32", "  = 3 : int32"], Results(replay));
        AssertRetainedSource(document, replay.CaptureSession(new SessionEditor()));
        Assert.AreEqual(7, replay.CellNumber);
    }

    /// <summary>
    /// Selecting a reader does not silently run an earlier cell that initializes a static field.
    /// </summary>
    [TestMethod]
    public void RunSession_SelectedCellDoesNotExecuteSkippedSetup()
    {
        using var source = new ReplCore();
        Submit(source, ".class public Counter {", ".field public static int32 Value", "}");
        Submit(source, "ldc.i4 42", "stsfld int32 Counter::Value", "ret", "ldsfld int32 Counter::Value", "ret");
        Assert.AreEqual("  = 42 : int32", Results(source).Last());
        var document = source.CaptureSession(new SessionEditor());
        using var replay = new ReplCore();

        var result = replay.RunSession(document, [3], CancellationToken.None);

        Assert.IsTrue(result.Succeeded, Transcript(replay));
        Assert.AreSequenceEqual(["  = 0 : int32"], Results(replay));
        AssertRetainedSource(document, replay.CaptureSession(new SessionEditor()));
    }

    /// <summary>
    /// A complete editor draft extends accepted input and executes once with an explicit or inferred run boundary.
    /// </summary>
    /// <param name="boundary">The explicit editor run boundary, or an empty string for inference.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("ret")]
    [DataRow(".run")]
    public void RunSession_ExecutesAcceptedInputAndCompleteEditorOnce(string boundary)
    {
        using var source = new ReplCore();
        Submit(source, "ldc.i4 40");
        string[] draft = boundary.Length != 0 ? ["ldc.i4.2", "add", boundary] : ["ldc.i4.2", "add"];
        var document = source.CaptureSession(new SessionEditor { Lines = draft });
        using var replay = new ReplCore();

        var result = replay.RunSession(document, [], CancellationToken.None);

        Assert.IsTrue(result.Succeeded, Transcript(replay));
        Assert.AreSequenceEqual(["  = 42 : int32"], Results(replay));
        var captured = replay.CaptureSession(new SessionEditor());
        var cell = Assert.ContainsSingle(captured.Cells);
        Assert.AreEqual(1, cell.Number);
        Assert.AreEqual("succeeded", cell.State);
        Assert.AreSequenceEqual(["ldc.i4 40", "ldc.i4.2", "add", boundary.Length == 0 ? "ret" : boundary], cell.Source);
        Assert.HasCount(3, captured.Entries);
        Assert.AreEqual(document.Entries[0].Identity, captured.Entries[0].Identity);
        Assert.IsEmpty(captured.Editor.Lines);
        Assert.IsTrue(replay.Session.Cell.IsEmpty);
        Assert.AreEqual(2, replay.CellNumber);
    }

    /// <summary>
    /// A newly executed draft records persistent generic bindings and argument declarations for later cell recall.
    /// </summary>
    /// <param name="changeBinding">Whether the draft changes the previously recorded generic binding.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RunSession_CapturesPersistentInputsForNewDraftCell(bool changeBinding)
    {
        using var source = new ReplCore();
        Submit(source, ".typeparams (T)", ".typeargs (int32)", ".args (int32 number = 42)", "ldarg number", "ret");
        string[] draft = changeBinding ? [".typeargs (int64)", "ldarg number"] : ["ldarg number"];
        var document = source.CaptureSession(new SessionEditor { Lines = draft });
        using var replay = new ReplCore();

        var result = replay.RunSession(document, [], CancellationToken.None);

        Assert.IsTrue(result.Succeeded, Transcript(replay));
        var captured = replay.CaptureSession(new SessionEditor());
        Assert.HasCount(2, captured.Cells);
        Assert.Contains(".typeparams (T)", captured.Cells[1].Inputs);
        Assert.Contains(changeBinding ? ".typeargs (int64)" : ".typeargs (int32)", captured.Cells[1].Inputs);
        Assert.Contains(".args (int32 number = 42)", captured.Cells[1].Inputs);
        Assert.AreSequenceEqual(draft.Concat(["ret"]), captured.Cells[1].Source);
        Assert.AreSequenceEqual(["  = 42 : int32", "  = 42 : int32"], Results(replay));
    }

    /// <summary>
    /// A draft containing a complete method declaration commits it before executing a following cell.
    /// </summary>
    [TestMethod]
    public void RunSession_RecordsDefinitionAndExecutableCellFromDraft()
    {
        var document = new SessionDocument
        {
            Editor = new SessionEditor
            {
                Lines = [".method int32 Value() {", "ldc.i4 42", "ret", "}", "call Value", "ret"],
            },
        };
        using var replay = new ReplCore();

        var result = replay.RunSession(document, [], CancellationToken.None);

        Assert.IsTrue(result.Succeeded, Transcript(replay));
        Assert.AreSequenceEqual(["  = 42 : int32"], Results(replay));
        var captured = replay.CaptureSession(new SessionEditor());
        Assert.HasCount(2, captured.Cells);
        Assert.AreEqual("definition", captured.Cells[0].Kind);
        Assert.AreEqual("succeeded", captured.Cells[0].State);
        Assert.AreSequenceEqual([".method int32 Value() {", "ldc.i4 42", "ret", "}"], captured.Cells[0].Source);
        Assert.AreEqual("cell", captured.Cells[1].Kind);
        Assert.AreEqual("succeeded", captured.Cells[1].State);
        Assert.AreSequenceEqual(["call Value", "ret"], captured.Cells[1].Source);
    }

    /// <summary>
    /// Explicit execution can finish an accepted method with editor source before calling the newly committed definition.
    /// </summary>
    [TestMethod]
    public void RunSession_CompletesAcceptedMethodFromEditor()
    {
        using var source = new ReplCore();
        Submit(source, ".method int32 Value() {", "ldc.i4 42");
        var document = source.CaptureSession(new SessionEditor { Lines = ["ret", "}", "call Value", "ret"] });
        using var replay = new ReplCore();

        var result = replay.RunSession(document, [], CancellationToken.None);

        Assert.IsTrue(result.Succeeded, Transcript(replay));
        Assert.AreSequenceEqual(["  = 42 : int32"], Results(replay));
        Assert.IsNull(replay.Status.OpenMethod);
        Assert.AreEqual(3, replay.CellNumber);
    }

    /// <summary>
    /// A complete editable method document is accepted as draft source and its committed copy can run explicitly.
    /// </summary>
    [TestMethod]
    public void RunSession_ExecutesCompleteEditDocumentFromDraft()
    {
        using var source = new ReplCore();
        DefineAndRun(source, 42);
        var prepared = source.Handle(".edit Value as Changed");
        Assert.IsNotNull(prepared.EditDocument);
        var edited = prepared.EditDocument.Source.Replace("ldc.i4 42", "ldc.i4 43", StringComparison.Ordinal);
        var document = source.CaptureSession(new SessionEditor { Lines = [.. edited.Split('\n'), "call Changed", "ret"] });
        using var replay = new ReplCore();

        var result = replay.RunSession(document, [], CancellationToken.None);

        Assert.IsTrue(result.Succeeded, Transcript(replay));
        Assert.AreSequenceEqual(["  = 42 : int32", "  = 43 : int32"], Results(replay));
        var edit = Assert.ContainsSingle(replay.Session.Edits);
        Assert.AreEqual(1, edit.Revision);
        Assert.AreEqual(prepared.EditDocument.BaselineFingerprint, edit.Fingerprint);
    }

    /// <summary>
    /// Incomplete or administrative drafts fail preflight before a historical side-effecting cell can execute.
    /// </summary>
    /// <param name="draft">The invalid editor source supplied with the experiment.</param>
    [TestMethod]
    [DataRow(".method int32 Pending() {\nldc.i4.1")]
    [DataRow("ldc.i4.1\nldc.i4.2")]
    [DataRow("not.an.opcode")]
    [DataRow(".reset")]
    [DataRow(".quit")]
    [DataRow(".session cells")]
    [DataRow(".load missing-assembly.dll")]
    [DataRow(".edit int32 Math::Max(int32, int32)")]
    public void RunSession_InvalidDraftFailsBeforeAnyExecution(string draft)
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-replay-preflight-");
        var marker = Path.Combine(directory.FullName, "marker");
        try
        {
            using var source = new ReplCore();
            AppendMarker(source, marker, "executed");
            var document = source.CaptureSession(new SessionEditor { Lines = draft.Split('\n') });
            File.Delete(marker);
            using var replay = new ReplCore();

            Assert.ThrowsExactly<ReplException>(() => replay.RunSession(document, [], CancellationToken.None));

            Assert.IsFalse(File.Exists(marker), "preflight failure ran an earlier cell");
            Assert.IsEmpty(replay.CaptureSession(new SessionEditor()).Entries);
            Assert.IsEmpty(Results(replay));
            Assert.AreSequenceEqual(draft.Split('\n'), document.Editor.Lines);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>
    /// A forged source entry containing an administrative command is rejected instead of interpreted during replay.
    /// </summary>
    [TestMethod]
    public void RunSession_DoesNotInterpretAdministrativeSourceEntries()
    {
        using var source = new ReplCore();
        DefineAndRun(source, 42);
        var captured = source.CaptureSession(new SessionEditor());
        var document = captured with
        {
            Entries = [.. captured.Entries, new SessionEntry { Number = 3, Source = [".reset"] }],
        };
        using var replay = new ReplCore();

        var exception = Assert.ThrowsExactly<ReplException>(() => replay.RunSession(document, [], CancellationToken.None));

        Assert.Contains("source and dependencies", exception.Message);
        Assert.IsEmpty(Results(replay));
        Assert.IsEmpty(replay.CaptureSession(new SessionEditor()).Entries);
    }

    /// <summary>
    /// Every historical run boundary is validated before execution, including boundaries imported from edited documents.
    /// </summary>
    [TestMethod]
    public void RunSession_InvalidHistoricalBoundaryFailsBeforeAnyExecution()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-replay-history-");
        var marker = Path.Combine(directory.FullName, "marker");
        try
        {
            using var source = new ReplCore();
            AppendMarker(source, marker, "first");
            Submit(source, "ldc.i4.1", "ret");
            var captured = source.CaptureSession(new SessionEditor());
            var boundary = Array.FindLastIndex(captured.Entries, entry => entry.Kind == SessionEntryKind.Run);
            var document = captured with
            {
                Entries = [.. captured.Entries[..boundary], new SessionEntry { Number = 2, Source = ["ldc.i4.2"] },
                    .. captured.Entries[boundary..]],
            };
            File.Delete(marker);
            using var replay = new ReplCore();

            var exception = Assert.ThrowsExactly<ReplException>(() => replay.RunSession(document, [], CancellationToken.None));

            Assert.Contains("stack", exception.Message);
            Assert.IsFalse(File.Exists(marker));
            Assert.IsEmpty(Results(replay));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>
    /// The first runtime failure stops execution, preserves all source, and marks later executable cells unrun.
    /// </summary>
    [TestMethod]
    public void RunSession_StopsAtFirstRuntimeFailureWithoutDiscardingLaterSource()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-replay-failure-");
        var marker = Path.Combine(directory.FullName, "marker");
        try
        {
            using var source = new ReplCore();
            AppendMarker(source, marker, "first");
            Submit(source, "ldc.i4.1", "ldc.i4.0", "div");
            Assert.IsFalse(source.Handle("ret").Succeeded);
            AppendMarker(source, marker, "last");
            Submit(source, "ldc.i4 42");
            var document = source.CaptureSession(new SessionEditor());
            File.Delete(marker);
            using var replay = new ReplCore();

            var result = replay.RunSession(document, [], CancellationToken.None);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("first", File.ReadAllText(marker));
            var captured = replay.CaptureSession(new SessionEditor());
            Assert.AreSequenceEqual(document.Entries.Select(entry => entry.Identity),
                captured.Entries.Take(document.Entries.Length).Select(entry => entry.Identity));
            Assert.AreSequenceEqual(document.Entries.SelectMany(entry => entry.Source),
                captured.Entries.Take(document.Entries.Length).SelectMany(entry => entry.Source));
            Assert.HasCount(4, captured.Cells);
            Assert.AreSequenceEqual(["succeeded", "failed", "unrun", "unrun"], captured.Cells.Select(cell => cell.State));
            Assert.Contains(line => line.Kind == LineKind.Error, captured.Cells[1].Output);
            Assert.AreSequenceEqual(["ldc.i4 42", "ret"], captured.Cells[3].Source);
            Assert.Contains("stopped at cell 2", Transcript(replay));

            Submit(replay, "ldc.i4 84", "ret");
            var continued = replay.CaptureSession(new SessionEditor());
            Assert.AreEqual(5, continued.Cells[^1].Number);
            Assert.AreSequenceEqual(["ldc.i4 84", "ret"], continued.Cells[^1].Source);
            Assert.AreEqual("  = 84 : int32", Results(replay).Last());
            Assert.AreEqual("first", File.ReadAllText(marker));
            SessionCodec.Validate(continued);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>
    /// Invalid typed selections are rejected before source publication or execution.
    /// </summary>
    /// <param name="number">The requested visible prompt number.</param>
    /// <param name="duplicate">Whether the same valid executable number is repeated.</param>
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(-1, false)]
    [DataRow(1, false)]
    [DataRow(99, false)]
    [DataRow(2, true)]
    public void RunSession_RejectsInvalidTypedSelections(int number, bool duplicate)
    {
        using var source = new ReplCore();
        DefineAndRun(source, 42);
        var document = source.CaptureSession(new SessionEditor());
        using var replay = new ReplCore();
        int[] numbers = duplicate ? [number, number] : [number];

        Assert.ThrowsExactly<ReplException>(() => replay.RunSession(document, numbers, CancellationToken.None));

        Assert.IsEmpty(Results(replay));
        Assert.IsEmpty(replay.CaptureSession(new SessionEditor()).Entries);
    }

    /// <summary>
    /// Pre-canceled runs never execute or publish source, including an otherwise empty experiment.
    /// </summary>
    /// <param name="empty">Whether the experiment contains no source.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RunSession_HonorsCancellationBeforeExecution(bool empty)
    {
        using var source = new ReplCore();
        if (!empty)
        {
            DefineAndRun(source, 42);
        }

        var document = source.CaptureSession(new SessionEditor());
        using var replay = new ReplCore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => replay.RunSession(document, [], cancellation.Token));

        Assert.IsEmpty(Results(replay));
        Assert.IsEmpty(replay.CaptureSession(new SessionEditor()).Entries);
    }

    /// <summary>
    /// Successive fresh runs replace historical output without duplicating source or mutating earlier snapshots.
    /// </summary>
    /// <param name="draft">An absent, empty, or whitespace-only editor buffer.</param>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t\n\t ")]
    public void RunSession_RecordsLatestOutputWithoutDuplicatingSource(string? draft)
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-replay-output-");
        var path = Path.Combine(directory.FullName, "value");
        try
        {
            File.WriteAllText(path, "original");
            using var source = new ReplCore();
            Submit(source, "ldstr \"" + Escape(path) + "\"", "call string System.IO.File::ReadAllText(string)", "ret");
            var editor = new SessionEditor { Lines = draft?.Split('\n') ?? [] };
            var document = source.CaptureSession(editor);
            File.WriteAllText(path, "second");
            using var first = new ReplCore();
            Assert.IsTrue(first.RunSession(document, [], CancellationToken.None).Succeeded, Transcript(first));
            var intermediate = first.CaptureSession(editor);
            File.WriteAllText(path, "third");
            using var second = new ReplCore();

            Assert.IsTrue(second.RunSession(intermediate, [], CancellationToken.None).Succeeded, Transcript(second));

            var final = second.CaptureSession(editor);
            AssertRetainedSource(document, intermediate);
            AssertRetainedSource(document, final);
            Assert.Contains("original", Assert.ContainsSingle(document.Cells[0].Output).PlainText);
            Assert.Contains("second", Assert.ContainsSingle(intermediate.Cells[0].Output).PlainText);
            Assert.Contains("third", Assert.ContainsSingle(final.Cells[0].Output).PlainText);
            Assert.DoesNotContain("original", Transcript(second));
            Assert.DoesNotContain("second", Transcript(second));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>
    /// An empty editor adds no source while run-all still executes any pending accepted instructions.
    /// </summary>
    /// <param name="pending">Whether accepted instructions are waiting for execution.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RunSession_BlankEditorDoesNotBecomeSource(bool pending)
    {
        using var source = new ReplCore();
        if (pending)
        {
            Submit(source, "ldc.i4 42");
        }

        var editor = new SessionEditor { Lines = ["", " \t", ""] };
        var document = source.CaptureSession(editor);
        using var replay = new ReplCore();

        Assert.IsTrue(replay.RunSession(document, [], CancellationToken.None).Succeeded, Transcript(replay));

        var captured = replay.CaptureSession(new SessionEditor());
        Assert.AreSequenceEqual(pending ? ["ldc.i4 42", "ret"] : Array.Empty<string>(),
            captured.Entries.SelectMany(entry => entry.Source));
        Assert.AreSequenceEqual(pending ? ["  = 42 : int32"] : Array.Empty<string>(), Results(replay));
        Assert.HasCount(pending ? 1 : 0, captured.Cells);
        Assert.AreEqual(pending ? 2 : 1, replay.CellNumber);
    }

    /// <summary>
    /// Comments make a draft meaningful, retaining its surrounding whitespace when accepted for replay.
    /// </summary>
    [TestMethod]
    public void RunSession_CommentDraftPreservesWhitespace()
    {
        string[] lines = ["", "  // keep this note", " \t", ""];
        var document = new SessionDocument { Editor = new SessionEditor { Lines = lines } };
        using var replay = new ReplCore();

        Assert.IsTrue(replay.RunSession(document, [], CancellationToken.None).Succeeded, Transcript(replay));

        var captured = replay.CaptureSession(new SessionEditor());
        Assert.AreSequenceEqual(lines, captured.Entries.SelectMany(entry => entry.Source));
        Assert.IsEmpty(captured.Cells);
        Assert.IsEmpty(Results(replay));
    }

    private static void DefineAndRun(ReplCore core, int value)
    {
        Submit(core, ".method int32 Value() {", "ldc.i4 " + value, "ret", "}", "call Value", "ret");
    }

    private static void AppendMarker(ReplCore core, string path, string value)
    {
        Submit(core, "ldstr \"" + Escape(path) + "\"", "ldstr \"" + value + "\"",
            "call void System.IO.File::AppendAllText(string, string)", "ret");
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static void AssertRetainedSource(SessionDocument expected, SessionDocument actual)
    {
        Assert.AreSequenceEqual(expected.Entries.Select(entry => entry.Identity), actual.Entries.Select(entry => entry.Identity));
        Assert.AreSequenceEqual(expected.Entries.SelectMany(entry => entry.Source), actual.Entries.SelectMany(entry => entry.Source));
        Assert.AreSequenceEqual(expected.Cells.Select(cell => cell.Identity), actual.Cells.Select(cell => cell.Identity));
        Assert.AreSequenceEqual(expected.Cells.Select(cell => cell.Number), actual.Cells.Select(cell => cell.Number));
        Assert.AreSequenceEqual(expected.Cells.SelectMany(cell => cell.Source), actual.Cells.SelectMany(cell => cell.Source));
    }

    private static void Submit(ReplCore core, params string[] lines)
    {
        foreach (var line in lines)
        {
            Assert.IsTrue(core.Handle(line).Succeeded, line + "\n" + Transcript(core));
        }
    }

    private static IEnumerable<string> Results(ReplCore core) =>
        core.Transcript.Lines.Where(line => line.Kind == LineKind.Result).Select(line => line.PlainText);

    private static string Transcript(ReplCore core) => string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText));
}
