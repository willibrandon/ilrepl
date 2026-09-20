#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:package System.CommandLine

using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

var ridOption = new Option<string>("--rid") { Description = "The runtime identifier to publish for.", Required = true };
var versionOption = new Option<string>("--package-version")
{
    Description = "The package version, with or without a leading v.",
    DefaultValueFactory = _ => "0.5.4",
};

var outputOption = new Option<string>("--output")
{
    Description = "The artifacts directory, relative to the repository.",
    DefaultValueFactory = _ => "artifacts/native-aot",
};

var buildOnlyOption = new Option<bool>("--build-only")
{
    Description = "Publish and inspect native host code without executing checks or producing a package.",
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
            $"-p:Version={version}", "-p:IlReplHostPublishReadyToRun=true", "--nologo", "-v", "quiet"], cancellationToken);
        if (publish != 0)
        {
            return publish;
        }
    }

    var publishedHost = ReadReadyToRunHost(publishDirectory, rid);
    if (publishedHost is null)
    {
        return 1;
    }

    var publishedTerminalHelper = ReadTerminalHelperHash(publishDirectory, rid);
    if (buildOnly)
    {
        return 0;
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

    if (!await SmokePublishedAsync(repo, publishDirectory, executable, cancellationToken))
    {
        return 1;
    }

    var publishedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable, cancellationToken)));
    var checkedPackages = new List<Dictionary<string, string>>();
    if (smokeOnly)
    {
        await WriteEvidenceAsync(artifacts, rid, publishedHash, publishedHost, publishedTerminalHelper,
            checkedPackages, cancellationToken);
        return 0;
    }

    var pack = await RunAsync(repo, "dotnet", ["pack", project, "-c", "Release", "-r", rid, "-o", packagesDirectory,
        $"-p:Version={version}", $"-p:PackageVersion={version}", "-p:IlReplHostPublishReadyToRun=true",
        "--nologo", "-v", "quiet"], cancellationToken);
    if (pack != 0)
    {
        return pack;
    }

    foreach (var package in Directory.GetFiles(packagesDirectory, "*.nupkg"))
    {
        if (Path.GetFileName(package) != $"ilrepl.{rid}.{version}.nupkg")
        {
            continue;
        }

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

            var packagedHost = ReadReadyToRunHost(packagedDirectory, rid);
            if (packagedHost is null)
            {
                return 1;
            }

            var packagedTerminalHelper = ReadTerminalHelperHash(packagedDirectory, rid);
            if (packagedTerminalHelper != publishedTerminalHelper)
            {
                Console.Error.WriteLine("the packaged terminal helper differs from the validated published helper");
                return 1;
            }

            if (!await SmokePublishedAsync(repo, packagedDirectory, packagedExecutable, cancellationToken))
            {
                return 1;
            }

            var packageEvidence = new Dictionary<string, string>(packagedHost)
            {
                ["package"] = Path.GetFileName(package),
                ["packageSha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package, cancellationToken))),
                ["executableSha256"] = Convert.ToHexString(
                    SHA256.HashData(await File.ReadAllBytesAsync(packagedExecutable, cancellationToken))),
            };

            if (packagedTerminalHelper is not null)
            {
                packageEvidence["terminalHelperSha256"] = packagedTerminalHelper;
            }

            checkedPackages.Add(packageEvidence);
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

    await WriteEvidenceAsync(artifacts, rid, publishedHash, publishedHost, publishedTerminalHelper,
        checkedPackages, cancellationToken);

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

static Dictionary<string, string>? ReadReadyToRunHost(string directory, string rid)
{
    var architecture = rid[(rid.LastIndexOf('-') + 1)..] switch
    {
        "x64" => (ushort)Machine.Amd64,
        "arm64" => (ushort)Machine.Arm64,
        _ => throw new ArgumentException($"unsupported ReadyToRun target: {rid}"),
    };

    // CoreCLR's IMAGE_FILE_MACHINE_NATIVE_OS_OVERRIDE is XORed with the target architecture.
    var os = rid.StartsWith("win-", StringComparison.Ordinal) ? 0
        : rid.StartsWith("osx-", StringComparison.Ordinal) ? 0x4644
        : rid.StartsWith("linux-", StringComparison.Ordinal) ? 0x7B79
        : throw new ArgumentException($"unsupported ReadyToRun target: {rid}");
    var expectedMachine = (ushort)(architecture ^ os);
    var evidence = new Dictionary<string, string> { ["readyToRunMachine"] = $"0x{expectedMachine:X4}" };
    try
    {
        var dependencies = File.ReadAllBytes(Path.Combine(directory, "host", "ilrepl-host.deps.json"));
        using var document = JsonDocument.Parse(dependencies);
        var runtimeTarget = document.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString();
        if (runtimeTarget is null || !runtimeTarget.EndsWith("/" + rid, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"host dependencies do not target {rid}: {runtimeTarget}");
            return null;
        }

        evidence["runtimeTarget"] = runtimeTarget;
        evidence["dependenciesSha256"] = Convert.ToHexString(SHA256.HashData(dependencies));
    }
    catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"cannot inspect host dependencies: {exception.Message}");
        return null;
    }

    foreach (var (file, key) in new[]
    {
        ("ilrepl-host.dll", "hostSha256"),
        ("IlRepl.Engine.dll", "engineSha256"),
        ("IlRepl.Protocol.dll", "protocolSha256"),
    })
    {
        var path = Path.Combine(directory, "host", file);
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var native = pe.PEHeaders.CorHeader?.ManagedNativeHeaderDirectory;
            if (native is not { Size: >= 4, RelativeVirtualAddress: > 0 }
                || (ushort)pe.PEHeaders.CoffHeader.Machine != expectedMachine
                || pe.GetSectionData(native.Value.RelativeVirtualAddress).GetReader().ReadUInt32() != 0x00525452)
            {
                Console.Error.WriteLine($"{path} does not contain ReadyToRun code for {rid}");
                return null;
            }

            stream.Position = 0;
            evidence[key] = Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"cannot inspect ReadyToRun host {path}: {exception.Message}");
            return null;
        }
    }

    Console.WriteLine($"ReadyToRun host images match {rid} ({evidence["readyToRunMachine"]})");
    return evidence;
}

