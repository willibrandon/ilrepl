using Hex1b;
using Hex1b.Documents;
using Hex1b.Input;
using Hex1b.Widgets;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Connects terminal file actions and unsaved-source dialogs to the shared workspace coordinator.
/// </summary>
public static partial class IlReplApp
{
    private static SessionEditor CaptureEditor(PromptState prompt) => prompt.CaptureSessionEditor();

    private static void RestoreEditor(PromptState prompt, SessionEditor editor)
    {
        prompt.SetText(string.Join('\n', editor.Lines), editor.Caret);
        if (editor.Anchor != editor.Caret)
        {
            prompt.Editor.Cursor.SelectionAnchor = new DocumentOffset(editor.Anchor);
        }
    }

    private static void ConfigureSessions(PromptState prompt, SessionController controller)
    {
        controller.RequestPathAsync = async (opening, suggested, cancellationToken) =>
        {
            var dialog = new SessionDialog { IsPath = true, Opening = opening, Path = suggested };
            prompt.SessionDialog = dialog;
            prompt.Invalidate?.Invoke();
            return await dialog.PathResult.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        };
        controller.ConfirmUnsavedAsync = async (path, cancellationToken) =>
        {
            var dialog = new SessionDialog { Path = path ?? "unsaved session" };
            prompt.SessionDialog = dialog;
            prompt.Invalidate?.Invoke();
            return await dialog.Decision.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        };
    }

    private static async Task RunSessionActionAsync(PromptState prompt, SessionController controller, SessionOperation operation)
    {
        if (prompt.SessionBusy || prompt.SessionDialog is not null)
        {
            return;
        }

        prompt.SessionBusy = true;
        prompt.Analyzer?.Cancel();
        prompt.Requester?.Cancel(prompt);
        var editor = CaptureEditor(prompt);
        try
        {
            var reply = await controller.SessionAsync(new SessionRequest
            {
                Action = new SessionAction { Operation = operation },
                Editor = editor,
            }, CancellationToken.None).ConfigureAwait(false);
            prompt.Post(new SubmissionEvent(SubmissionEventKind.SessionDocument, reply.Reply.Lines)
            {
                SessionEditor = reply.Reply.SessionEditor,
                SessionQuit = reply.Reply.Quit,
            });
        }
        catch (Exception exception) when (exception is ReplEngineException or IOException or InvalidOperationException
            or ArgumentException or OperationCanceledException)
        {
            prompt.Post(new SubmissionEvent(SubmissionEventKind.SessionDocument,
                [TranscriptLine.Of(LineKind.Error, "  " + exception.Message, SpanStyle.Error)]));
        }
    }

    private static VStackWidget BuildSessionDialog(RootContext context, Hex1bApp app, PromptState prompt, SessionDialog dialog)
    {
        void Finish(SessionDecision choice, string? path = null)
        {
            if (dialog.Submitted) return;
            dialog.Submitted = true;
            if (choice == SessionDecision.Cancel)
            {
                prompt.SessionDialog = null;
                app.RequestFocus(node => node is EditorNode);
            }
            dialog.PathResult.TrySetResult(path);
            dialog.Decision.TrySetResult(choice);
            app.Invalidate();
        }

        if (!dialog.Focused)
        {
            dialog.Focused = true;
            app.RequestFocus(node => dialog.IsPath ? node is TextBoxNode : node is ButtonNode);
        }
        return context.VStack(v => dialog.IsPath
            ? [
                v.Text(dialog.Opening ? "Open session" : "Save session"),
                v.TextBox(dialog.Path).OnTextChanged(e => dialog.Path = e.NewText)
                    .OnSubmit(_ => Finish(SessionDecision.Save, dialog.Path)),
                v.Text("Enter to continue, Escape to cancel"),
            ]
            : [
                v.Text("Save changes to " + dialog.Path + "?"),
                v.Button("Save").OnClick(_ => Finish(SessionDecision.Save)),
                v.Button("Discard").OnClick(_ => Finish(SessionDecision.Discard)),
                v.Button("Cancel").OnClick(_ => Finish(SessionDecision.Cancel)),
            ])
            .InputBindings(bindings =>
            {
                bindings.Key(Hex1bKey.Escape).Action(_ => Finish(SessionDecision.Cancel), "Cancel");
                if (!dialog.IsPath)
                {
                    bindings.Key(Hex1bKey.UpArrow).Triggers(VStackWidget.FocusPreviousAction);
                    bindings.Key(Hex1bKey.DownArrow).Triggers(VStackWidget.FocusNextAction);
                }
            });
    }
}
