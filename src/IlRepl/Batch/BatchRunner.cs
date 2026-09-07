using IlRepl.Protocol;

namespace IlRepl.Batch;

/// <summary>
/// Runs lines through an engine without the terminal UI and streams the transcript to a
/// writer. Used for scripts, <c>-e</c>, and piped input. A cell left open at the end of the
/// input is run; a <c>.method</c> block left open is an error.
/// </summary>
public sealed class BatchRunner
{
    private readonly IReplEngine _engine;
    private readonly TextWriter _output;
    private readonly bool _color;
    private readonly bool _echoInput;

    /// <summary>
    /// Initializes a runner.
    /// </summary>
    /// <param name="engine">The engine to drive.</param>
    /// <param name="output">Where the transcript goes.</param>
    /// <param name="color">Whether to emit ANSI colors.</param>
    /// <param name="echoInput">Whether to print each input line with its prompt.</param>
    public BatchRunner(IReplEngine engine, TextWriter output, bool color, bool echoInput)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(output);
        _engine = engine;
        _output = output;
        _color = color;
        _echoInput = echoInput;
    }

    /// <summary>
    /// Runs every line, then runs any cell left open. Returns 0 when every line succeeded.
    /// </summary>
    /// <param name="lines">The lines.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The exit code: 0 on success, 1 when any line failed.</returns>
    public async Task<int> RunAsync(IEnumerable<string> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var ok = true;
        foreach (var line in lines)
        {
            var reply = await _engine.HandleAsync(line, cancellationToken).ConfigureAwait(false);
            ok &= reply.Succeeded;
            Write(reply);
            if (reply.Quit)
            {
                return ok ? 0 : 1;
            }
        }

        var status = _engine.Status;
        if (status.OpenMethod is { } open)
        {
            // Input that ends inside a .method block cannot be completed on the user's behalf.
            AnsiWriter.Write(_output, new TranscriptLine(LineKind.Error,
                [new TranscriptSpan("  error: ", SpanStyle.Error), new TranscriptSpan($"method {open} is still open; close it with }}")]), _color);
            _output.Flush();
            return 1;
        }

        if (!status.CellIsEmpty)
        {
            var reply = await _engine.HandleAsync("ret", cancellationToken).ConfigureAwait(false);
            ok &= reply.Succeeded;
            Write(reply);
        }

        return ok ? 0 : 1;
    }

    private void Write(HandleReply reply)
    {
        foreach (var line in reply.Lines)
        {
            if (line.Kind == LineKind.Input && !_echoInput)
            {
                continue;
            }

            AnsiWriter.Write(_output, line, _color);
        }

        _output.Flush();
    }
}