static string? ReadTerminalHelperHash(string directory, string rid)
{
    if (!rid.StartsWith("linux-", StringComparison.Ordinal))
    {
        return null;
    }

    using var stream = File.OpenRead(Path.Combine(directory, "libhex1binterop.so"));
    return Convert.ToHexString(SHA256.HashData(stream));
}

static async Task WriteEvidenceAsync(
    string artifacts,
    string rid,
    string publishedHash,
    Dictionary<string, string> publishedHost,
    string? terminalHelperHash,
    List<Dictionary<string, string>> packages,
    CancellationToken cancellationToken)
{
    Directory.CreateDirectory(artifacts);
    await using var file = File.Create(Path.Combine(artifacts, "smoke-results.json"));
    await using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
    writer.WriteStartObject();
    writer.WriteNumber("schemaVersion", 2);
    writer.WriteString("targetRid", rid);
    writer.WriteString("actualRuntimeIdentifier", RuntimeInformation.RuntimeIdentifier);
    writer.WriteString("architecture", RuntimeInformation.OSArchitecture.ToString());
    writer.WriteString("runtime", Environment.Version.ToString());
    writer.WriteString("libc", OperatingSystem.IsLinux()
        ? RuntimeInformation.RuntimeIdentifier.StartsWith("linux-musl-", StringComparison.Ordinal) ? "musl" : "glibc" : null);
    writer.WriteString("commit", Environment.GetEnvironmentVariable("GITHUB_SHA"));
    writer.WriteString("publishedExecutableSha256", publishedHash);
    writer.WriteString("publishedTerminalHelperSha256", terminalHelperHash);
    writer.WriteBoolean("completed", true);
    writer.WriteStartObject("publishedHost");
    foreach (var (name, value) in publishedHost)
    {
        writer.WriteString(name, value);
    }

    writer.WriteEndObject();
    writer.WriteStartArray("packages");
    foreach (var package in packages)
    {
        writer.WriteStartObject();
        foreach (var (name, value) in package)
        {
            writer.WriteString(name, value);
        }

        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
    await writer.FlushAsync(cancellationToken);
}

static async Task<bool> SmokePublishedAsync(
    string repo,
    string publishDirectory,
    string executable,
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

static async Task WaitForProcessExitAsync(Process process, CancellationToken cancellationToken)
{
    await process.WaitForExitAsync(cancellationToken);
    // The Windows exit-code shortcut can precede the process object's resource-release signal.
    if (OperatingSystem.IsWindows())
    {
        while (!process.WaitForExit(0))
        {
            await Task.Delay(10, cancellationToken);
        }
    }
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
        await WaitForProcessExitAsync(process, cancellationToken);
        return process.ExitCode;
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await WaitForProcessExitAsync(process, CancellationToken.None);
    }
}

static async Task<(int ExitCode, string Output)> CaptureAsync(
    string workingDirectory,
    string fileName,
    string[] arguments,
    CancellationToken cancellationToken,
    IReadOnlyDictionary<string, string>? environment = null)
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
        await WaitForProcessExitAsync(process, timeout.Token);
        await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token);
        return (process.ExitCode, await stdout + await stderr);
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await WaitForProcessExitAsync(process, CancellationToken.None);
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
        var installedMuxer = Path.Combine(installedRoot, muxerName);
        File.Copy(installedMuxer, muxer);
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine($"runtime-only muxer attributes: installed {File.GetAttributes(installedMuxer)}, "
                + $"copied {File.GetAttributes(muxer)}");
        }

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

        if (!await SmokeNativeAsync(directory, executable, environment, cancellationToken))
        {
            return false;
        }

        Console.WriteLine("session save/open/run and runtime-only package smoke passed");
        return true;
    }
    finally
    {
        DeleteRuntimeDirectory(directory);
    }
}

