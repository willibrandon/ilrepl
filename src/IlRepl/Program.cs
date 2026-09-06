using System.CommandLine;
using IlRepl.Batch;
using IlRepl.Hosting;
using IlRepl.Tui;

var evalOption = new Option<string[]>("--eval", "-e")
{
    Description = "Run IL lines separated by ';' and exit. ret runs the cell.",
};
var noColorOption = new Option<bool>("--no-color") { Description = "Plain output without ANSI colors." };
var quietOption = new Option<bool>("--quiet", "-q") { Description = "Do not echo the stack after each instruction." };
var batchOption = new Option<bool>("--batch") { Description = "Read lines from standard input without the terminal UI." };
var scriptArgument = new Argument<FileInfo?>("script")
{
    Description = "An IL script to run, one line per instruction.",
    Arity = ArgumentArity.ZeroOrOne,
};

var root = new RootCommand("Interactive CIL REPL with a live evaluation stack. Type IL, watch the stack, run it.")
{
    evalOption,
    noColorOption,
    quietOption,
    batchOption,
    scriptArgument,
};

root.SetAction(async (parseResult, cancellationToken) =>
{
    var eval = parseResult.GetValue(evalOption) ?? [];
    var script = parseResult.GetValue(scriptArgument);
    var quiet = parseResult.GetValue(quietOption);
    var batch = parseResult.GetValue(batchOption) || Console.IsInputRedirected || eval.Length > 0 || script is not null;
    var color = !parseResult.GetValue(noColorOption)
        && !Console.IsOutputRedirected
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    if (script is not null && !script.Exists)
    {
        Console.Error.WriteLine($"no such file: {script.FullName}");
        return 2;
    }

    HostProcessEngine engine;
    try
    {
        engine = await HostProcessEngine.StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    catch (HostProtocolException ex)
    {
        Console.Error.WriteLine("ilrepl: " + ex.Message);
        return 3;
    }

    await using (engine.ConfigureAwait(false))
    {
        string[] prelude = quiet ? QuietPrelude : [];
        if (!batch)
        {
            foreach (var line in prelude)
            {
                await engine.HandleAsync(line, cancellationToken).ConfigureAwait(false);
            }

            return await IlReplApp.RunAsync(engine, cancellationToken).ConfigureAwait(false);
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
        return await runner.RunAsync(prelude.Concat(lines), cancellationToken).ConfigureAwait(false);
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

static partial class Program
{
    private static readonly string[] QuietPrelude = [".quiet on"];
}
