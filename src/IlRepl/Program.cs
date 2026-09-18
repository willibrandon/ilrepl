using System.CommandLine;
using System.Text;
using IlRepl.Batch;
using IlRepl.Processes;
using IlRepl.Protocol;
using IlRepl.Tui;

if (args is ["--lifetime-supervisor", ..]) return await LifetimeSupervisorProgram.RunAsync(args).ConfigureAwait(false);

using var measurements = new ProcessMeasurements("frontend");
await using var lifetime = new HostProcessLifetime();

var evalOption = new Option<string[]>("--eval", "-e")
{
    Description = "Run IL lines separated by ';' and exit. ret runs the cell.",
};
var noColorOption = new Option<bool>("--no-color") { Description = "Plain output without ANSI colors." };
var quietOption = new Option<bool>("--quiet", "-q") { Description = "Do not echo the stack after each instruction." };
var batchOption = new Option<bool>("--batch") { Description = "Read lines from standard input without the terminal UI." };
var noHistoryOption = new Option<bool>("--no-history") { Description = "Do not read or write the history file." };
var sessionOption = new Option<FileInfo?>("--session") { Description = "Reopen an editable session without executing it." };
var runOption = new Option<bool>("--run") { Description = "Explicitly run the opened session from fresh runtime state and exit." };
var scriptArgument = new Argument<FileInfo?>("script")
{
    Description = "An IL script to run, or an .ilrepl.json session to reopen without execution.",
    Arity = ArgumentArity.ZeroOrOne,
};

var root = new RootCommand("Interactive CIL REPL with a live evaluation stack. Type IL, watch the stack, run it.")
{
    evalOption,
    noColorOption,
    quietOption,
    batchOption,
    noHistoryOption,
    sessionOption,
    runOption,
    scriptArgument,
};

