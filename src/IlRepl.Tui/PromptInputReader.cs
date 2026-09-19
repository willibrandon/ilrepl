using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Hex1b.Input;

namespace IlRepl.Tui;

/// <summary>
/// Orders paste application before later input and preserves control keys grouped with printable text.
/// </summary>
internal sealed class PromptInputReader(
    ChannelReader<Hex1bEvent> source,
    Func<Hex1bEvent, bool> filter,
    Func<PasteContext, bool> take) : ChannelReader<Hex1bEvent>
{
    private TaskCompletionSource? _applied;
    private TaskCompletionSource? _frameReady;
    private PasteContext? _paste;
    private Hex1bKeyEvent? _pendingText;
    private int _textOffset;

    /// <inheritdoc />
    public override Task Completion => source.Completion;

    /// <inheritdoc />
    public override bool TryRead([MaybeNullWhen(false)] out Hex1bEvent item)
    {
        if (_frameReady is { Task.IsCompleted: false })
        {
            item = null;
            return false;
        }

        if (_pendingText is not null)
        {
            item = Filter(TakeTextOrControl());
            return true;
        }

        if (Waiting && !CanCancelPaste())
        {
            item = null;
            return false;
        }

        if (!source.TryRead(out item))
        {
            return false;
        }

        // Hex1b cancels a paste it delivered when Escape arrives. A paste taken here is cancelled the same way.
        if (item is Hex1bKeyEvent { Key: Hex1bKey.Escape } && _paste is { IsCompleted: false, IsCancelled: false } streaming)
        {
            streaming.Cancel();
            item = Hex1bKeyEvent.Plain(Hex1bKey.None);
            return true;
        }

        if (item is Hex1bKeyEvent { Key: Hex1bKey.None } key && FindControl(key.Text, 0) >= 0)
        {
            // Hex1b treats some control bytes inside multi-character text tokens as printable.
            // Keep Unicode text together, while delivering each control byte as its own key.
            _pendingText = key;
            _textOffset = 0;
            item = TakeTextOrControl();
        }

        item = Filter(item);

        // Hex1b delivers a paste to the focused node from a background task, so that frames keep rendering. A frame
        // that rebuilds the palette at that moment can leave the delivery without the editor, and the text with it.
        // A paste meant for the prompt is therefore read here, which does not depend on where focus is. Later input
        // waits for it alone: a paste that travels on through focus has no one here to say when it has landed.
        if (item is Hex1bPasteEvent paste)
        {
            // The gate stands before the read starts, because a paste that has already arrived in full can be applied at once.
            _paste = paste.Paste;
            _applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (take(paste.Paste))
            {
                item = Hex1bKeyEvent.Plain(Hex1bKey.None);
            }
            else
            {
                Applied();
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
    {
        if (_frameReady is { Task.IsCompleted: false } frame)
        {
            await frame.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_pendingText is not null)
        {
            return true;
        }

        if (Waiting)
        {
            // Escape still cancels a streaming paste. Ordinary input waits for the editor update.
            if (!source.TryPeek(out _))
            {
                var available = source.WaitToReadAsync(cancellationToken).AsTask();
                await Task.WhenAny(_applied!.Task, available).WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Waiting && !CanCancelPaste())
            {
                await _applied!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return await source.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Defers queued input until a help transition has rebuilt the focused widget.
    /// </summary>
    internal void PauseUntilFrame() => _frameReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Releases input when the app begins rendering the new help or editor surface.
    /// </summary>
    internal void FrameReady() => _frameReady?.TrySetResult();

    /// <summary>
    /// Releases queued keys after the paste has changed the document and its caret.
    /// </summary>
    public void Applied()
    {
        _paste = null;
        _applied?.TrySetResult();
    }

    /// <summary>
    /// Cancels a paste still streaming when its terminal session stops.
    /// </summary>
    public void Stop()
    {
        _paste?.Cancel();
        _pendingText = null;
        FrameReady();
        Applied();
    }

    private Hex1bKeyEvent TakeTextOrControl()
    {
        var key = _pendingText!;
        Hex1bKeyEvent result;
        var character = key.Text[_textOffset];
        if (character < ' ' || character == '\x7f')
        {
            _textOffset++;
            result = character switch
            {
                '\r' or '\n' => Hex1bKeyEvent.Plain(Hex1bKey.Enter),
                '\t' => Hex1bKeyEvent.Plain(Hex1bKey.Tab),
                '\b' or '\x7f' => Hex1bKeyEvent.Plain(Hex1bKey.Backspace),
                '\x1b' => Hex1bKeyEvent.Plain(Hex1bKey.Escape),
                '\0' => Hex1bKeyEvent.WithCtrl(Hex1bKey.Spacebar),
                >= '\x01' and <= '\x1a' => Hex1bKeyEvent.WithCtrl((Hex1bKey)((int)Hex1bKey.A + character - '\x01')),
                _ => Hex1bKeyEvent.Plain(Hex1bKey.None),
            };
        }
        else
        {
            var end = FindControl(key.Text, _textOffset);
            if (end < 0)
            {
                end = key.Text.Length;
            }

            result = key with { Text = key.Text[_textOffset..end] };
            _textOffset = end;
        }

        if (_textOffset == key.Text.Length)
        {
            _pendingText = null;
        }

        return result;
    }

    private Hex1bEvent Filter(Hex1bEvent item) => filter(item) ? Hex1bKeyEvent.Plain(Hex1bKey.None) : item;

    private static int FindControl(string text, int start)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] < ' ' || text[index] == '\x7f')
            {
                return index;
            }
        }

        return -1;
    }

    private bool Waiting => _applied is { Task.IsCompleted: false };

    private bool CanCancelPaste() => _paste is { IsCompleted: false, IsCancelled: false }
        && source.TryPeek(out var next) && next is Hex1bKeyEvent { Key: Hex1bKey.Escape };
}
