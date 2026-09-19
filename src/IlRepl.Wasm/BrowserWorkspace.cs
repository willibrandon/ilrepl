using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Wasm;

/// <summary>
/// Bridges the shared document codec and source checkpoints to the page that owns the execution worker.
/// </summary>
public static partial class BrowserWorkspace
{
    private const int BrowserFileLimit = 8 * 1024 * 1024;
    private static SessionController? s_controller;
    private static PromptState? s_prompt;
    private static bool s_allowLocalRun;
    private static bool s_replayingInput;
    private static string? s_lastEditor;
    private static readonly Queue<TaskCompletionSource<SessionEditor>> InputBarriers = new();
    private static readonly HashSet<string> PublishedAssets = new(StringComparer.Ordinal);
    private static SessionDocument? s_checkpoint;
    private static SessionAsset? s_sample;

    /// <summary>
    /// Reopening findings displayed when the worker's terminal starts.
    /// </summary>
    internal static IReadOnlyList<TranscriptLine> StartupMessages { get; private set; } = [];

    /// <summary>
    /// Reopens initial source and installs acknowledged checkpoints before any terminal input can execute.
    /// </summary>
    /// <param name="controller">The worker's workspace coordinator.</param>
    /// <returns>A task completing after safe reconstruction.</returns>
    public static async Task InitializeAsync(SessionController controller)
    {
        s_controller = controller;
        s_replayingInput = false;
        controller.ExportAsync = (export, token) => DownloadAssembly(export.Path, export.Image).WaitAsync(token);
        StartupMessages = [];
        s_checkpoint = null;
        s_lastEditor = null;
        s_sample = null;
        while (InputBarriers.TryDequeue(out var barrier))
        {
            barrier.TrySetCanceled();
        }

        PublishedAssets.Clear();
        if (File.Exists("/samples/Greeter.dll"))
        {
            var image = await File.ReadAllBytesAsync("/samples/Greeter.dll").ConfigureAwait(false);
            s_sample = new SessionAsset { Hash = SessionCodec.Hash(image), Image = image };
        }

        var preferences = InitialPreferences().Split(',');
        if (preferences[0] == "false")
        {
            await controller.HandleAsync(".quiet on", CancellationToken.None).ConfigureAwait(false);
        }

        if (preferences.ElementAtOrDefault(1) == "true")
        {
            await controller.HandleAsync(".time on", CancellationToken.None).ConfigureAwait(false);
        }

        var source = InitialDocument();
        if (!string.IsNullOrEmpty(source))
        {
            try
            {
                var document = source.StartsWith('#') ? SessionCodec.ReadFragment(source)
                    : SessionCodec.Read(Encoding.UTF8.GetBytes(source));
                var opened = await controller.SessionAsync(new SessionRequest
                {
                    Action = new SessionAction { Operation = SessionOperation.Hydrate, Force = true, Path = InitialPath() },
                    Document = SupplyBundledAssets(document), Editor = document.Editor, AnnounceOpen = InitialAnnounceOpen(),
                }, CancellationToken.None).ConfigureAwait(false);

                StartupMessages = opened.Reply.Lines;
            }
            catch (Exception exception) when (source.StartsWith('#')
                && exception is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                StartupMessages = [TranscriptLine.Of(LineKind.Error, "  cannot open share link: " + exception.Message,
                    SpanStyle.Error)];
            }
        }

        controller.PublishCheckpointAsync = async (reply, cancellationToken) =>
        {
            var document = reply.Document;
            var (delta, prefix) = SessionCheckpointDelta.Create(document, s_checkpoint, PublishedAssets);
            await Checkpoint(JsonSerializer.Serialize(delta, SessionJsonContext.Default.SessionDocument), reply.Path, reply.Dirty,
                controller.Status.Mark.EchoStack, controller.Status.Mark.ShowTiming, reply.PendingSubmission ?? 0,
                JsonSerializer.Serialize(reply.PendingSource, SessionJsonContext.Default.StringArray), prefix,
                string.Join(',', document.Cells.Select(cell => cell.Number)), string.Join(',', document.Assets.Select(asset => asset.Hash)),
                JsonSerializer.Serialize(controller.PendingInput, SessionJsonContext.Default.StringArray),
                JsonSerializer.Serialize(controller.Editor, SessionJsonContext.Default.SessionEditor))
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            s_checkpoint = document;
            PublishedAssets.UnionWith(document.Assets.Select(asset => asset.Hash));
        };

        controller.EditorChanged = editor =>
        {
            var serialized = JsonSerializer.Serialize(editor, SessionJsonContext.Default.SessionEditor);
            if (serialized != s_lastEditor)
            {
                s_lastEditor = serialized;
                EditorChanged(serialized);
            }
        };

        controller.ExternalActionAsync = async (request, cancellationToken) =>
        {
            var document = request.Document!;
            if (request.Action.Operation == SessionOperation.Save)
            {
                var path = request.Action.Path ?? controller.Workspace?.Path ?? "session.ilrepl.json";
                if (!SessionCodec.IsSessionPath(path))
                {
                    path += ".ilrepl.json";
                }

                await PageAction("download", Encoding.UTF8.GetString(SessionCodec.Write(document)), path)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                var lines = new List<TranscriptLine> { TranscriptLine.Of(LineKind.Info, "  downloaded " + path, SpanStyle.Dim) };
                if (request.Action.Embed)
                {
                    lines.Add(TranscriptLine.Of(LineKind.Info,
                        "  browser downloads include available dependency images; --embed is already applied", SpanStyle.Dim));
                }

                return new SessionReply { Document = document, Path = path, Dirty = false,
                    Reply = new HandleReply(true, false, [.. lines], controller.Status) };
            }

            if (request.Action.Operation == SessionOperation.Restart)
            {
                await PageAction("restart", Encoding.UTF8.GetString(SessionCodec.Write(document)), "")
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                return new SessionReply { Document = document, Reply = new HandleReply(true, false, [], controller.Status) };
            }

            if (request.Action.Operation == SessionOperation.Run && !s_allowLocalRun)
            {
                await PageAction("run", Encoding.UTF8.GetString(SessionCodec.Write(document)),
                    string.Join(' ', request.Action.Numbers)).WaitAsync(cancellationToken).ConfigureAwait(false);
                return new SessionReply { Document = document, Reply = new HandleReply(true, false,
                    [TranscriptLine.Of(LineKind.Info, "  starting a fresh runtime to run the saved source", SpanStyle.Dim)],
                    controller.Status) };
            }

            if (request.Action.Operation == SessionOperation.Open)
            {
                throw new ReplEngineException("use the page's Open button to choose a session file");
            }

            if (request.Action.Operation is SessionOperation.Load or SessionOperation.Restore)
            {
                var operation = request.Action.Operation == SessionOperation.Load ? "load the dependency" : "restore the session";
                throw new ReplEngineException("use terminal ilrepl to " + operation
                    + ", then run .session save example.ilrepl.json --embed and use the page's Open button to open that file");
            }

            return null;
        };

        await controller.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Capture }, Editor = controller.Editor,
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Attaches the terminal prompt so explicit page execution can publish transcript and editor updates.
    /// </summary>
    /// <param name="prompt">The active terminal prompt.</param>
    public static void Attach(PromptState prompt)
    {
        s_prompt = prompt;
        prompt.FilterInput = input =>
        {
            if (input is Hex1bKeyEvent { Key: Hex1bKey.Escape })
            {
                CompleteEscapeInput();
            }

            if (input is Hex1bKeyEvent { Key: Hex1bKey.F12 } boundary
                && boundary.Modifiers == (Hex1bModifiers.Control | Hex1bModifiers.Alt | Hex1bModifiers.Shift))
            {
                switch (ReadInputBoundary())
                {
                    case "replay":
                        s_replayingInput = true;
                        break;
                    case "live":
                        s_replayingInput = false;
                        break;
                    case "end":
                        InputApplied(JsonSerializer.Serialize(prompt.CaptureSessionEditor(), SessionJsonContext.Default.SessionEditor));
                        s_replayingInput = false;
                        break;
                    case "capture":
                        if (InputBarriers.TryDequeue(out var barrier))
                        {
                            barrier.TrySetResult(prompt.CaptureSessionEditor());
                        }

                        break;
                }

                return true;
            }

            if (!s_replayingInput && input is Hex1bKeyEvent { Key: Hex1bKey.Enter })
            {
                InputSubmitted();
            }

            if (!s_replayingInput || input is not Hex1bKeyEvent key)
            {
                return false;
            }

            if (key.Key is Hex1bKey.Enter or Hex1bKey.Tab)
            {
                prompt.Editor.InsertText(key.Key == Hex1bKey.Enter ? "\n" : "\t");
                prompt.Invalidate?.Invoke();
                return true;
            }

            // Replacement input only edits the restored draft. Shortcuts cannot run commands or replace it.
            return (key.Modifiers & (Hex1bModifiers.Control | Hex1bModifiers.Alt)) != 0
                || key.Key is Hex1bKey.F1 or Hex1bKey.F2 or Hex1bKey.F3 or Hex1bKey.F4 or Hex1bKey.F5 or Hex1bKey.F6
                    or Hex1bKey.F7 or Hex1bKey.F8 or Hex1bKey.F9 or Hex1bKey.F10 or Hex1bKey.F11 or Hex1bKey.F12
                || key.Key == Hex1bKey.Escape;
        };
    }

