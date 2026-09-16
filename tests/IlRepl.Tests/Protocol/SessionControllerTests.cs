using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// The session controller replaces real engines atomically and coordinates editor, storage, and execution decisions.
/// </summary>
[TestClass]
public sealed class SessionControllerTests
{
    /// <summary>
    /// Supplies cancellation to real engine operations and host processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A long unexecuted submission checkpoints at structural boundaries while retaining every line for worker recovery.
    /// </summary>
    /// <param name="instructions">The length of the pasted method body.</param>
    [TestMethod]
    [DataRow(128)]
    [DataRow(512)]
    public async Task Handle_BatchedSourceUsesBoundedCheckpointsWithoutLosingSource(int instructions)
    {
        var token = TestContext.CancellationToken;
        await using var controller = CreateController();
        string[] source = [".method void LongBody() {", .. Enumerable.Repeat("nop", instructions), "ret", "}"];
        var checkpoints = new List<SessionReply>();
        controller.PublishCheckpointAsync = (snapshot, _) =>
        {
            checkpoints.Add(snapshot);
            return Task.CompletedTask;
        };
        for (var index = 0; index < source.Length; index++)
        {
            controller.PendingInput = source[(index + 1)..];
            var reply = await controller.HandleAsync(source[index], token);
            Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(line => line.PlainText)));
        }

        Assert.IsLessThan(10, checkpoints.Count, "Pasting a method must not serialize its growing document for every instruction.");
        Assert.AreSequenceEqual(source, checkpoints[0].Document.Editor.Lines);
        Assert.AreSequenceEqual(source, checkpoints[^1].Document.Entries.SelectMany(entry => entry.Source));
        Assert.AreEqual("definition", Assert.ContainsSingle(checkpoints[^1].Document.Cells).Kind);
        Assert.IsEmpty(checkpoints[^1].Document.Editor.Lines);
    }

    /// <summary>
    /// A continuously waiting assembly observer follows a replay replacement and waits again on its fresh cancellation source.
    /// </summary>
    [TestMethod]
    public async Task Run_AssemblyWatcherObservesReplacementWithoutRepeatedCompletedWaits()
    {
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        await using var controller = new SessionController(new InProcessEngine(),
            _ => Task.FromResult<IReplEngine>(new InProcessEngine()));
        await SubmitAsync(controller, "ldc.i4 42", "ret");
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var version = controller.AssemblyVersion;
        async Task WatchAsync()
        {
            while (true)
            {
                var next = await controller.WaitForAssembliesAsync(version, watching.Token);
                Assert.AreNotEqual(version, next, "A completed assembly wait must report a new catalog version.");
                version = next;
                changed.TrySetResult();
            }
        }

        var observer = WatchAsync();
        try
        {
            var result = await controller.SessionAsync(Request(controller, SessionOperation.Run), TestContext.CancellationToken);
            Assert.IsTrue(result.Reply.Succeeded);
            Assert.Contains(line => line.Kind == LineKind.Result && line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal),
                result.Reply.Lines);
            await changed.Task.WaitAsync(TestContext.CancellationToken);
            Assert.IsFalse(observer.IsCompleted, "The observer should now await a later catalog change.");
        }
        finally
        {
            await watching.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => observer);
        }
    }

    /// <summary>
    /// Noninteractive replacement protects dirty source unless force explicitly authorizes discarding it.
    /// </summary>
    /// <param name="force">Whether replacement is explicitly forced.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Hydrate_ProtectsDirtyWorkspaceUnlessForced(bool force)
    {
        var token = TestContext.CancellationToken;
        var initial = new InProcessEngine();
        var starts = 0;
        await using var controller = new SessionController(initial, _ =>
        {
            starts++;
            return Task.FromResult<IReplEngine>(new InProcessEngine());
        });
        await SubmitAsync(controller, "ldc.i4.1");
        controller.Editor = new SessionEditor { Lines = ["// unsaved draft"], Caret = 3, Anchor = 1, Revision = 7 };
        var originalEditor = controller.Editor;
        var epoch = controller.AssemblyVersion >> 32;
        var incoming = Document("ldc.i4.2");
        var request = Request(controller, SessionOperation.Hydrate, incoming, force: force);

        if (!force)
        {
            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => controller.SessionAsync(request, token));
            Assert.Contains("unsaved", exception.Message);
            Assert.AreEqual(0, starts);
            Assert.AreEqual(epoch, controller.AssemblyVersion >> 32);
            Assert.AreSame(originalEditor, controller.Editor);
            AssertResult(await controller.HandleAsync("ret", token), 1);
        }
        else
        {
            var reply = await controller.SessionAsync(request, token);
            Assert.IsTrue(reply.Reply.Succeeded);
            Assert.AreEqual(1, starts);
            Assert.AreEqual(epoch + 1, controller.AssemblyVersion >> 32);
            Assert.AreSame(incoming.Editor, controller.Editor);
            AssertResult(await controller.HandleAsync("ret", token), 2);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => initial.HandleAsync(".show", token));
        }
    }

    /// <summary>
    /// A rejected replacement disposes its candidate and preserves the existing engine, source, and editor.
    /// </summary>
    [TestMethod]
    public async Task Hydrate_InvalidCandidatePreservesCurrentWorkspace()
    {
        var token = TestContext.CancellationToken;
        var initial = new InProcessEngine();
        InProcessEngine? candidate = null;
        await using var controller = new SessionController(initial, _ =>
        {
            candidate = new InProcessEngine();
            return Task.FromResult<IReplEngine>(candidate);
        });
        await SubmitAsync(controller, "ldc.i4 42");
        controller.Editor = new SessionEditor { Lines = ["// retained editor"], Caret = 5, Anchor = 2, Revision = 11 };
        var editor = controller.Editor;
        var epoch = controller.AssemblyVersion >> 32;
        var invalid = Document("ldc.i4.0") with { Version = 999 };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            controller.SessionAsync(Request(controller, SessionOperation.Hydrate, invalid, force: true), token));

        Assert.AreEqual(epoch, controller.AssemblyVersion >> 32);
        Assert.AreSame(editor, controller.Editor);
        Assert.IsNotNull(candidate);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => candidate.HandleAsync(".show", token));
        var captured = await controller.SessionAsync(Request(controller, SessionOperation.Capture), token);
        Assert.AreSequenceEqual(["ldc.i4 42"], captured.Document.Entries.SelectMany(entry => entry.Source));
        AssertResult(await controller.HandleAsync("ret", token), 42);
    }

    /// <summary>
    /// Replacement retains local presentation preferences, imports the document editor, and advances observable epochs.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Hydrate_PreservesPreferencesAndPublishesNewEpochAndEditor()
    {
        var token = TestContext.CancellationToken;
        await using var controller = CreateController();
        await SubmitAsync(controller, ".quiet on", ".time on");
        var incoming = Document("ldc.i4 42") with
        {
            Editor = new SessionEditor { Lines = ["// incoming", ""], Caret = 7, Anchor = 2, Revision = 13 },
        };
        var oldEpoch = controller.AssemblyVersion >> 32;
        var changed = ObserveReplacementAsync();

        var reply = await controller.SessionAsync(Request(controller, SessionOperation.Hydrate, incoming), token);

        Assert.AreEqual(oldEpoch + 1, (await changed) >> 32);
        Assert.IsFalse(reply.Reply.Status.Mark.EchoStack);
        Assert.IsTrue(reply.Reply.Status.Mark.ShowTiming);
        Assert.AreSequenceEqual(incoming.Editor.Lines, controller.Editor.Lines);
        Assert.AreEqual(7, controller.Editor.Caret);
        Assert.AreEqual(2, controller.Editor.Anchor);
        Assert.AreEqual(13L, controller.Editor.Revision);
        var analysis = await controller.AnalyzeAsync(new AnalysisRequest(["nop"], 0, 3, 9), token);
        Assert.AreEqual(oldEpoch + 1, analysis.AssemblyVersion >> 32);
        Assert.AreEqual(9L, analysis.DocumentVersion);

        async Task<long> ObserveReplacementAsync()
        {
            while ((controller.AssemblyVersion >> 32) == oldEpoch)
            {
                await controller.WaitForAssembliesAsync(controller.AssemblyVersion, token);
            }

            return controller.AssemblyVersion;
        }
    }

    /// <summary>
    /// Canceling the first save path prompt leaves accepted source and the editor untouched.
    /// </summary>
    [TestMethod]
    public async Task Save_CanceledFirstPathPromptPreservesWorkspace()
    {
        var token = TestContext.CancellationToken;
        await using var controller = CreateController();
        await SubmitAsync(controller, "ldc.i4 42");
        controller.Editor = new SessionEditor { Lines = ["// untouched"], Caret = 4, Anchor = 1, Revision = 3 };
        var editor = controller.Editor;
        var prompted = 0;
        controller.RequestPathAsync = (opening, suggested, _) =>
        {
            Assert.IsFalse(opening);
            Assert.AreEqual("session.ilrepl.json", suggested);
            prompted++;
            return Task.FromResult<string?>(null);
        };

        var reply = await controller.SessionAsync(Request(controller, SessionOperation.Save), token);

        Assert.AreEqual(1, prompted);
        Assert.IsNull(reply.Path);
        Assert.IsTrue(reply.Dirty);
        Assert.AreSame(editor, controller.Editor);
        Assert.AreSequenceEqual(["ldc.i4 42"], reply.Document.Entries.SelectMany(entry => entry.Source));
        AssertResult(await controller.HandleAsync("ret", token), 42);
    }

    /// <summary>
    /// Real host storage honors Save, Discard, and Cancel when opening over a modified associated session.
    /// </summary>
    /// <param name="decision">The response to the unsaved-source dialog.</param>
    [TestMethod]
    [DataRow(SessionDecision.Save)]
    [DataRow(SessionDecision.Discard)]
    [DataRow(SessionDecision.Cancel)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Open_ResolvesUnsavedDecisionThroughRealHostStorage(SessionDecision decision)
    {
        var token = TestContext.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ilrepl-controller-save-").FullName;
        try
        {
            var originalPath = Path.Combine(directory, "original.ilrepl.json");
            var incomingPath = Path.Combine(directory, "incoming.ilrepl.json");
            var store = new SessionFileStore(Path.Combine(directory, "cache"));
            await store.WriteAsync(incomingPath, Document("ldc.i4.2"), false, token);
            await using var controller = new SessionController(await HostPaths.StartEngineAsync(token),
                static async cancellation => await HostPaths.StartEngineAsync(cancellation));
            var pathPrompts = 0;
            controller.RequestPathAsync = (opening, suggested, _) =>
            {
                Assert.IsFalse(opening);
                Assert.AreEqual("session.ilrepl.json", suggested);
                pathPrompts++;
                return Task.FromResult<string?>(originalPath);
            };
            await SubmitAsync(controller, "ldc.i4.1");
            var saved = await controller.SessionAsync(Request(controller, SessionOperation.Save), token);
            Assert.AreEqual(originalPath, saved.Path);
            Assert.IsFalse(saved.Dirty);
            await SubmitAsync(controller, ".clear", "ldc.i4.s 9");
            controller.Editor = new SessionEditor { Lines = ["// unsaved"], Caret = 3, Anchor = 1, Revision = 7 };
            var editor = controller.Editor;
            var decisions = 0;
            controller.ConfirmUnsavedAsync = (path, _) =>
            {
                Assert.AreEqual(originalPath, path);
                decisions++;
                return Task.FromResult(decision);
            };
            var epoch = controller.AssemblyVersion >> 32;

            var reply = await controller.HandleAsync(".session open \"" + incomingPath + "\"", token);

            Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(line => line.PlainText)));
            Assert.AreEqual(1, decisions);
            Assert.AreEqual(1, pathPrompts, "saving an associated document must reuse its path");
            Assert.IsNotNull(controller.Workspace);
            var replaced = decision != SessionDecision.Cancel;
            Assert.AreEqual(replaced ? incomingPath : originalPath, controller.Workspace.Path);
            Assert.AreEqual(replaced ? epoch + 1 : epoch, controller.AssemblyVersion >> 32);
            if (replaced)
            {
                Assert.IsEmpty(controller.Editor.Lines);
            }
            else
            {
                Assert.AreSame(editor, controller.Editor);
            }

            var onDisk = await store.ReadAsync(originalPath, token);
            if (decision == SessionDecision.Save)
            {
                Assert.Contains("ldc.i4.s 9", onDisk.Entries.SelectMany(entry => entry.Source));
                Assert.AreSequenceEqual(editor.Lines, onDisk.Editor.Lines);
            }
            else
            {
                Assert.DoesNotContain("ldc.i4.s 9", onDisk.Entries.SelectMany(entry => entry.Source));
                Assert.IsEmpty(onDisk.Editor.Lines);
            }

            AssertResult(await controller.HandleAsync("ret", token), replaced ? 2 : 9);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Quit prompts only for modified file-associated workspaces and respects cancellation.
    /// </summary>
    /// <param name="associated">Whether the workspace has an associated document path.</param>
    /// <param name="dirty">Whether the source changes after its initial load.</param>
    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Quit_PromptsOnlyForDirtyAssociatedSessions(bool associated, bool dirty)
    {
        var token = TestContext.CancellationToken;
        var initial = new InProcessEngine();
        var path = Path.Combine(Path.GetTempPath(), "ilrepl-associated-" + Guid.NewGuid().ToString("N") + ".ilrepl.json");
        if (associated)
        {
            await initial.SessionAsync(new SessionRequest
            {
                Action = new SessionAction { Operation = SessionOperation.Hydrate, Path = path },
                Document = new SessionDocument(),
            }, token);
        }

        await using var controller = CreateController(initial);
        if (dirty)
        {
            await SubmitAsync(controller, "ldc.i4.1");
        }

        var prompts = 0;
        controller.ConfirmUnsavedAsync = (current, _) =>
        {
            Assert.AreEqual(path, current);
            prompts++;
            return Task.FromResult(SessionDecision.Cancel);
        };

        var reply = await controller.HandleAsync(".quit", token);

        Assert.IsTrue(reply.Succeeded);
        Assert.AreEqual(!(associated && dirty), reply.Quit);
        Assert.AreEqual(associated && dirty ? 1 : 0, prompts);
        if (associated && dirty)
        {
            AssertResult(await controller.HandleAsync("ret", token), 1);
        }
    }

    /// <summary>
    /// Explicit run consumes a complete editor draft instead of leaving already executed source ready for resubmission.
    /// </summary>
    [TestMethod]
    public async Task Run_ConsumesCompletedDraftInEditor()
    {
        var token = TestContext.CancellationToken;
        await using var controller = CreateController();
        await SubmitAsync(controller, "ldc.i4 40");
        controller.Editor = new SessionEditor { Lines = ["ldc.i4.2", "add"], Caret = 12, Anchor = 12, Revision = 4 };

        var reply = await controller.SessionAsync(Request(controller, SessionOperation.Run), token);

        AssertResult(reply.Reply, 42);
        Assert.IsEmpty(controller.Editor.Lines);
        Assert.IsEmpty(reply.Document.Editor.Lines);
        var captured = await controller.SessionAsync(Request(controller, SessionOperation.Capture), token);
        Assert.IsEmpty(captured.Document.Editor.Lines);
        Assert.HasCount(1, captured.Document.Cells);
    }

    /// <summary>
    /// Replacing the runtime for explicit execution retains the session's associated file path.
    /// </summary>
    [TestMethod]
    public async Task Run_PreservesFileAssociation()
    {
        var token = TestContext.CancellationToken;
        var initial = new InProcessEngine();
        var path = Path.Combine(Path.GetTempPath(), "ilrepl-run-" + Guid.NewGuid().ToString("N") + ".ilrepl.json");
        await initial.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Hydrate, Path = path },
            Document = Document("ldc.i4 42"),
        }, token);
        await using var controller = CreateController(initial);
        var epoch = controller.AssemblyVersion >> 32;

        var reply = await controller.SessionAsync(Request(controller, SessionOperation.Run), token);

        AssertResult(reply.Reply, 42);
        Assert.AreEqual(path, reply.Path);
        Assert.AreEqual(path, controller.Workspace!.Path);
        Assert.IsTrue(reply.Dirty, "executing the saved current body creates a newly recorded cell");
        Assert.AreEqual(epoch + 1, controller.AssemblyVersion >> 32);
    }

    /// <summary>
    /// Execution waits until the frontend acknowledges its source checkpoint and publishes the completed result afterward.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Handle_WaitsForCheckpointBeforeExecutingUserCode()
    {
        var token = TestContext.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ilrepl-controller-checkpoint-").FullName;
        var marker = Path.Combine(directory, "marker");
        try
        {
            await using var controller = CreateController();
            await SubmitAsync(controller, "ldstr \"" + marker.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"", "ldstr \"ran\"",
                "call void System.IO.File::AppendAllText(string, string)");
            var offered = new TaskCompletionSource<SessionReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var checkpoints = new List<SessionReply>();
            controller.PublishCheckpointAsync = async (snapshot, cancellation) =>
            {
                checkpoints.Add(snapshot);
                offered.TrySetResult(snapshot);
                await accepted.Task.WaitAsync(cancellation);
            };
            var running = controller.HandleAsync("ret", token);
            HandleReply reply;
            try
            {
                var before = await offered.Task.WaitAsync(token);
                Assert.IsFalse(File.Exists(marker));
                Assert.IsFalse(running.IsCompleted);
                Assert.Contains("call void System.IO.File::AppendAllText(string, string)",
                    before.Document.Entries.SelectMany(entry => entry.Source));
                Assert.AreSequenceEqual(["ret"], before.Document.Editor.Lines);
            }
            finally
            {
                accepted.TrySetResult();
                reply = await running;
            }

            Assert.IsTrue(reply.Succeeded);
            Assert.AreEqual("ran", File.ReadAllText(marker));
            Assert.HasCount(2, checkpoints);
            Assert.AreEqual("succeeded", Assert.ContainsSingle(checkpoints[1].Document.Cells).State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Disposal cancels an outstanding frontend path request and completes safely for concurrent and repeated callers.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Dispose_CancelsOutstandingPathRequestAndSupportsRepeatedDisposal()
    {
        var token = TestContext.CancellationToken;
        var initial = new InProcessEngine();
        await using var controller = CreateController(initial);
        var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var path = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        controller.RequestPathAsync = async (_, _, cancellation) =>
        {
            requestToken = cancellation;
            prompted.TrySetResult();
            return await path.Task.WaitAsync(cancellation);
        };
        var opening = controller.SessionAsync(Request(controller, SessionOperation.Open), token);
        try
        {
            await prompted.Task.WaitAsync(token);
            Assert.IsFalse(opening.IsCompleted);

            var first = controller.DisposeAsync().AsTask();
            var concurrent = controller.DisposeAsync().AsTask();
            await Task.WhenAll(first, concurrent).WaitAsync(token);

            Assert.IsTrue(requestToken.IsCancellationRequested);
            await Assert.ThrowsAsync<OperationCanceledException>(() => opening);
            await controller.DisposeAsync().AsTask().WaitAsync(token);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => initial.HandleAsync(".show", token));
        }
        finally
        {
            path.TrySetCanceled(token);
        }
    }

    private static SessionController CreateController(InProcessEngine? initial = null) =>
        new(initial ?? new InProcessEngine(), static _ => Task.FromResult<IReplEngine>(new InProcessEngine()));

    private static SessionRequest Request(SessionController controller, SessionOperation operation, SessionDocument? document = null,
        string? path = null, bool force = false) => new()
    {
        Action = new SessionAction { Operation = operation, Path = path, Force = force },
        Editor = controller.Editor,
        Document = document,
    };

    private static SessionDocument Document(params string[] lines)
    {
        using var source = new ReplCore();
        foreach (var line in lines)
        {
            Assert.IsTrue(source.Handle(line).Succeeded, line);
        }

        return source.CaptureSession(new SessionEditor());
    }

    private async Task SubmitAsync(SessionController engine, params string[] lines)
    {
        foreach (var line in lines)
        {
            var reply = await engine.HandleAsync(line, TestContext.CancellationToken);
            Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(output => output.PlainText)));
        }
    }

    private static void AssertResult(HandleReply reply, int expected)
    {
        Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(line => line.PlainText)));
        Assert.Contains(line => line.Kind == LineKind.Result
            && line.PlainText.Contains("= " + expected + " : int32", StringComparison.Ordinal), reply.Lines);
    }
}
