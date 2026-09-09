using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Hex1b.Input;

namespace IlRepl.Tui;

/// <summary>
/// Orders paste application before later input and preserves control keys grouped with printable text.
/// </summary>
internal sealed class PromptInputReader(ChannelReader<Hex1bEvent> source) : ChannelReader<Hex1bEvent>
{
    private TaskCompletionSource? _applied;
    private PasteContext? _paste;
    private Hex1bKeyEvent? _pendingText;
    private int _textOffset;

    /// <inheritdoc />
    public override Task Completion => source.Completion;

    /// <inheritdoc />
    public override bool TryRead([MaybeNullWhen(false)] out Hex1bEvent item)
    {
        if (_pendingText is not null)
        {
            item = TakeTextOrControl();
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

        if (item is Hex1bPasteEvent paste)
        {
            _paste = paste.Paste;
            _applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        else if (item is Hex1bKeyEvent { Key: Hex1bKey.None } key && FindControl(key.Text, 0) >= 0)
        {
            // Hex1b treats some control bytes inside multi-character text tokens as printable.
            // Keep Unicode text together, while delivering each control byte as its own key.
            _pendingText = key;
            _textOffset = 0;
            item = TakeTextOrControl();
        }

        return true;
    }

    /// <inheritdoc />
    public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
    {
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
