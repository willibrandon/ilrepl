using System.Diagnostics;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;

namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// Runs the built front-end as a real process: batch mode over pipes and the terminal UI in a PTY.
/// </summary>
[TestClass]
public sealed class FrontEndProcessTests
{
    /// <summary>
    /// The test context, for cancellation.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// -e runs lines and prints the transcript.
    /// </summary>
    [TestMethod]
    public async Task Eval_PrintsResult()
    {
        var (code, stdout, stderr) = await RunAsync(["--no-color", "-e", "ldc.i4 6; ldc.i4 7; mul; ret"]);
        Assert.AreEqual(0, code, stderr);
        Assert.Contains("= 42 : int32", stdout);
        Assert.Contains("┊ [int32, int32] ◂ top", stdout);
    }

    /// <summary>
    /// -e can define a method and disassemble it in one run.
    /// </summary>
    [TestMethod]
    public async Task Eval_DisassemblesMethod()
    {
        var (code, stdout, stderr) = await RunAsync(["--no-color", "-e", ".method int32 Two() {; ldc.i4 2; ret; }; .dis Two"]);
        Assert.AreEqual(0, code, stderr);
        Assert.Contains(".method public hidebysig static int32 Two() cil managed {", stdout);
        Assert.Contains("ldc.i4 2", stdout);
        Assert.Contains("ret", stdout);
        Assert.Contains(".maxstack", stdout);
    }

    /// <summary>
    /// A script file runs with prompts echoed and a failing line sets the exit code.
    /// </summary>
    [TestMethod]
    public async Task Script_EchoesInputAndReportsFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), "ilrepl-tests", Guid.NewGuid().ToString("N") + ".il");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllLinesAsync(path, ["ldc.i4 1", "bogus", "ret"], TestContext.CancellationToken);
        try
        {
            var (code, stdout, _) = await RunAsync(["--no-color", path]);
            Assert.AreEqual(1, code);
            Assert.Contains("il[1]> ldc.i4 1", stdout);
            Assert.Contains("unknown opcode 'bogus'", stdout);
            Assert.Contains("= 1 : int32", stdout);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A script that declares a class runs it through the host and shows the instance by its fields.
    /// </summary>
    [TestMethod]
    public async Task Script_DefinesAndUsesClass()
    {
        var path = Path.Combine(Path.GetTempPath(), "ilrepl-tests", Guid.NewGuid().ToString("N") + ".il");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllLinesAsync(path,
        [
            ".class public sequential sealed Pair extends [System.Runtime]System.ValueType {",
            ".field public int32 A",
            ".field public int32 B",
            "}",
            ".locals init (valuetype Pair p)",
            "ldloca p",
            "ldc.i4 9",
            "stfld int32 Pair::A",
            "ldloc p",
            "box Pair",
            "ret",
        ], TestContext.CancellationToken);
        try
        {
            var (code, stdout, _) = await RunAsync(["--no-color", path]);
            Assert.AreEqual(0, code, stdout);
            Assert.Contains("struct Pair", stdout);
            Assert.Contains("end of struct Pair", stdout);
            Assert.Contains("= Pair { A = 9, B = 0 } : Pair", stdout);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Piped standard input runs in batch mode.
    /// </summary>
    [TestMethod]
    public async Task Stdin_RunsInBatchMode()
    {
        var (code, stdout, _) = await RunAsync(["--no-color"], "ldstr \"piped\"\nret\n");
        Assert.AreEqual(0, code);
        Assert.Contains("= \"piped\" : string", stdout);
    }

    /// <summary>
    /// The terminal UI starts in a PTY, answers input, and exits on Ctrl+Q.
    /// </summary>
    [TestMethod]
    public async Task Pty_RunsTerminalUi()
    {
        TestSkip.Unless(!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("ILREPL_PTY_TESTS") == "1", "PTY test runs on Unix by default");
        var ct = TestContext.CancellationToken;
        var recorder = new WorkloadRecorder();
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithPtyProcess(options =>
            {
                options.FileName = Hosting.HostLocator.FindDotnet();
                options.Arguments = [RepoPaths.FrontEndAssembly];
                options.WorkingDirectory = RepoPaths.Root;
                options.Environment = new Dictionary<string, string> { ["TERM"] = "xterm-256color", ["NO_COLOR"] = "" };
            })
            .AddWorkloadFilter(recorder)
            .WithHeadless()
            .WithDimensions(100, 30)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(30));
        await auto.WaitUntilTextAsync("il[1]>");

        // What the process writes is what a terminal would see: the caret at the prompt is
        // requested as a blinking block, never as the bar.
        await auto.WaitUntilAsync(_ => recorder.Output.Contains("\x1b[1 q", StringComparison.Ordinal));
        Assert.DoesNotContain("\x1b[6 q", recorder.Output, "the bar caret should not reach the terminal");
        await auto.TypeAsync("ldc.i4 41", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("[int32]");
        await auto.TypeAsync("ldc.i4 1", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.TypeAsync("add", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.TypeAsync("ret", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("= 42 : int32");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        var exitCode = await run;
        Assert.AreEqual(0, exitCode);
    }

    private async Task<(int Code, string StdOut, string StdErr)> RunAsync(string[] arguments, string? stdin = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Hosting.HostLocator.FindDotnet(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = RepoPaths.Root,
        };
        startInfo.ArgumentList.Add(RepoPaths.FrontEndAssembly);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet did not start");
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
        }

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken);
        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// -e can define a method and call it, with the block's lines separated by semicolons.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task Eval_DefinesAndCallsMethod()
    {
        var (code, stdout, stderr) = await RunAsync(["--no-color", "-e", ".method int32 Two() {; ldc.i4 2; ret; }; call int32 Two(); ret"]);
        Assert.AreEqual(0, code, stderr);
        Assert.Contains("method int32 Two()", stdout);
        Assert.Contains("end of method Two", stdout);
        Assert.Contains("= 2 : int32", stdout);
    }

    /// <summary>
    /// A script that ends inside a method block is an error rather than a silent ret.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task Script_EndingInsideMethod_Fails()
    {
        var path = Path.Combine(Path.GetTempPath(), "ilrepl-tests", Guid.NewGuid().ToString("N") + ".il");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllLinesAsync(path, [".method int32 Two() {", "ldc.i4 2"], TestContext.CancellationToken);
        try
        {
            var (code, stdout, _) = await RunAsync(["--no-color", path]);
            Assert.AreEqual(1, code);
            Assert.Contains("error: method Two is still open; close it with }", stdout);
            Assert.DoesNotContain("= 2", stdout);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