    /// <summary>
    /// Services page controls through the same codec and typed engine operations as the terminal.
    /// </summary>
    /// <param name="operation">Capture, share, saved, validate, or an explicit fresh-worker run.</param>
    /// <param name="value">The operation's filename, URL, source document, or selected numbers.</param>
    /// <returns>The serialized document, share URL, or empty acknowledgement.</returns>
    [JSExport]
    public static async Task<string> ExecuteAsync(string operation, string value)
    {
        var controller = s_controller ?? throw new InvalidOperationException("the session is still starting");
        if (operation is "capture" or "share" or "run")
        {
            var barrier = new TaskCompletionSource<SessionEditor>(TaskCreationOptions.RunContinuationsAsynchronously);
            InputBarriers.Enqueue(barrier);
            InputBarrier();
            try
            {
                controller.Editor = await barrier.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                barrier.TrySetCanceled();
                throw new InvalidOperationException("the editor did not respond; wait for the current operation or restart the session");
            }

            if (s_prompt?.Submission is { } submission)
            {
                await submission.Completion.ConfigureAwait(false);
            }
        }

        if (operation == "validate")
        {
            if (!value.StartsWith('#') && Encoding.UTF8.GetByteCount(value) > BrowserFileLimit)
            {
                throw new InvalidDataException("browser session files are limited to 8 MiB; open larger files with terminal ilrepl");
            }

            var document = value.StartsWith('#') ? SessionCodec.ReadFragment(value) : SessionCodec.Read(Encoding.UTF8.GetBytes(value));
            return Encoding.UTF8.GetString(SessionCodec.Write(document));
        }

        if (operation == "saved")
        {
            using var acknowledgement = JsonDocument.Parse(value);
            var path = acknowledgement.RootElement.GetProperty("path").GetString();
            var source = acknowledgement.RootElement.GetProperty("document").GetString()
                ?? throw new InvalidOperationException("the download acknowledgement has no document");
            var downloaded = SessionCodec.Read(Encoding.UTF8.GetBytes(source));
            await controller.SessionAsync(new SessionRequest
            {
                Action = new SessionAction { Operation = SessionOperation.AcknowledgeSave, Path = path },
                Document = downloaded, Editor = controller.Editor,
            }, CancellationToken.None).ConfigureAwait(false);

            return "";
        }

        if (operation == "run")
        {
            s_allowLocalRun = true;
            if (s_prompt is { } running)
            {
                running.SessionBusy = true;
                running.Invalidate?.Invoke();
            }

            try
            {
                var numbers = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                var result = await controller.SessionAsync(new SessionRequest
                {
                    Action = new SessionAction { Operation = SessionOperation.Run, Numbers = numbers }, Editor = controller.Editor,
                }, CancellationToken.None).ConfigureAwait(false);

                s_prompt?.Post(new SubmissionEvent(SubmissionEventKind.SessionDocument, result.Reply.Lines)
                {
                    SessionEditor = result.Reply.SessionEditor,
                });
            }
            finally
            {
                s_allowLocalRun = false;
                if (s_prompt is { } completed)
                {
                    completed.SessionBusy = false;
                    completed.Invalidate?.Invoke();
                }
            }

            return "";
        }

        var captured = await controller.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Capture }, Editor = controller.Editor,
        }, CancellationToken.None).ConfigureAwait(false);

        if (operation == "share")
        {
            return SessionCodec.Share(captured.Document, value);
        }

        return Encoding.UTF8.GetString(SessionCodec.Write(captured.Document));
    }

    private static SessionDocument SupplyBundledAssets(SessionDocument document)
    {
        if (s_sample is not { } sample || document.Assets.Any(asset => asset.Hash == sample.Hash)
            || !document.References.SelectMany(reference => reference.Assets).Any(asset => asset.Hash == sample.Hash))
        {
            return document;
        }

        return document with { Assets = [.. document.Assets, sample] };
    }

    [JSImport("initialDocument", "main.js")]
    private static partial string? InitialDocument();

    [JSImport("initialPath", "main.js")]
    private static partial string? InitialPath();

    [JSImport("initialPreferences", "main.js")]
    private static partial string InitialPreferences();

    [JSImport("initialAnnounceOpen", "main.js")]
    private static partial bool InitialAnnounceOpen();

    [JSImport("checkpoint", "main.js")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    private static partial Task Checkpoint(
        string document,
        string? path,
        bool dirty,
        bool echoStack,
        bool showTiming,
        int pendingSubmission,
        string pendingSource,
        int entryPrefix,
        string cellNumbers,
        string assetHashes,
        string pendingInput,
        string editor);

    [JSImport("editorChanged", "main.js")]
    private static partial void EditorChanged(string editor);

    [JSImport("completeEscapeInput", "main.js")]
    private static partial void CompleteEscapeInput();

    [JSImport("readInputBoundary", "main.js")]
    private static partial string ReadInputBoundary();

    [JSImport("inputSubmitted", "main.js")]
    private static partial void InputSubmitted();

    [JSImport("inputApplied", "main.js")]
    private static partial void InputApplied(string editor);

    [JSImport("downloadAssembly", "main.js")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    private static partial Task DownloadAssembly(string path, byte[] image);

    [JSImport("inputBarrier", "main.js")]
    private static partial void InputBarrier();

    [JSImport("pageAction", "main.js")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    private static partial Task PageAction(string operation, string document, string value);
}
