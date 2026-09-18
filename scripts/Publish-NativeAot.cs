#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

var ridOption = new Option<string>("--rid") { Description = "The runtime identifier to publish for.", Required = true };
var versionOption = new Option<string>("--package-version")
{
    Description = "The package version, with or without a leading v.",
    DefaultValueFactory = _ => "0.4.1",
};
var outputOption = new Option<string>("--output")
{
    Description = "The artifacts directory, relative to the repository.",
    DefaultValueFactory = _ => "artifacts/native-aot",
};
var buildOnlyOption = new Option<bool>("--build-only")
{
    Description = "Publish without executing checks or producing a distributable package.",
};
var smokeOnlyOption = new Option<bool>("--smoke-only")
{
    Description = "Validate the existing published files without rebuilding or packing them.",
};
var root = new RootCommand("Publishes and validates the Native AOT frontend and its runtime-specific tool package.")
{
    ridOption,
    versionOption,
    outputOption,
    buildOnlyOption,
    smokeOnlyOption,
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
    var buildOnly = parseResult.GetValue(buildOnlyOption);
    var smokeOnly = parseResult.GetValue(smokeOnlyOption);
    if (buildOnly && smokeOnly)
    {
        Console.Error.WriteLine("--build-only and --smoke-only cannot be combined");
        return 1;
    }
    if (!buildOnly && !CanRunHere(rid))
    {
        Console.Error.WriteLine($"{rid} must be validated on a matching OS, architecture, and libc; current runtime is "
            + RuntimeInformation.RuntimeIdentifier + ". Use --build-only for publishing without validation.");
        return 1;
    }

    if (!smokeOnly)
    {
        var publish = await RunAsync(repo, "dotnet", ["publish", project, "-c", "Release", "-r", rid, "-o", publishDirectory,
            $"-p:Version={version}", "--nologo", "-v", "quiet"], cancellationToken);
        if (publish != 0) return publish;
    }
    if (buildOnly) return 0;

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

    if (!await SmokePublishedAsync(repo, publishDirectory, executable, cancellationToken)) return 1;
    var publishedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable, cancellationToken)));
    var checkedPackages = new List<Dictionary<string, string>>();
    if (smokeOnly)
    {
        await WriteEvidenceAsync(artifacts, rid, publishedHash, checkedPackages, cancellationToken);
        return 0;
    }

    var pack = await RunAsync(repo, "dotnet", ["pack", project, "-c", "Release", "-r", rid, "-o", packagesDirectory,
        $"-p:Version={version}", $"-p:PackageVersion={version}", "--nologo", "-v", "quiet"], cancellationToken);
    if (pack != 0)
    {
        return pack;
    }

    foreach (var package in Directory.GetFiles(packagesDirectory, "*.nupkg"))
    {
        if (Path.GetFileName(package) != $"ilrepl.{rid}.{version}.nupkg") continue;
        var unpacked = Directory.CreateTempSubdirectory("ilrepl-packaged-").FullName;
        try
        {
            ZipFile.ExtractToDirectory(package, unpacked);
            var settings = Directory.GetFiles(unpacked, "DotnetToolSettings.xml", SearchOption.AllDirectories).Single();
            var packagedDirectory = Path.GetDirectoryName(settings)!;
            var entry = XDocument.Load(settings).Descendants("Command").Single().Attribute("EntryPoint")!.Value;
            var packagedExecutable = Path.Combine(packagedDirectory, entry);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(packagedExecutable, File.GetUnixFileMode(packagedExecutable)
                    | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            if (!await SmokePublishedAsync(repo, packagedDirectory, packagedExecutable, cancellationToken)) return 1;
            checkedPackages.Add(new Dictionary<string, string>
            {
                ["package"] = Path.GetFileName(package),
                ["packageSha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package, cancellationToken))),
                ["executableSha256"] = Convert.ToHexString(
                    SHA256.HashData(await File.ReadAllBytesAsync(packagedExecutable, cancellationToken))),
            });
            Console.WriteLine($"packed and validated {Path.GetFileName(package)}");
        }
        finally
        {
            Directory.Delete(unpacked, recursive: true);
        }
    }
    if (checkedPackages.Count == 0)
    {
        Console.Error.WriteLine("the expected runtime-specific package was not produced");
        return 1;
    }
    await WriteEvidenceAsync(artifacts, rid, publishedHash, checkedPackages, cancellationToken);

    return 0;
});

return await root.Parse(args).InvokeAsync();

static bool CanRunHere(string rid)
{
    var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
    var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx"
        : RuntimeInformation.RuntimeIdentifier.StartsWith("linux-musl-", StringComparison.Ordinal) ? "linux-musl" : "linux";
    return rid == $"{os}-{arch}";
}

