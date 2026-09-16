using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Cell recall respects active declarations and restores missing inputs without executing historical source.
/// </summary>
[TestClass]
public sealed class SessionRecallTests
{
    /// <summary>
    /// Supplies cancellation for the real local engines used by recall and replay.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Removing or narrowing declarations retains continued comments and distinguishes quoted comment markers from comments.
    /// </summary>
    /// <param name="partial">Whether some members of the historical argument declaration are still missing.</param>
    /// <param name="runAll">Whether the recalled editor is run with its saved history in a fresh engine.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RecallCell_PreservesDeclarationCommentsAndQuotedLiterals(bool partial, bool runAll)
    {
        var token = TestContext.CancellationToken;
        await using var engine = new SessionController(new InProcessEngine(),
            _ => Task.FromResult<IReplEngine>(new InProcessEngine()));
        var declaration = ".args (int32 number = 42, " + (partial ? "int32 extra = 35, " : "")
            + "string message = \"/* literal */\")";
        string[] original = [".typeparams (T)", ".typeargs (int32) /* generic", "generic end */", "/* declaration",
            "declaration end */ " + declaration + " /* tail", "tail end */"];
        string[] body = partial
            ? ["ldarg message", "call Console::WriteLine(string)", "ldarg number", "ldarg extra", "add", "ret"]
            : ["ldarg message", "call Console::WriteLine(string)", "ldarg number", "ret"];
        string[] current = [".typeparams (T)", ".typeargs (int32)", partial
            ? ".args (int32 number = 7)" : ".args (int32 number = 7, string message = \"current\")"];
        foreach (var line in original.Concat(body).Concat([".reset"]).Concat(current))
        {
            var accepted = await engine.HandleAsync(line, token);
            Assert.IsTrue(accepted.Succeeded, string.Join('\n', accepted.Lines.Select(item => item.PlainText)));
        }

        var recalled = await engine.HandleAsync(".session cell 1", token);

        Assert.IsTrue(recalled.Succeeded);
        Assert.IsNotNull(recalled.SessionEditor);
        var narrowed = partial ? ".args (int32 extra = 35, string message = \"/* literal */\")" : "";
        string[] expected = ["/* generic", "generic end */", "/* declaration", "declaration end */" + narrowed + "/* tail",
            "tail end */", .. body];
        Assert.AreSequenceEqual(expected, recalled.SessionEditor.Lines);
        var outputs = new List<TranscriptLine>();
        if (runAll)
        {
            var result = await engine.HandleAsync(".session run", token);
            Assert.IsTrue(result.Succeeded, string.Join('\n', result.Lines.Select(item => item.PlainText)));
            outputs.AddRange(result.Lines);
        }
        else
        {
            foreach (var line in recalled.SessionEditor.Lines)
            {
                var result = await engine.HandleAsync(line, token);
                Assert.IsTrue(result.Succeeded, string.Join('\n', result.Lines.Select(item => item.PlainText)));
                outputs.AddRange(result.Lines);
            }
        }

        Assert.AreEqual(partial ? "  = 42 : int32" : "  = 7 : int32", outputs.Last(line => line.Kind == LineKind.Result).PlainText);
        Assert.AreEqual(partial ? "/* literal */" : "current", outputs.Last(line => line.Kind == LineKind.Output).PlainText.Trim());
    }

