using IlRepl.Protocol;

namespace IlRepl.Batch;

/// <summary>
/// Runs scripts, expressions, and piped input with physical source coordinates and streams the transcript to a writer.
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
    /// Runs every line and executes pending instructions at EOF unless the latest code action was inspection.
    /// </summary>
    /// <param name="lines">The lines.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The exit code: 0 on success, 1 when any line failed.</returns>
    public async Task<int> RunAsync(IEnumerable<string> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        using var enumerator = lines.GetEnumerator();
        return await RunCoreAsync(_ => ValueTask.FromResult(enumerator.MoveNext() ? enumerator.Current : null),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads interactive or piped batch input without trapping cancellation inside a synchronous console read.
    /// </summary>
    /// <param name="input">The input reader, owned by its caller.</param>
    /// <param name="cancellationToken">Cancels pending input and subsequent execution.</param>
    /// <returns>The completed batch exit code.</returns>
    public Task<int> RunInputAsync(TextReader input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return RunCoreAsync(async token => await Task.Run(() => input.ReadLineAsync(token).AsTask(), CancellationToken.None)
            .WaitAsync(token).ConfigureAwait(false), cancellationToken);
    }

    private async Task<int> RunCoreAsync(Func<CancellationToken, ValueTask<string?>> readLine, CancellationToken cancellationToken)
    {
        var ok = true;
        var sourceIdentity = Guid.NewGuid().ToString("N");
        var lineIndex = 0;
        var suppliedSource = false;
        var suppliedInstructions = false;
        cancellationToken.ThrowIfCancellationRequested();
        while (await readLine(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = _engine.Status;
            var comment = before.Mark.InBlockComment;
            var kind = CilLexer.Classify(line, ref comment, out var text);
            var start = line.Length - line.TrimStart().Length;
            var location = new AnalysisLocation(sourceIdentity, lineIndex++, start, line.Length - start);
            var reply = await _engine.HandleSourceAsync(line, location, cancellationToken).ConfigureAwait(false);
            if (reply.SessionEditor is not null)
            {
                suppliedSource = false;
                suppliedInstructions = false;
            }
            else if (reply.Succeeded && kind == SourceLineKind.Text
                && (!text.StartsWith('.') || _engine.Vocabulary.Directives.Contains(text.Split(' ')[0])))
            {
                suppliedSource = true;
                if (before.OpenMethod is null && before.OpenType is null && before.OpenEdit is null
                    && reply.Status.OpenMethod is null && reply.Status.OpenType is null && reply.Status.OpenEdit is null
                    && reply.Status.CellNumber == before.CellNumber && reply.Status.Instructions > before.Instructions)
                {
                    suppliedInstructions = true;
                }
            }

            if (reply.Status.CellIsEmpty)
            {
                suppliedInstructions = false;
            }

            var command = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (kind == SourceLineKind.Text && command is ".show" or ".list" or ".ls" or ".il"
                or ".dis" or ".disassemble" or ".diff" or ".jit" or ".save" or ".session")
            {
                suppliedInstructions = false;
            }

            ok &= reply.Succeeded;
            Write(reply);
            if (reply.PendingComparison is { } comparison)
            {
                var compared = await _engine.CompareAsync(comparison.Identity, cancellationToken).ConfigureAwait(false);
                ok &= compared.Succeeded;
                Write(compared);
            }

            if (reply.PendingNative is { } native)
            {
                var inspected = await _engine.InspectNativeAsync(native.Identity, cancellationToken).ConfigureAwait(false);
                ok &= inspected.Succeeded;
                Write(inspected);
            }

            if (reply.Quit)
            {
                return ok ? 0 : 1;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var status = _engine.Status;
        if (!suppliedSource)
        {
            return ok ? 0 : 1;
        }

        if (status.OpenEdit is { } edit)
        {
            var message = $"edit {edit} is still open; close it with }}";
            AnsiWriter.Write(_output, new TranscriptLine(LineKind.Error,
                [new TranscriptSpan("  error: ", SpanStyle.Error), new TranscriptSpan(message)]), _color);
            _output.Flush();
            return 1;
        }

        if (status.OpenMethod is { } open)
        {
            // Input that ends inside a .method or .class block cannot be completed on the user's behalf.
            AnsiWriter.Write(_output, new TranscriptLine(LineKind.Error,
                [new TranscriptSpan("  error: ", SpanStyle.Error), new TranscriptSpan($"method {open} is still open; close it with }}")]),
                _color);
            _output.Flush();
            return 1;
        }

        if (status.OpenType is { } openType)
        {
            AnsiWriter.Write(_output, new TranscriptLine(LineKind.Error,
                [new TranscriptSpan("  error: ", SpanStyle.Error),
                new TranscriptSpan($"class {openType} is still open; close it with }}")]), _color);
            _output.Flush();
            return 1;
        }

        if (suppliedInstructions && !status.CellIsEmpty)
        {
            var reply = await _engine.HandleAsync("ret", cancellationToken).ConfigureAwait(false);
            ok &= reply.Succeeded;
            Write(reply);
        }

        return ok ? 0 : 1;
    }

    /// <summary>
    /// Prints a response and any recalled draft without submitting its source for execution.
    /// </summary>
    /// <param name="reply">The response from a batch line or startup session open.</param>
    internal void Write(HandleReply reply)
    {
        foreach (var line in reply.Lines)
        {
            if (line.Kind == LineKind.Input && !_echoInput)
            {
                continue;
            }

            AnsiWriter.Write(_output, line, _color);
        }

        if (reply.EditDocument is { } document)
        {
            _output.WriteLine(document.Source);
        }

        if (reply.SessionEditor is { Lines.Length: > 0 } editor && editor.Lines.Any(line => line.Length != 0))
        {
            _output.WriteLine("  editor draft (not executed)");
            foreach (var line in editor.Lines)
            {
                _output.WriteLine(line);
            }
        }

        _output.Flush();
    }
}
