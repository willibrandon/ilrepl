#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;

var ridOption = new Option<string>("--rid") { Description = "The runtime identifier to publish for.", Required = true };
var versionOption = new Option<string>("--package-version")
{
    Description = "The package version, with or without a leading v.",
    DefaultValueFactory = _ => "0.4.1",
};
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

    var nativeDiagnostics = Directory.EnumerateFiles(Path.Combine(publishDirectory, "host"), "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) is "TraceEventNative.dll" or "KernelTraceControl.dll"
            or "KernelTraceControl.Win61.dll" or "msdia140.dll").ToArray();
    if (nativeDiagnostics.Length != 0)
    {
        Console.Error.WriteLine("unused native diagnostics helpers were bundled: " + string.Join(", ", nativeDiagnostics));
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

        var diagnostic = await CaptureAsync(publishDirectory, executable,
            ["--no-color", "-e", "ldstr \"text\"; call int32 System.Math::Abs(int32)"], cancellationToken);
        if (diagnostic.ExitCode == 0 || !diagnostic.Output.Contains("Expected:", StringComparison.Ordinal)
            || !diagnostic.Output.Contains("actual string", StringComparison.Ordinal)
            || !diagnostic.Output.Contains("ldstr \"text\"", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("diagnostic smoke test failed:\n" + diagnostic.Output);
            return 1;
        }

        Console.WriteLine($"smoke test passed for {rid}");
        if (!await SmokeSessionsAsync(repo, publishDirectory, executable, cancellationToken))
        {
            return 1;
        }
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

static async Task<(int ExitCode, string Output)> CaptureAsync(string workingDirectory, string fileName, string[] arguments,
    CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null)
{
    var startInfo = new ProcessStartInfo(fileName)
    {
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
    };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    if (environment is not null)
    {
        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }
    }

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{fileName} did not start");
    process.StandardInput.Close();
    var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
    var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
    await process.WaitForExitAsync(cancellationToken);
    return (process.ExitCode, await stdout + await stderr);
}

static async Task<bool> SmokeSessionsAsync(string repo, string publishDirectory, string executable, CancellationToken cancellationToken)
{
    var directory = Directory.CreateTempSubdirectory("ilrepl-native-session-").FullName;
    try
    {
        var feed = Path.Combine(directory, "feed");
        Directory.CreateDirectory(feed);
        var packed = await RunAsync(repo, "dotnet",
            ["pack", Path.Combine(repo, "samples", "Greeter", "Greeter.csproj"), "-c", "Release", "-o", feed,
                "-p:IsPackable=true", "-p:PackageId=IlRepl.SessionSmoke", "-p:PackageVersion=1.0.0", "--nologo", "-v", "quiet"],
            cancellationToken);
        if (packed != 0)
        {
            return false;
        }

        new XDocument(new XElement("configuration",
            new XElement("packageSources", new XElement("clear"), new XElement("add", new XAttribute("key", "local"),
                new XAttribute("value", feed))),
            new XElement("packageSourceMapping", new XElement("clear")),
            new XElement("fallbackPackageFolders", new XElement("clear")),
            new XElement("config", new XElement("add", new XAttribute("key", "globalPackagesFolder"),
                new XAttribute("value", Path.Combine(directory, "packages")))))).Save(Path.Combine(directory, "NuGet.Config"));

        var installedRuntime = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        var installedRoot = Path.GetFullPath(Path.Combine(installedRuntime, "..", "..", ".."));
        var runtimeOnly = Path.Combine(directory, "runtime-only");
        Directory.CreateDirectory(runtimeOnly);
        var muxerName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var muxer = Path.Combine(runtimeOnly, muxerName);
        File.Copy(Path.Combine(installedRoot, muxerName), muxer);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(muxer, File.GetUnixFileMode(Path.Combine(installedRoot, muxerName)));
        }

        CopyDirectory(Path.Combine(installedRoot, "host"), Path.Combine(runtimeOnly, "host"));
        CopyDirectory(installedRuntime, Path.Combine(runtimeOnly, "shared", "Microsoft.NETCore.App",
            Path.GetFileName(installedRuntime)));
        var environment = new Dictionary<string, string>
        {
            ["DOTNET_HOST_PATH"] = muxer, ["DOTNET_ROOT"] = runtimeOnly, ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["PATH"] = runtimeOnly, ["NUGET_PACKAGES"] = Path.Combine(directory, "packages"),
        };
        var sdks = await CaptureAsync(directory, muxer, ["--list-sdks"], cancellationToken, environment);
        if (sdks.ExitCode != 0 || !string.IsNullOrWhiteSpace(sdks.Output))
        {
            Console.Error.WriteLine("runtime-only smoke fixture unexpectedly found an SDK: " + sdks.Output);
            return false;
        }

        var save = await CaptureAsync(directory, executable, ["--no-color", "-e",
            ".load nuget:IlRepl.SessionSmoke; ldc.i4 6; ldc.i4 7; call Greeter.Ops::Multiply(int32, int32); "
                + ".session save example.ilrepl.json --embed"], cancellationToken, environment);
        var open = await CaptureAsync(directory, executable,
            ["--no-color", "--batch", "example.ilrepl.json"], cancellationToken, environment);
        var run = await CaptureAsync(directory, executable,
            ["--no-color", "example.ilrepl.json", "--run"], cancellationToken, environment);
        if (save.ExitCode != 0 || !save.Output.Contains("= 42 : int32", StringComparison.Ordinal)
            || open.ExitCode != 0 || !open.Output.Contains("Session opened. Nothing has run yet.", StringComparison.Ordinal)
            || open.Output.Contains("= 42 : int32", StringComparison.Ordinal)
            || run.ExitCode != 0 || !run.Output.Contains("= 42 : int32", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("session/runtime-only package smoke failed:\n" + save.Output + open.Output + run.Output);
            return false;
        }

        if (Directory.EnumerateFiles(publishDirectory, "NuGet.*.dll", SearchOption.TopDirectoryOnly).Any())
        {
            Console.Error.WriteLine("NuGet restore libraries must remain inside the host directory");
            return false;
        }

        if (!await SmokeNativeAsync(directory, executable, environment, cancellationToken)) return false;

        Console.WriteLine("session save/open/run and runtime-only package smoke passed");
        return true;
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task<bool> SmokeNativeAsync(string directory, string executable, IReadOnlyDictionary<string, string> environment,
    CancellationToken cancellationToken)
{
    const string declaration = ".method int32 Increment(int32 value) {; ldarg.0; ldc.i4.1; add; ret; }; ";
    var listing = await CaptureAsync(directory, executable,
        ["--no-color", "-e", declaration + ".jit Increment --against Increment --assert"], cancellationToken, environment);
    var difference = await CaptureAsync(directory, executable,
        ["--no-color", "-e", declaration + ".method int32 Other(int32 value) {; ldarg.0; ldc.i4.2; add; ret; }; "
            + ".jit Increment --against Other --assert"], cancellationToken, environment);
    if (listing.ExitCode != 0 || !listing.Output.Contains("native inspection: equal", StringComparison.Ordinal)
        || !listing.Output.Contains("FullOpts", StringComparison.Ordinal)
        || !listing.Output.Contains("0 workload invocations", StringComparison.Ordinal)
        || difference.ExitCode == 0 || !difference.Output.Contains("native inspection: different", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("native inspection/runtime-only smoke failed:\n" + listing.Output + difference.Output);
        return false;
    }

    Console.WriteLine("native inspection, comparison, and assertion smoke passed without an SDK");
    return true;
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.EnumerateFiles(source))
    {
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }

    foreach (var directory in Directory.EnumerateDirectories(source))
    {
        CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
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