    /// <summary>
    /// Recall reuses active argument values and restores only missing members of overlapping declaration lists.
    /// </summary>
    /// <param name="partial">Whether the historical declarations contain additional arguments, locals, and generic parameters.</param>
    /// <param name="runAll">Whether execution replays the experiment rather than directly submitting the recalled editor.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RecallCell_ReusesCurrentValuesAndRestoresOnlyMissingDeclarations(bool partial, bool runAll)
    {
        var token = TestContext.CancellationToken;
        await using var engine = new SessionController(new InProcessEngine(),
            _ => Task.FromResult<IReplEngine>(new InProcessEngine()));
        string[] original = partial
            ? [".typeparams (T, U)", ".typeargs (int32, int64)", ".args (int32 'number' = 42, int32 extra = 35)",
                ".locals init (int32 result, int32 spare)"]
            : [".args (int32 number = 42)"];
        string[] current = partial
            ? [".typeparams ( T )", ".typeargs (int32)", ".args ( int32 number = 7 )", ".locals init ( int32 result )"]
            : [".args ( int32 number = 7 )"];
        string[] body = partial
            ? ["ldarg number", "ldarg extra", "add", "stloc spare", "ldloc spare", "box int32", "unbox.any !!T", "box !!T", "ret"]
            : ["ldarg number", "ret"];
        foreach (var line in original.Concat(body).Concat([".reset"]).Concat(current))
        {
            var accepted = await engine.HandleAsync(line, token);
            Assert.IsTrue(accepted.Succeeded, string.Join('\n', accepted.Lines.Select(item => item.PlainText)));
        }

        var recalled = await engine.HandleAsync(".session cell 1", token);

        Assert.IsTrue(recalled.Succeeded);
        Assert.IsNotNull(recalled.SessionEditor);
        var expected = partial
            ? [".typeparams (U)", ".typeargs (int32, int64)", ".args (int32 extra = 35)", ".locals init (int32 spare)", .. body]
            : body;
        Assert.AreSequenceEqual(expected, recalled.SessionEditor.Lines);
        HandleReply? result = null;
        if (runAll) result = await engine.HandleAsync(".session run", token);
        else
        {
            foreach (var line in recalled.SessionEditor.Lines)
            {
                result = await engine.HandleAsync(line, token);
                Assert.IsTrue(result.Succeeded, string.Join('\n', result.Lines.Select(item => item.PlainText)));
            }
        }

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Succeeded, string.Join('\n', result.Lines.Select(item => item.PlainText)));
        Assert.AreEqual(partial ? "  = 42 : int32" : "  = 7 : int32",
            result.Lines.Last(line => line.Kind == LineKind.Result).PlainText);
    }

    /// <summary>
    /// Repeated unnamed declaration lines restore only missing positional slots instead of duplicating active slots.
    /// </summary>
    /// <param name="runAll">Whether execution occurs through a fresh session replay.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecallCell_RestoresMissingAnonymousSlots(bool runAll)
    {
        var token = TestContext.CancellationToken;
        await using var engine = new SessionController(new InProcessEngine(),
            _ => Task.FromResult<IReplEngine>(new InProcessEngine()));
        string[] source = [".args (int32 = 20)", "ldarg.0", "ret", ".args (int32 = 20)", "ldarg.0", "ldarg.1", "add", "ret", ".reset",
            ".args (int32 = 7)"];
        foreach (var line in source) Assert.IsTrue((await engine.HandleAsync(line, token)).Succeeded, line);

        var recalled = await engine.HandleAsync(".session cell 2", token);

        Assert.IsNotNull(recalled.SessionEditor);
        Assert.AreSequenceEqual([".args (int32 = 20)", "ldarg.0", "ldarg.1", "add", "ret"], recalled.SessionEditor.Lines);
        HandleReply? result = null;
        if (runAll) result = await engine.HandleAsync(".session run", token);
        else
        {
            foreach (var line in recalled.SessionEditor.Lines)
            {
                result = await engine.HandleAsync(line, token);
                Assert.IsTrue(result.Succeeded, string.Join('\n', result.Lines.Select(item => item.PlainText)));
            }
        }

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Succeeded, string.Join('\n', result.Lines.Select(item => item.PlainText)));
        Assert.AreEqual("  = 27 : int32", result.Lines.Last(line => line.Kind == LineKind.Result).PlainText);
    }

    /// <summary>
    /// Filtering declaration identities needs only syntax and never resolves unknown types or materializes their initializers.
    /// </summary>
    [TestMethod]
    public void RecallCell_InspectsNamesWithoutBindingTypesOrArgumentValues()
    {
        using var core = new ReplCore();
        Assert.IsTrue(core.Handle(".args (int32 current = 7)").Succeeded);
        var cell = new SessionCell
        {
            Inputs = [".args (UnavailableType current = default, OtherUnavailableType restored = default)"],
            Source = ["ldarg current", "ret"],
        };

        var recalled = core.RecallSessionCell(cell);

        Assert.AreSequenceEqual([".args (OtherUnavailableType restored = default)", "ldarg current", "ret"], recalled);
        Assert.AreEqual(7, Assert.ContainsSingle(core.Session.Cell.Arguments).Value);
        Assert.AreEqual(0, core.Session.Cell.InstructionCount);
    }

    /// <summary>
    /// Unsupported dependency operations name the desktop workflow that can prepare a portable file for the demo.
    /// </summary>
    /// <param name="operation">The desktop dependency operation requested without host tooling.</param>
    [TestMethod]
    [DataRow(SessionOperation.Load)]
    [DataRow(SessionOperation.Restore)]
    public async Task DependencyAction_WithoutHostNamesDesktopWorkflow(SessionOperation operation)
    {
        await using var engine = new InProcessEngine();

        var exception = await Assert.ThrowsExactlyAsync<ReplEngineException>(() => engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = operation },
        }, TestContext.CancellationToken));

        Assert.Contains("desktop ilrepl", exception.Message);
        Assert.Contains(".session save --embed", exception.Message);
        Assert.Contains("open the file in the demo", exception.Message);
        Assert.DoesNotContain("Open and Download controls", exception.Message);
    }

    /// <summary>
    /// A failed draft replay stays an ordinary command error with its echo and accepted source preserved.
    /// </summary>
    [TestMethod]
    public async Task Run_InvalidRecalledDraftPreservesEchoAndSource()
    {
        var token = TestContext.CancellationToken;
        await using var engine = new SessionController(new InProcessEngine(),
            _ => Task.FromResult<IReplEngine>(new InProcessEngine()));
        Assert.IsTrue((await engine.HandleAsync("ldc.i4 42", token)).Succeeded);
        Assert.IsTrue((await engine.HandleAsync("ret", token)).Succeeded);
        Assert.IsTrue((await engine.HandleAsync(".session cell 1", token)).Succeeded);
        engine.Editor = engine.Editor with { Lines = ["not.an.opcode"] };

        var failed = await engine.HandleAsync(".session run", token);

        Assert.IsFalse(failed.Succeeded);
        Assert.AreEqual("il[2]> .session run", failed.Lines[0].PlainText);
        Assert.Contains(line => line.Kind == LineKind.Error && line.PlainText.Contains("not.an.opcode", StringComparison.Ordinal),
            failed.Lines);
        Assert.AreEqual(2, engine.Status.CellNumber);
        Assert.AreSequenceEqual(["not.an.opcode"], engine.Editor.Lines);
        var listed = await engine.HandleAsync(".session cells", token);
        Assert.IsTrue(listed.Succeeded);
        Assert.Contains(line => line.Kind == LineKind.Result && line.PlainText == "  = 42 : int32", listed.Lines);
    }

    /// <summary>
    /// Recalled cells can execute directly or through run-all whether their declarations remain active or were reset.
    /// </summary>
    /// <param name="reset">Whether the original persistent declarations have been reset.</param>
    /// <param name="runAll">Whether the recalled editor is executed with the saved experiment in a fresh host.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RecallCell_ReusesActiveDeclarationsAndRestoresMissingInputs(bool reset, bool runAll)
    {
        var token = TestContext.CancellationToken;
        await using var engine = new SessionController(new InProcessEngine(),
            _ => Task.FromResult<IReplEngine>(new InProcessEngine()));
        string[] inputs = [".typeparams (T)", ".typeargs (int32)", ".args (int32 number = 42)", ".locals init (int32 result)"];
        string[] body = ["ldarg number", "stloc result", "ldloc result", "box int32", "unbox.any !!T", "box !!T", "ret"];
        foreach (var line in inputs.Concat(body))
        {
            var reply = await engine.HandleAsync(line, token);
            Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(item => item.PlainText)));
        }

        if (reset) Assert.IsTrue((await engine.HandleAsync(".reset", token)).Succeeded);

        var recalled = await engine.HandleAsync(".session cell 1", token);

        Assert.IsTrue(recalled.Succeeded);
        Assert.IsNotNull(recalled.SessionEditor);
        Assert.AreSequenceEqual(reset ? inputs.Concat(body) : body, recalled.SessionEditor.Lines);
        if (runAll)
        {
            var ran = await engine.HandleAsync(".session run", token);
            Assert.IsTrue(ran.Succeeded, string.Join('\n', ran.Lines.Select(line => line.PlainText)));
            Assert.AreSequenceEqual(["  = 42 : int32", "  = 42 : int32"],
                ran.Lines.Where(line => line.Kind == LineKind.Result).Select(line => line.PlainText));
        }
        else
        {
            HandleReply? ran = null;
            foreach (var line in recalled.SessionEditor.Lines)
            {
                ran = await engine.HandleAsync(line, token);
                Assert.IsTrue(ran.Succeeded, string.Join('\n', ran.Lines.Select(item => item.PlainText)));
            }

            Assert.IsNotNull(ran);
            Assert.Contains(line => line.Kind == LineKind.Result && line.PlainText == "  = 42 : int32", ran.Lines);
        }
    }

    /// <summary>
    /// Recall preserves a method's own locals even when identical declaration text exists at session scope.
    /// </summary>
    [TestMethod]
    public async Task RecallCell_PreservesDefinitionLocalDeclarations()
    {
        var token = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        const string locals = ".locals init (int32 result)";
        string[] method = [".method int32 Read()", "{", locals, "ldc.i4 42", "stloc result", "ldloc result", "ret", "}"];
        foreach (var line in new[] { locals }.Concat(method))
        {
            Assert.IsTrue((await engine.HandleAsync(line, token)).Succeeded, line);
        }

        var recalled = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Cell, Numbers = [1] },
        }, token);

        Assert.IsNotNull(recalled.Reply.SessionEditor);
        Assert.AreSequenceEqual(method, recalled.Reply.SessionEditor.Lines);
        foreach (var line in recalled.Reply.SessionEditor.Lines)
        {
            Assert.IsTrue((await engine.HandleAsync(line, token)).Succeeded, line);
        }

        Assert.IsTrue((await engine.HandleAsync("call Read", token)).Succeeded);
        var result = await engine.HandleAsync("ret", token);
        Assert.IsTrue(result.Succeeded);
        Assert.Contains(line => line.Kind == LineKind.Result && line.PlainText == "  = 42 : int32", result.Lines);
    }

    /// <summary>
    /// A recalled generic cell restores its historical binding when a later command changed the current binding.
    /// </summary>
    [TestMethod]
    public async Task RecallCell_RestoresChangedTypeArgumentsInSourceOrder()
    {
        var token = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        string[] body = ["ldc.i4 42", "box int32", "unbox.any !!T", "box !!T", "ret"];
        string[] declarations = [".typeparams (T)", ".typeargs (int64)", ".typeargs (int32)"];
        foreach (var line in declarations.Concat(body))
        {
            Assert.IsTrue((await engine.HandleAsync(line, token)).Succeeded, line);
        }

        Assert.IsTrue((await engine.HandleAsync(".typeargs (int64)", token)).Succeeded);
        var recalled = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Cell, Numbers = [1] },
        }, token);

        Assert.IsNotNull(recalled.Reply.SessionEditor);
        Assert.DoesNotContain(".typeparams (T)", recalled.Reply.SessionEditor.Lines);
        string[] expected = [".typeargs (int32)", .. body];
        Assert.AreSequenceEqual(expected, recalled.Reply.SessionEditor.Lines);
        HandleReply? result = null;
        foreach (var line in recalled.Reply.SessionEditor.Lines)
        {
            result = await engine.HandleAsync(line, token);
            Assert.IsTrue(result.Succeeded, string.Join('\n', result.Lines.Select(item => item.PlainText)));
        }

        Assert.IsNotNull(result);
        Assert.Contains(line => line.Kind == LineKind.Result && line.PlainText == "  = 42 : int32", result.Lines);
    }

    /// <summary>
    /// Listing a fresh or unfinished experiment explains why no completed cell can be recalled.
    /// </summary>
    /// <param name="unfinished">Whether a method declaration has been accepted without a closing brace.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cells_ExplainsEmptyAndUnfinishedExperiments(bool unfinished)
    {
        var token = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        if (unfinished) Assert.IsTrue((await engine.HandleAsync(".method int32 Pending() {", token)).Succeeded);

        var listed = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Cells },
        }, token);

        Assert.IsTrue(listed.Reply.Succeeded);
        Assert.Contains(line => line.PlainText == "  no retained cells; the current input has not completed a submission",
            listed.Reply.Lines);
        Assert.IsEmpty(listed.Document.Cells);
        Assert.AreEqual(unfinished ? "Pending" : null, engine.Status.OpenMethod);
    }
}
