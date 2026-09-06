#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:package Hex1b
#:package System.CommandLine

using System.CommandLine;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;

var outputOption = new Option<string>("--output") { Description = "Output path without extension. Writes <path>.cast and <path>.svg.", DefaultValueFactory = _ => "docs/public/demo" };
var frontEndOption = new Option<string?>("--front-end") { Description = "Path to ilrepl.dll. Defaults to the Debug build." };
var root = new RootCommand("Records a short ilrepl session through the Hex1b terminal emulator.") { outputOption, frontEndOption };

root.SetAction(async (parseResult, cancellationToken) =>
{
    var repo = FindRepoRoot();
    var output = Path.GetFullPath(parseResult.GetValue(outputOption)!, repo);
    var frontEnd = parseResult.GetValue(frontEndOption) ?? Path.Combine(repo, "src", "IlRepl", "bin", "Debug", "net10.0", "ilrepl.dll");
    if (!File.Exists(frontEnd))
    {
        Console.Error.WriteLine($"front-end not found at {frontEnd}; run dotnet build first");
        return 2;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    var cast = output + ".cast";
    var svg = output + ".svg";

    await using var terminal = Hex1bTerminal.CreateBuilder()
        .WithPtyProcess(options =>
        {
            options.FileName = "dotnet";
            options.Arguments = [frontEnd];
            options.WorkingDirectory = repo;
            options.Environment = new Dictionary<string, string> { ["TERM"] = "xterm-256color" };
        })
        .WithHeadless()
        .WithDimensions(96, 28)
        .WithAsciinemaRecording(cast)
        .Build();

    var run = terminal.RunAsync(cancellationToken);
    var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(30));
    var pace = TimeSpan.FromMilliseconds(450);

    await auto.WaitUntilTextAsync("il[1]>");
    await auto.WaitAsync(pace, ct: cancellationToken);
    foreach (var line in Demo.Arithmetic)
    {
        await auto.SlowTypeAsync(line, TimeSpan.FromMilliseconds(60), ct: cancellationToken);
        await auto.EnterAsync(ct: cancellationToken);
        await auto.WaitAsync(pace, ct: cancellationToken);
    }

    await auto.WaitUntilTextAsync("= 42 : int32");
    foreach (var line in Demo.Loop)
    {
        await auto.SlowTypeAsync(line, TimeSpan.FromMilliseconds(45), ct: cancellationToken);
        await auto.EnterAsync(ct: cancellationToken);
        await auto.WaitAsync(TimeSpan.FromMilliseconds(250), ct: cancellationToken);
    }

    await auto.WaitUntilTextAsync("= 10 : int32");
    await auto.SlowTypeAsync("conv.", TimeSpan.FromMilliseconds(80), ct: cancellationToken);
    await auto.WaitUntilTextAsync("opcodes");
    await auto.WaitAsync(TimeSpan.FromSeconds(1.5), ct: cancellationToken);

    using (var snapshot = auto.CreateSnapshot())
    {
        await File.WriteAllTextAsync(svg, snapshot.ToSvg(), cancellationToken);
    }

    await auto.EscapeAsync(ct: cancellationToken);
    await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: cancellationToken);
    await run;
    Console.WriteLine($"wrote {cast}");
    Console.WriteLine($"wrote {svg}");
    return 0;
});

return await root.Parse(args).InvokeAsync();

static string FindRepoRoot()
{
    var directory = Directory.GetCurrentDirectory();
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory, "IlRepl.slnx")))
        {
            return directory;
        }

        directory = Path.GetDirectoryName(directory);
    }

    throw new InvalidOperationException("run this from inside the repository");
}

/// <summary>
/// The lines the demo types.
/// </summary>
static class Demo
{
    /// <summary>
    /// A first cell that multiplies two constants.
    /// </summary>
    public static readonly string[] Arithmetic = ["ldc.i4 6", "ldc.i4 7", "mul", "ret"];

    /// <summary>
    /// A counting loop with a local and a backward branch.
    /// </summary>
    public static readonly string[] Loop =
    [
        ".locals init (int32 i)", "ldc.i4.0", "stloc i", "LOOP: ldloc i", "ldc.i4.1", "add", "dup", "stloc i", "ldc.i4 10", "blt LOOP", "ldloc i", "ret",
    ];
}
