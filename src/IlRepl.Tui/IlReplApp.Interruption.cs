using System.Diagnostics;
using Hex1b.Input;
using Hex1b.Tokens;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Connects terminal interrupts to cooperative cancellation and explicitly confirmed runtime replacement.
/// </summary>
public static partial class IlReplApp
{
    private static TimeSpan InterruptTime => Stopwatch.GetElapsedTime(0);

    private static void ObserveInterruptNotice(PromptState prompt, IReadOnlyList<AppliedToken> tokens)
    {
        foreach (var marker in tokens.Select(token => token.Token).OfType<OscToken>().Where(token => token.Command == "7777"))
        {
            var fields = marker.Payload.Split(':');
            if (fields.Length == 3 && fields[0] == "ilrepl-interrupt" && long.TryParse(fields[2], out var sequence))
                prompt.Interruption.FrameFlushed(fields[1], sequence);
        }
    }

    private static void ConfigureInterruption(PromptState prompt, IReplEngine engine)
    {
        if (engine is IInterruptibleEngine interruptible)
        {
            prompt.Interruption.Update(interruptible.Progress, InterruptTime);
            interruptible.ProgressChanged += progress =>
            {
                prompt.Interruption.Update(progress, InterruptTime);
                prompt.Invalidate?.Invoke();
                if (progress.CancellationRequested && progress.IsRunning) _ = RefreshInterruptNoticeAsync(prompt, progress);
            };
            prompt.Interrupt = () =>
            {
                if (engine is SessionController { RuntimeState: SessionRuntimeState.Starting } starting)
                {
                    starting.CancelStartup();
                    return true;
                }
                var action = prompt.Interruption.Press(InterruptTime, out var progress);
                if (action == InterruptAction.None) return false;
                prompt.Submission?.Cancel();
                if (action is InterruptAction.Cancel or InterruptAction.Restart)
                    _ = ApplyInterruptAsync(prompt, engine, interruptible, action, progress);
                prompt.Invalidate?.Invoke();
                return true;
            };
        }
        if (engine is SessionController controller)
        {
            controller.OutputReceived += output => prompt.Post(new SubmissionEvent(SubmissionEventKind.Lines) { Output = output });
            controller.RuntimeStateChanged += _ => prompt.Invalidate?.Invoke();
            controller.SupervisionChanged += _ => prompt.Invalidate?.Invoke();
            controller.RecoveryCompleted += result => prompt.Post(new SubmissionEvent(SubmissionEventKind.SessionDocument,
                result.Reply.Lines) { SessionEditor = result.Document.Editor, RuntimeRecovery = true });
        }
    }

    private static async Task RetrySupervisionAsync(PromptState prompt, IProcessSupervision supervision)
    {
        try
        {
            await supervision.RetrySupervisionAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ReplEngineException or InvalidOperationException)
        {
            prompt.Post(SubmissionEvent.Reply([TranscriptLine.Of(LineKind.Error,
                "  could not restore process supervision: " + exception.Message, SpanStyle.Error)]));
        }
        finally { prompt.Invalidate?.Invoke(); }
    }

    private static async Task ApplyInterruptAsync(PromptState prompt, IReplEngine engine, IInterruptibleEngine interruptible,
        InterruptAction action, ExecutionProgress progress)
    {
        try
        {
            if (action == InterruptAction.Restart && engine is SessionController controller)
                await controller.EscalateAsync(progress.Identity, progress.Sequence, CancellationToken.None).ConfigureAwait(false);
            else if (action == InterruptAction.Cancel)
                await interruptible.InterruptAsync(progress.Identity, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ReplEngineException or IOException or InvalidOperationException
            or OperationCanceledException)
        {
            prompt.Post(SubmissionEvent.Reply([TranscriptLine.Of(LineKind.Error, "  " + exception.Message, SpanStyle.Error)]));
        }
    }

    private static async Task RefreshInterruptNoticeAsync(PromptState prompt, ExecutionProgress progress)
    {
        var delay = progress.Phase switch
        {
            ExecutionPhase.UserCode => TimeSpan.FromMilliseconds(250),
            ExecutionPhase.CannotStop => TimeSpan.Zero,
            _ => TimeSpan.FromSeconds(5),
        };
        await Task.Delay(delay).ConfigureAwait(false);
        prompt.Invalidate?.Invoke();
    }

    private static bool FilterPromptInput(PromptState prompt, Hex1bEvent input)
    {
        // Selecting an empty document leaves a zero-length anchor in Hex1b. Clear it before insertion
        // so the inserted character does not become a selection that the next character replaces.
        if (input is Hex1bPasteEvent or Hex1bKeyEvent { Text.Length: > 0 } && !prompt.Editor.Cursor.HasSelection)
            prompt.Editor.Cursor.ClearSelection();
        if (input is not Hex1bKeyEvent { Key: Hex1bKey.C, Modifiers: Hex1bModifiers.Control }) prompt.Interruption.Edited();
        return prompt.FilterInput?.Invoke(input) == true;
    }
}