static async Task WriteEvidenceAsync(string artifacts, string rid, string publishedHash,
    List<Dictionary<string, string>> packages, CancellationToken cancellationToken)
{
    Directory.CreateDirectory(artifacts);
    await using var file = File.Create(Path.Combine(artifacts, "smoke-results.json"));
    await using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
    writer.WriteStartObject();
    writer.WriteNumber("schemaVersion", 1);
    writer.WriteString("targetRid", rid);
    writer.WriteString("actualRuntimeIdentifier", RuntimeInformation.RuntimeIdentifier);
    writer.WriteString("architecture", RuntimeInformation.OSArchitecture.ToString());
    writer.WriteString("runtime", Environment.Version.ToString());
    writer.WriteString("libc", OperatingSystem.IsLinux()
        ? RuntimeInformation.RuntimeIdentifier.StartsWith("linux-musl-", StringComparison.Ordinal) ? "musl" : "glibc" : null);
    writer.WriteString("commit", Environment.GetEnvironmentVariable("GITHUB_SHA"));
    writer.WriteString("publishedExecutableSha256", publishedHash);
    writer.WriteBoolean("completed", true);
    writer.WriteStartArray("packages");
    foreach (var package in packages)
    {
        writer.WriteStartObject();
        foreach (var (name, value) in package) writer.WriteString(name, value);
        writer.WriteEndObject();
    }
    writer.WriteEndArray();
    writer.WriteEndObject();
    await writer.FlushAsync(cancellationToken);
}

static async Task<bool> SmokePublishedAsync(string repo, string publishDirectory, string executable,
    CancellationToken cancellationToken)
{
    var smoke = await CaptureAsync(publishDirectory, executable, ["--no-color", "-e", "ldc.i4 6; ldc.i4 7; mul; ret"], cancellationToken);
    if (smoke.ExitCode != 0 || !smoke.Output.Contains("= 42 : int32", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("smoke test failed:\n" + smoke.Output);
        return false;
    }

    var diagnostic = await CaptureAsync(publishDirectory, executable,
        ["--no-color", "-e", "ldstr \"text\"; call int32 System.Math::Abs(int32)"], cancellationToken);
    if (diagnostic.ExitCode == 0 || !diagnostic.Output.Contains("Expected:", StringComparison.Ordinal)
        || !diagnostic.Output.Contains("actual string", StringComparison.Ordinal)
        || !diagnostic.Output.Contains("ldstr \"text\"", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("diagnostic smoke test failed:\n" + diagnostic.Output);
        return false;
    }

    Console.WriteLine("arithmetic and diagnostic smoke passed");
    if (!await SmokeSessionsAsync(repo, publishDirectory, executable, cancellationToken))
    {
        return false;
    }
    var packaged = await RunAsync(repo, "dotnet",
        ["run", "--project", Path.Combine(repo, "tests", "IlRepl.Tests", "IlRepl.Tests.csproj"), "-c", "Release", "--",
            "--packaged-smoke", executable], cancellationToken);
    return packaged == 0;
}

static async Task<int> RunAsync(string workingDirectory, string fileName, string[] arguments, CancellationToken cancellationToken)
{
    var startInfo = new ProcessStartInfo(fileName) { WorkingDirectory = workingDirectory, UseShellExecute = false };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{fileName} did not start");
    try
    {
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
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

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(60));
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{fileName} did not start");
    process.StandardInput.Close();
    var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
    var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
    try
    {
        await process.WaitForExitAsync(timeout.Token);
        await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token);
        return (process.ExitCode, await stdout + await stderr);
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
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
            ".load nuget:IlRepl.SessionSmoke; ldstr \"executions\"; ldstr \"run\"; "
                + "call void System.IO.File::AppendAllText(string, string); "
                + "ldc.i4 6; ldc.i4 7; call Greeter.Ops::Multiply(int32, int32); "
                + ".run; .session save example.ilrepl.json --embed"], cancellationToken, environment);
        var marker = Path.Combine(directory, "executions");
        var savedExecutions = File.Exists(marker) ? await File.ReadAllTextAsync(marker, cancellationToken) : "";
        var open = await CaptureAsync(directory, executable,
            ["--no-color", "--batch", "example.ilrepl.json"], cancellationToken, environment);
        var openedExecutions = File.Exists(marker) ? await File.ReadAllTextAsync(marker, cancellationToken) : "";
        var run = await CaptureAsync(directory, executable,
            ["--no-color", "example.ilrepl.json", "--run"], cancellationToken, environment);
        var replayedExecutions = File.Exists(marker) ? await File.ReadAllTextAsync(marker, cancellationToken) : "";
        if (save.ExitCode != 0 || !save.Output.Contains("= 42 : int32", StringComparison.Ordinal)
            || open.ExitCode != 0 || !open.Output.Contains("Session opened. Nothing has run yet.", StringComparison.Ordinal)
            || !open.Output.Contains("saved session history (no code executed)", StringComparison.Ordinal)
            || savedExecutions != "run" || openedExecutions != "run" || replayedExecutions != "runrun"
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