root.SetAction(async (parseResult, cancellationToken) =>
{
    try
    {
        var eval = parseResult.GetValue(evalOption) ?? [];
        var script = parseResult.GetValue(scriptArgument);
        var sessionFile = parseResult.GetValue(sessionOption);
        var runSession = parseResult.GetValue(runOption);
        if (script is not null && SessionCodec.IsSessionPath(script.Name))
        {
            if (sessionFile is not null)
            {
                Console.Error.WriteLine("specify one session file");
                return 2;
            }

            sessionFile = script;
            script = null;
        }

        if ((runSession && (sessionFile is null || script is not null || eval.Length != 0))
            || (sessionFile is not null && script is not null))
        {
            Console.Error.WriteLine("--run requires one session file and cannot be combined with a script or --eval");
            return 2;
        }
        var quiet = parseResult.GetValue(quietOption);
        var noHistory = parseResult.GetValue(noHistoryOption);
        var batch = runSession || parseResult.GetValue(batchOption) || Console.IsInputRedirected || eval.Length > 0 || script is not null;
        var color = !parseResult.GetValue(noColorOption)
            && !Console.IsOutputRedirected
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

        if ((script is not null && !script.Exists) || (sessionFile is not null && !sessionFile.Exists))
        {
            Console.Error.WriteLine($"no such file: {(sessionFile ?? script)!.FullName}");
            return 2;
        }

        if (!batch)
        {
            var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<IReplEngine> StartInteractiveAsync(CancellationToken token)
            {
                await firstFrame.Task.WaitAsync(token).ConfigureAwait(false);
                measurements.Mark("host-starting");
                var host = await lifetime.StartAsync(cancellationToken: token).ConfigureAwait(false);
                measurements.Mark("host-ready");
                try
                {
                    if (quiet) await host.HandleAsync(".quiet on", token).ConfigureAwait(false);
                    return host;
                }
                catch
                {
                    await host.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            var initialRequest = sessionFile is null ? null : new SessionRequest
            {
                Action = new SessionAction { Operation = SessionOperation.Open, Path = sessionFile.FullName },
            };
            await using var interactive = new SessionController(StartInteractiveAsync, initialRequest, IlReplApp.TranscriptLineLimit);
            var history = noHistory ? null : new FileHistoryStore(FileHistoryStore.DefaultPath());
            _ = BootstrapCatalog.Hello;
            measurements.Mark("catalog-ready");
            measurements.Mark("frontend-prepared");
            return await IlReplApp.RunAsync(interactive, history, cancellationToken, () =>
            {
                measurements.Mark("prompt-rendered");
                firstFrame.TrySetResult();
            }).ConfigureAwait(false);
        }

        using var consoleCancellation = new BatchConsoleCancellation(cancellationToken);
        cancellationToken = consoleCancellation.Token;
        SessionController engine;
        HostProcessEngine initial;
        try
        {
            initial = await lifetime.StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            engine = new SessionController(
                initial,
                async ct => await lifetime.StartAsync(cancellationToken: ct).ConfigureAwait(false))
            {
                RecoverHostFailures = false,
            };
        }
        catch (HostProtocolException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.Error.WriteLine("ilrepl: " + ex.Message);
            return 3;
        }

        await using (engine.ConfigureAwait(false))
        {
            engine.OutputReceived += output =>
            {
                foreach (var line in output.LeadingLines)
                {
                    if (line.Kind != LineKind.Input || eval.Length == 0) AnsiWriter.Write(Console.Out, line, color);
                }
                Console.Out.Write(output.Text);
                Console.Out.Flush();
            };
            using var interruption = cancellationToken.Register(() => _ = lifetime.TerminateAsync(CancellationToken.None));
            if (quiet)
            {
                await engine.HandleAsync(".quiet on", cancellationToken).ConfigureAwait(false);
            }

            if (sessionFile is not null)
            {
                try
                {
                    if (runSession)
                    {
                        var result = await initial.SessionAsync(new SessionRequest
                        {
                            Action = new SessionAction { Operation = SessionOperation.Open, Path = sessionFile.FullName, Execute = true },
                        }, cancellationToken).ConfigureAwait(false);
                        foreach (var (line, index) in result.Reply.Lines.Select((line, index) => (line, index)))
                        {
                            if (result.Reply.OutputSequence == 0
                                || line.Kind != LineKind.Output && !result.Reply.StreamedLineIndexes.Contains(index))
                                AnsiWriter.Write(Console.Out, line, color);
                        }

                        await engine.DisposeAsync().ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        return result.Reply.Succeeded ? 0 : 1;
                    }

                    var opened = await engine.SessionAsync(new SessionRequest
                    {
                        Action = new SessionAction { Operation = SessionOperation.Open, Path = sessionFile.FullName },
                    }, cancellationToken).ConfigureAwait(false);

                    if (batch)
                    {
                        new BatchRunner(engine, Console.Out, color, echoInput: true).Write(opened.Reply);
                    }
                }
                catch (Exception exception) when (exception is ReplEngineException or IOException or InvalidOperationException)
                {
                    Console.Error.WriteLine("ilrepl: " + exception.Message);
                    cancellationToken.ThrowIfCancellationRequested();
                    return exception is HostProtocolException ? 3
                        : exception is ReplEngineException engineFailure ? engineFailure.ExitCode : 1;
                }
            }

            IEnumerable<string>? lines = null;
            var echo = true;
            if (eval.Length > 0)
            {
                lines = eval.SelectMany(SplitEval);
                echo = false;
            }
            else if (script is not null)
            {
                lines = await File.ReadAllLinesAsync(script.FullName, cancellationToken).ConfigureAwait(false);
            }

            var runner = new BatchRunner(engine, Console.Out, color, echo);
            var exitCode = lines is null
                ? await runner.RunInputAsync(Console.In, cancellationToken).ConfigureAwait(false)
                : await runner.RunAsync(lines, cancellationToken).ConfigureAwait(false);
            // A Windows console interrupt can finish ReadLine as EOF before its signal callback runs.
            // Keep cancellation registered through cleanup and decide the exit status only afterward.
            await engine.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return exitCode;
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        await lifetime.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
        await Console.Out.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        await Console.Error.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        return 130;
    }
    catch (ReplEngineException exception)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            await lifetime.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
            await Console.Out.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await Console.Error.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return 130;
        }
        Console.Error.WriteLine("ilrepl: " + exception.Message);
        return exception.ExitCode;
    }
});

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);

static IEnumerable<string> SplitEval(string text)
{
    var current = new StringBuilder();
    var inString = false;
    for (var i = 0; i < text.Length; i++)
    {
        var c = text[i];
        if (inString && c == '\\' && i + 1 < text.Length)
        {
            current.Append(c).Append(text[++i]);
            continue;
        }

        if (c == '"')
        {
            inString = !inString;
        }

        if (c == ';' && !inString)
        {
            yield return current.ToString();
            current.Clear();
            continue;
        }

        current.Append(c);
    }

    if (current.Length > 0)
    {
        yield return current.ToString();
    }
}
