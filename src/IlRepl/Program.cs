using System.CommandLine;
using IlRepl.Batch;
using IlRepl.Hosting;
using IlRepl.Protocol;
using IlRepl.Tui;

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
    var batch = parseResult.GetValue(batchOption) || Console.IsInputRedirected || eval.Length > 0 || script is not null;
    var color = !parseResult.GetValue(noColorOption)
        && !Console.IsOutputRedirected
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    if ((script is not null && !script.Exists) || (sessionFile is not null && !sessionFile.Exists))
    {
        Console.Error.WriteLine($"no such file: {(sessionFile ?? script)!.FullName}");
        return 2;
    }

    SessionController engine;
    HostProcessEngine initial;
    try
    {
        initial = await HostProcessEngine.StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        engine = new SessionController(
            initial,
            async ct => await HostProcessEngine.StartAsync(cancellationToken: ct).ConfigureAwait(false));
    }
    catch (HostProtocolException ex)
    {
        Console.Error.WriteLine("ilrepl: " + ex.Message);
        return 3;
    }

    await using (engine.ConfigureAwait(false))
    {
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
                    foreach (var line in result.Reply.Lines)
                    {
                        AnsiWriter.Write(Console.Out, line, color);
                    }

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
                return exception is HostProtocolException ? 3 : exception is ReplEngineException engineFailure ? engineFailure.ExitCode : 1;
            }
        }

        if (!batch)
        {
            var history = noHistory ? null : new FileHistoryStore(FileHistoryStore.DefaultPath());
            return await IlReplApp.RunAsync(engine, history, cancellationToken).ConfigureAwait(false);
        }

        IEnumerable<string> lines;
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
        else
        {
            lines = ReadStandardInput();
        }

        var runner = new BatchRunner(engine, Console.Out, color, echo);
        return await runner.RunAsync(lines, cancellationToken).ConfigureAwait(false);
    }
});

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);

static IEnumerable<string> SplitEval(string text)
{
    var current = new System.Text.StringBuilder();
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

static IEnumerable<string> ReadStandardInput()
{
    string? line;
    while ((line = Console.In.ReadLine()) is not null)
    {
        yield return line;
    }
}
