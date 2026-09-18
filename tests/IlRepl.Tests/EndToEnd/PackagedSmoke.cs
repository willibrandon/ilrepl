using System.Diagnostics;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Engine;
using IlRepl.Processes;
using IlRepl.Protocol;

namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// Exercises interruption, replacement, and offline saving against the exact packaged frontend in real terminals.
/// </summary>
internal static class PackagedSmoke
{
    /// <summary>
    /// Runs explicit packaged validation without adding browser or packaging work to ordinary test discovery.
    /// </summary>
    /// <param name="args">The private probe switch and native frontend path.</param>
    /// <returns>Whether packaged validation was requested.</returns>
    internal static async Task<bool> TryRunAsync(string[] args)
    {
        if (args is not ["--packaged-smoke", var executable]) return false;
        executable = Path.GetFullPath(executable);
        var directory = Directory.CreateTempSubdirectory("ilrepl-packaged-smoke-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await InterruptAndRestartAsync(executable, directory, timeout.Token);
            if (!OperatingSystem.IsWindows()) await SupervisorAdoptionAsync(executable, directory, timeout.Token);
            await OfflineSaveAsync(executable, directory, timeout.Token);
            Console.WriteLine("packaged interruption, restart, retained source, supervision, and offline save passed");
            return true;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Verifies real terminal recovery, immediate editing, callable retained definitions, restart, and interrupted history saving.
    /// </summary>
    /// <param name="executable">A published frontend executable or the ordinary managed frontend assembly.</param>
    /// <param name="directory">An isolated directory for invocation markers and saved source.</param>
    /// <param name="cancellationToken">Bounds the complete terminal interaction.</param>
    /// <returns>Completion after the frontend exits successfully.</returns>
    internal static async Task InterruptAndRestartAsync(string executable, string directory, CancellationToken cancellationToken)
    {
        await using var terminal = CreateTerminal(executable, directory, out var diagnosticsPath);
        var run = terminal.RunAsync(cancellationToken);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(30));
        await WaitForStartupAsync(auto, run, "il[1]>", diagnosticsPath, cancellationToken);
        await auto.WaitUntilNoTextAsync("starting execution host");
        await SubmitAsync(auto, ".method int32 Answer() {", cancellationToken);
        await SubmitAsync(auto, "ldc.i4 42", cancellationToken);
        await SubmitAsync(auto, "ret", cancellationToken);
        await SubmitAsync(auto, "}", cancellationToken);
        await auto.WaitUntilTextAsync("end of method Answer");
        var running = Path.Combine(directory, "running");
        await SubmitAsync(auto, ".method void Spin() {", cancellationToken);
        await SubmitAsync(auto, "ldstr " + LiteralParser.Escape(running), cancellationToken);
        await SubmitAsync(auto, "ldstr \"started\"", cancellationToken);
        await SubmitAsync(auto, "call void System.IO.File::WriteAllText(string, string)", cancellationToken);
        await SubmitAsync(auto, "AGAIN: br AGAIN", cancellationToken);
        await SubmitAsync(auto, "}", cancellationToken);
        await auto.WaitUntilTextAsync("end of method Spin");
        await SubmitAsync(auto, "call void Spin()", cancellationToken);
        await SubmitAsync(auto, "ret", cancellationToken);
        await auto.WaitUntilAsync(_ => File.Exists(running));
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: cancellationToken);
        await auto.WaitUntilTextAsync("Press Ctrl+C again to restart");
        Assert.IsFalse(run.IsCompleted, "The first interrupt must preserve the running frontend.");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: cancellationToken);
        await auto.WaitUntilTextAsync("runtime restarted");
        await auto.Ctrl().KeyAsync(Hex1bKey.A, ct: cancellationToken);
        await auto.BackspaceAsync(ct: cancellationToken);
        await auto.TypeAsync(".", ct: cancellationToken);
        await auto.WaitUntilTextAsync("il[4]> .");
        await auto.TypeAsync("clear", ct: cancellationToken);
        await auto.WaitUntilTextAsync("il[4]> .clear");
        await auto.EnterAsync(ct: cancellationToken);
        await auto.WaitUntilTextAsync("cell cleared (declarations kept)");
        await SubmitAsync(auto, "call int32 Answer()", cancellationToken);
        await SubmitAsync(auto, "ret", cancellationToken);
        await auto.WaitUntilTextAsync("= 42 : int32");
        await SubmitAsync(auto, ".session restart", cancellationToken);
        await auto.WaitUntilAsync(snapshot => snapshot.FindText("= 42 : int32").Any(result =>
            snapshot.FindText("runtime restarted").Any(notice => notice.Line > result.Line)),
            description: "a new restart has completed after the retained definition ran");
        await SubmitAsync(auto, "call int32 Answer()", cancellationToken);
        await SubmitAsync(auto, "ldc.i4.1", cancellationToken);
        await SubmitAsync(auto, "add", cancellationToken);
        await SubmitAsync(auto, "ret", cancellationToken);
        await auto.WaitUntilTextAsync("= 43 : int32");
        await auto.TypeAsync("// unsent after completion", ct: cancellationToken);
        await auto.WaitUntilTextAsync("// unsent after completion");
        await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: cancellationToken);
        await auto.WaitUntilNoTextAsync("// unsent after completion");
        Assert.IsFalse(run.IsCompleted, "After completed execution, Ctrl+C must clear the draft and retain the frontend.");
        var saved = Path.Combine(directory, "retained.ilrepl.json");
        await SubmitAsync(auto, ".session save " + LiteralParser.Escape(saved) + " --embed", cancellationToken);
        await auto.WaitUntilAsync(_ => File.Exists(saved));
        await auto.WaitUntilTextAsync("saved session");
        var document = SessionCodec.Read(await File.ReadAllBytesAsync(saved, cancellationToken));
        Assert.Contains(cell => cell.State == "interrupted", document.Cells);
        Assert.Contains(cell => cell.Source.Any(line => line.Contains("Answer", StringComparison.Ordinal)), document.Cells);
        Assert.IsNotEmpty(document.Interruptions);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: cancellationToken);
        Assert.AreEqual(0, await run);
    }

    private static async Task OfflineSaveAsync(string executable, string directory, CancellationToken cancellationToken)
    {
        await using var terminal = CreateTerminal(executable, directory, out var diagnosticsPath,
            new Dictionary<string, string> { ["ILREPL_HOST_PATH"] = Path.Combine(directory, "missing-host.dll") });
        var run = terminal.RunAsync(cancellationToken);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(30));
        await WaitForStartupAsync(auto, run, "host unavailable", diagnosticsPath, cancellationToken);
        var saved = Path.Combine(directory, "offline.ilrepl.json");
        await SubmitAsync(auto, ".session save " + LiteralParser.Escape(saved) + " --embed", cancellationToken);
        await auto.WaitUntilAsync(_ => File.Exists(saved));
        await auto.WaitUntilTextAsync("saved session");
        var document = SessionCodec.Read(await File.ReadAllBytesAsync(saved, cancellationToken));
        Assert.IsEmpty(document.Cells);
        await SubmitAsync(auto, ".quit", cancellationToken);
        Assert.AreEqual(0, await run);
    }

    private static async Task SupervisorAdoptionAsync(string executable, string directory, CancellationToken cancellationToken)
    {
        await using var terminal = CreateTerminal(executable, directory, out var diagnosticsPath);
        var run = terminal.RunAsync(cancellationToken);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(30));
        await WaitForStartupAsync(auto, run, "il[1]>", diagnosticsPath, cancellationToken);
        await auto.WaitUntilNoTextAsync("starting execution host");
        var identity = Path.Combine(directory, "runtime-before-adoption");
        var release = Path.Combine(directory, "release-adoption");
        string[] source =
        [
            ".class public Keeper {", ".field public static int32 Value", "}",
            "ldc.i4 73", "stsfld int32 Keeper::Value", "ldstr " + LiteralParser.Escape(identity),
            "call int32 Environment::get_ProcessId()", "box int32", "callvirt instance string Object::ToString()",
            "call void File::WriteAllText(string, string)", "WAIT: ldstr " + LiteralParser.Escape(release),
            "call bool File::Exists(string)", "brfalse WAIT", "ldsfld int32 Keeper::Value", "ret",
        ];
        foreach (var line in source) await SubmitAsync(auto, line, cancellationToken);
        await auto.WaitUntilAsync(_ => File.Exists(identity) && new FileInfo(identity).Length > 0);
        var hostId = int.Parse(await File.ReadAllTextAsync(identity, cancellationToken));
        using var host = Process.GetProcessById(hostId);
        using var supervisor = Process.GetProcessById(await ParentProcessIdAsync(hostId, cancellationToken));
        supervisor.Kill();
        await supervisor.WaitForExitAsync(cancellationToken);
        Assert.IsFalse(host.HasExited, "Losing the packaged supervisor must preserve the running host.");
        await File.WriteAllTextAsync(release, "release", cancellationToken);
        await auto.WaitUntilTextAsync("= 73 : int32");
        var adoptedIdentity = Path.Combine(directory, "runtime-after-adoption");
        string[] continued =
        [
            "ldstr " + LiteralParser.Escape(adoptedIdentity), "call int32 Environment::get_ProcessId()", "box int32",
            "callvirt instance string Object::ToString()", "call void File::WriteAllText(string, string)",
            "ldsfld int32 Keeper::Value", "ldc.i4.1", "add", "ret",
        ];
        foreach (var line in continued) await SubmitAsync(auto, line, cancellationToken);
        await auto.WaitUntilTextAsync("= 74 : int32");
        Assert.AreEqual(hostId, int.Parse(await File.ReadAllTextAsync(adoptedIdentity, cancellationToken)));
        Assert.IsFalse(host.HasExited);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: cancellationToken);
        Assert.AreEqual(0, await run);
    }

    private static async Task<int> ParentProcessIdAsync(int processId, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            var status = await File.ReadAllTextAsync("/proc/" + processId + "/stat", cancellationToken);
            return int.Parse(status[(status.LastIndexOf(')') + 2)..].Split(' ')[1]);
        }
        var start = new ProcessStartInfo("/bin/ps");
        foreach (var argument in new[] { "-o", "ppid=", "-p", processId.ToString() }) start.ArgumentList.Add(argument);
        var result = await ToolProcess.RunAsync(start, cancellationToken);
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        return int.Parse(result.StandardOutput.Trim());
    }

    private static Hex1bTerminal CreateTerminal(string executable, string directory, out string diagnosticsPath,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var stderr = Path.Combine(directory, "frontend-" + Guid.NewGuid().ToString("N") + ".stderr");
        diagnosticsPath = stderr;
        return Hex1bTerminal.CreateBuilder()
            .WithPtyProcess(options =>
            {
                var managed = Path.GetExtension(executable).Equals(".dll", StringComparison.OrdinalIgnoreCase);
                options.FileName = managed ? HostLocator.FindDotnet() : executable;
                options.Arguments = managed ? [executable, "--no-history"] : ["--no-history"];
                options.WorkingDirectory = directory;
                options.Environment = new Dictionary<string, string> { ["TERM"] = "xterm-256color", ["NO_COLOR"] = "" };
                if (environment is not null)
                {
                    foreach (var (name, value) in environment) options.Environment[name] = value;
                }
                if (!OperatingSystem.IsWindows())
                {
                    // Retain startup exceptions independently of the PTY pump, which can stop before final output is applied.
                    const string launch = "ilrepl_smoke_stderr=$1; shift; exec \"$@\" 2> \"$ilrepl_smoke_stderr\"";
                    options.Arguments = [.. options.Environment.Select(pair => pair.Key + "=" + pair.Value),
                        "/bin/sh", "-c", launch, "ilrepl-smoke", stderr, options.FileName, .. options.Arguments];
                    options.FileName = "/usr/bin/env";
                }
            })
            .WithHeadless()
            .WithDimensions(120, 35)
            .Build();
    }

    private static async Task WaitForStartupAsync(Hex1bTerminalAutomator auto, Task<int> run, string expected,
        string diagnosticsPath, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await auto.WaitUntilTextAsync(expected);
            return;
        }
        try
        {
            await auto.WaitUntilAsync(snapshot => run.IsCompleted || snapshot.ContainsText(expected),
                description: "the frontend displays " + expected + " or exits");
            if (run.IsCompleted)
                throw new InvalidOperationException($"The frontend exited with code {await run} before displaying {expected}.");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            var error = "No stderr file was created.";
            try
            {
                if (File.Exists(diagnosticsPath)) error = await File.ReadAllTextAsync(diagnosticsPath, cancellationToken);
            }
            catch (Exception diagnosticError) when (diagnosticError is IOException or UnauthorizedAccessException)
            {
                error = "Could not read startup stderr: " + diagnosticError.Message;
            }
            throw new InvalidOperationException("Packaged frontend startup failed. Standard error:\n" + error, exception);
        }
    }

    private static async Task SubmitAsync(Hex1bTerminalAutomator auto, string line, CancellationToken cancellationToken)
    {
        await auto.TypeAsync(line, ct: cancellationToken);
        await auto.EnterAsync(ct: cancellationToken);
    }
}
