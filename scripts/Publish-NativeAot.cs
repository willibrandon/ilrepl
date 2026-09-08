#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;

var ridOption = new Option<string>("--rid") { Description = "The runtime identifier to publish for.", Required = true };
var versionOption = new Option<string>("--package-version") { Description = "The package version, with or without a leading v.", DefaultValueFactory = _ => "0.3.0" };
var outputOption = new Option<string>("--output") { Description = "The artifacts directory, relative to the repository.", DefaultValueFactory = _ => "artifacts/native-aot" };
var root = new RootCommand("Publishes the Native AOT front-end for one runtime identifier, smoke-tests it, and packs the runtime-specific tool package.")
{
    ridOption,
    versionOption,
    outputOption,
};

root.SetAction(async (parseResult, cancellationToken) =>
{
    var repo = FindRepoRoot();
    var rid = parseResult.GetValue(ridOption)!;
    var version = parseResult.GetValue(versionOption)!.TrimStart('v');
    var artifacts = Path.GetFullPath(Path.Combine(parseResult.GetValue(outputOption)!, rid), repo);
    var publishDirectory = Path.Combine(artifacts, "publish");
    var packagesDirectory = Path.Combine(artifacts, "packages");
    var project = Path.Combine(repo, "src", "IlRepl", "IlRepl.csproj");

    var publish = await RunAsync(repo, "dotnet", ["publish", project, "-c", "Release", "-r", rid, "-o", publishDirectory, $"-p:Version={version}", "--nologo", "-v", "quiet"], cancellationToken);
    if (publish != 0)
    {
        return publish;
    }

    var executable = Path.Combine(publishDirectory, OperatingSystem.IsWindows() ? "ilrepl.exe" : "ilrepl");
    if (!File.Exists(executable))
    {
        Console.Error.WriteLine($"expected {executable} after publish");
        return 1;
    }

    if (!File.Exists(Path.Combine(publishDirectory, "host", "ilrepl-host.dll")))
    {
        Console.Error.WriteLine("the host was not bundled beside the executable");
        return 1;
    }

    // The smoke test only runs when the build machine can execute the binary.
    if (CanRunHere(rid))
    {
        var smoke = await CaptureAsync(publishDirectory, executable, ["--no-color", "-e", "ldc.i4 6; ldc.i4 7; mul; ret"], cancellationToken);
        if (smoke.ExitCode != 0 || !smoke.Output.Contains("= 42 : int32", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("smoke test failed:\n" + smoke.Output);
            return 1;
        }

        Console.WriteLine($"smoke test passed for {rid}");
    }

    var pack = await RunAsync(repo, "dotnet", ["pack", project, "-c", "Release", "-r", rid, "-o", packagesDirectory, $"-p:Version={version}", $"-p:PackageVersion={version}", "--nologo", "-v", "quiet"], cancellationToken);
    if (pack != 0)
    {
        return pack;
    }

    foreach (var package in Directory.GetFiles(packagesDirectory, "*.nupkg"))
    {
        Console.WriteLine($"packed {Path.GetFileName(package)}");
    }

    return 0;
});

return await root.Parse(args).InvokeAsync();

static bool CanRunHere(string rid)
{
    var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
    var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
    return rid == $"{os}-{arch}";
}

static async Task<int> RunAsync(string workingDirectory, string fileName, string[] arguments, CancellationToken cancellationToken)
{
    var startInfo = new ProcessStartInfo(fileName) { WorkingDirectory = workingDirectory, UseShellExecute = false };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{fileName} did not start");
    await process.WaitForExitAsync(cancellationToken);
    return process.ExitCode;
}

static async Task<(int ExitCode, string Output)> CaptureAsync(string workingDirectory, string fileName, string[] arguments, CancellationToken cancellationToken)
{
    var startInfo = new ProcessStartInfo(fileName)
    {
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{fileName} did not start");
    var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
    var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
    await process.WaitForExitAsync(cancellationToken);
    return (process.ExitCode, await stdout + await stderr);
}

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
