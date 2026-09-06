#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;

var configurationOption = new Option<string>("--configuration", "-c") { Description = "The build configuration.", DefaultValueFactory = _ => "Release" };
var outputOption = new Option<string>("--output") { Description = "Where the browser assets go, relative to the repository.", DefaultValueFactory = _ => "docs/public/try" };
var root = new RootCommand("Publishes the browser build of ilrepl and copies it into the docs site.") { configurationOption, outputOption };

root.SetAction(async (parseResult, cancellationToken) =>
{
    var repo = FindRepoRoot();
    var configuration = parseResult.GetValue(configurationOption)!;
    var output = Path.GetFullPath(parseResult.GetValue(outputOption)!, repo);
    var project = Path.Combine(repo, "src", "IlRepl.Wasm", "IlRepl.Wasm.csproj");

    var publish = new ProcessStartInfo("dotnet") { WorkingDirectory = repo, UseShellExecute = false };
    foreach (var argument in new[] { "publish", project, "-c", configuration, "--nologo", "-v", "quiet" })
    {
        publish.ArgumentList.Add(argument);
    }

    using (var process = Process.Start(publish) ?? throw new InvalidOperationException("dotnet did not start"))
    {
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine("publish failed");
            return process.ExitCode;
        }
    }

    var wwwroot = Path.Combine(repo, "src", "IlRepl.Wasm", "bin", configuration, "net10.0", "publish", "wwwroot");
    if (Directory.Exists(output))
    {
        Directory.Delete(output, recursive: true);
    }

    var framework = Path.Combine(output, "_framework");
    Directory.CreateDirectory(framework);

    // GitHub Pages serves files as they are, so the pre-compressed variants only add weight.
    var copied = 0;
    foreach (var file in Directory.EnumerateFiles(Path.Combine(wwwroot, "_framework"), "*", SearchOption.AllDirectories))
    {
        if (file.EndsWith(".gz", StringComparison.Ordinal) || file.EndsWith(".br", StringComparison.Ordinal))
        {
            continue;
        }

        File.Copy(file, Path.Combine(framework, Path.GetFileName(file)), overwrite: true);
        copied++;
    }

    foreach (var name in PageFiles.Names)
    {
        File.Copy(Path.Combine(wwwroot, name), Path.Combine(output, name), overwrite: true);
    }

    Console.WriteLine($"copied {copied} framework files to {output}");
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
/// The page-side files that sit next to the framework directory.
/// </summary>
static class PageFiles
{
    /// <summary>
    /// The file names, copied as they are.
    /// </summary>
    public static readonly string[] Names = ["main.js", "worker.js", "interop.js"];
}