static async Task<bool> SmokeNativeAsync(
    string directory,
    string executable,
    IReadOnlyDictionary<string, string> environment,
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

static void DeleteRuntimeDirectory(string directory)
{
    try
    {
        Directory.Delete(directory, recursive: true);
    }
    catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
    {
        try
        {
            Console.Error.WriteLine($"runtime-only fixture cleanup failed (0x{exception.HResult:X8}): {directory}");
            var muxer = Path.Combine(directory, "runtime-only", "dotnet.exe");
            if (File.Exists(muxer))
            {
                Console.Error.WriteLine($"remaining muxer attributes: {File.GetAttributes(muxer)}");
            }

            var prefix = directory + Path.DirectorySeparatorChar;
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        var image = process.MainModule?.FileName;
                        if (image?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
                        {
                            Console.Error.WriteLine($"runtime-only fixture process: {process.Id} {image}");
                        }
                    }
                    catch (Exception inspection) when (inspection is Win32Exception or InvalidOperationException)
                    {
                        // Processes may exit or deny inspection while diagnostics are collected.
                    }
                }
            }

            var handleTool = Environment.GetEnvironmentVariable("ILREPL_SMOKE_HANDLE_PATH");
            if (handleTool is not null && File.Exists(handleTool))
            {
                var start = new ProcessStartInfo(handleTool) { UseShellExecute = false };
                foreach (var argument in new[] { "-accepteula", "-nobanner", "-a", directory })
                {
                    start.ArgumentList.Add(argument);
                }

                using var handles = Process.Start(start);
                if (handles is not null && !handles.WaitForExit(15_000))
                {
                    handles.Kill(entireProcessTree: true);
                    handles.WaitForExit();
                }
            }
        }
        catch (Exception)
        {
            // Best-effort diagnostics must never replace the original cleanup failure.
        }

        throw;
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
