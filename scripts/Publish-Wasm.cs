#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var configurationOption = new Option<string>("--configuration", "-c")
{
    Description = "The build configuration.",
    DefaultValueFactory = _ => "Release",
};

var outputOption = new Option<string>("--output")
{
    Description = "Where the browser assets go, relative to the repository.",
    DefaultValueFactory = _ => "docs/public/try",
};

var conformanceOption = new Option<bool>("--conformance")
{
    Description = "Includes the test-only browser conformance entry point.",
};

var root = new RootCommand("Publishes the browser build of ilrepl and copies it into the docs site.")
{
    configurationOption, outputOption, conformanceOption,
};

root.SetAction(async (parseResult, cancellationToken) =>
{
    var repo = FindRepoRoot();
    var configuration = parseResult.GetValue(configurationOption)!;
    var output = Path.GetFullPath(parseResult.GetValue(outputOption)!, repo);
    var project = Path.Join(repo, "src", "IlRepl.Wasm", "IlRepl.Wasm.csproj");

    var publish = new ProcessStartInfo("dotnet") { WorkingDirectory = repo, UseShellExecute = false };
    foreach (var argument in new[] { "publish", project, "-c", configuration, "--nologo", "-v", "quiet" })
    {
        publish.ArgumentList.Add(argument);
    }

    if (parseResult.GetValue(conformanceOption))
    {
        publish.ArgumentList.Add("-p:BrowserConformance=true");
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

    var wwwroot = Path.Join(repo, "src", "IlRepl.Wasm", "bin", configuration, "net10.0", "publish", "wwwroot");
    if (Directory.Exists(output))
    {
        Directory.Delete(output, recursive: true);
    }

    var bundle = Path.Join(output, "bundle");
    var framework = Path.Join(bundle, "_framework");
    Directory.CreateDirectory(framework);

    // GitHub Pages serves files as they are, so the pre-compressed variants only add weight.
    var copied = 0;
    foreach (var file in Directory.EnumerateFiles(Path.Join(wwwroot, "_framework"), "*", SearchOption.AllDirectories))
    {
        if (file.EndsWith(".gz", StringComparison.Ordinal) || file.EndsWith(".br", StringComparison.Ordinal))
        {
            continue;
        }

        File.Copy(file, Path.Join(framework, Path.GetFileName(file)), overwrite: true);
        copied++;
    }

    var pageFiles = parseResult.GetValue(conformanceOption)
        ? PageFiles.Names.Append("conformance-worker.js") : PageFiles.Names;
    foreach (var name in pageFiles)
    {
        File.Copy(Path.Join(wwwroot, name), Path.Join(bundle, name), overwrite: true);
    }

    // The Greeter sample sits beside the page; the worker fetches it into the runtime's file
    // system so .load has an assembly to read in the browser.
    var samples = Path.Join(bundle, "samples");
    Directory.CreateDirectory(samples);
    var sample = Path.Join(repo, "samples", "Greeter", "bin", configuration, "net10.0", "Greeter.dll");
    File.Copy(sample, Path.Join(samples, "Greeter.dll"), overwrite: true);

    // Version the complete import graph, including dotnet.js and its embedded assembly manifest.
    // A page refresh cannot reliably invalidate the HTTP cache used by module workers.
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var file in Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories)
        .OrderBy(file => Path.GetRelativePath(bundle, file).Replace('\\', '/'), StringComparer.Ordinal))
    {
        hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(bundle, file).Replace('\\', '/') + "\0"));
        hash.AppendData(SHA256.HashData(await File.ReadAllBytesAsync(file, cancellationToken)));
    }

    var directory = "assets/" + Convert.ToHexStringLower(hash.GetHashAndReset());
    Directory.CreateDirectory(Path.Join(output, "assets"));
    Directory.Move(bundle, Path.Join(output, directory));
    await using var manifest = File.Create(Path.Join(output, "asset-manifest.json"));
    await using var json = new Utf8JsonWriter(manifest);
    json.WriteStartObject();
    json.WriteString("directory", directory);
    json.WriteEndObject();
    await json.FlushAsync(cancellationToken);

    Console.WriteLine($"copied {copied} framework files to {output}");
    return 0;
});

return await root.Parse(args).InvokeAsync();

static string FindRepoRoot()
{
    var directory = Directory.GetCurrentDirectory();
    while (directory is not null)
    {
        if (File.Exists(Path.Join(directory, "IlRepl.slnx")))
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
    public static readonly string[] Names =
    [
        "main.js", "worker.js", "interop.js", "comparison-worker.js", "comparison-interop.js", "comparison-supervisor.js",
        "workspace-controls.js",
    ];
}
