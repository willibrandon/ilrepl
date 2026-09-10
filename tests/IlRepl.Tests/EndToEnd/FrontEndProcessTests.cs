using System.Diagnostics;
using System.Text;
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
    /// A block comment that spans lines inside a method reaches the host as one comment, and the
    /// text after its closing delimiter is assembled.
    /// </summary>
    [TestMethod]
    public async Task Script_MultiLineCommentInsideMethod()
    {
        var path = Path.Combine(Path.GetTempPath(), "ilrepl-tests", Guid.NewGuid().ToString("N") + ".il");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllLinesAsync(path, [".method int32 F() {", "/* open", ".reset", "still */ ldc.i4.1", "ret", "}", "call int32 F()", "ret"], TestContext.CancellationToken);
        try
        {
            var (code, stdout, stderr) = await RunAsync(["--no-color", path]);
            Assert.AreEqual(0, code, stderr + stdout);
            Assert.Contains("end of method F", stdout);
            Assert.Contains("= 1 : int32", stdout);
            Assert.DoesNotContain("unknown opcode", stdout);
            Assert.DoesNotContain("cleared", stdout);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A line that is only a comment never runs the cell, so a value stays on the stack until a
    /// blank line or ret.
    /// </summary>
    [TestMethod]
    public async Task Script_CommentOnlyLines_NeverRun()
    {
        var path = Path.Combine(Path.GetTempPath(), "ilrepl-tests", Guid.NewGuid().ToString("N") + ".il");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllLinesAsync(path, ["ldc.i4 1", "// one", "/* two", "", "*/", "ldc.i4 2", "add", "", "ldc.i4 3", "// three", "ret"], TestContext.CancellationToken);
        try
        {
            var (code, stdout, stderr) = await RunAsync(["--no-color", path]);
            Assert.AreEqual(0, code, stderr + stdout);
            Assert.Contains("= 3 : int32", stdout);
            Assert.DoesNotContain("= 1 : int32", stdout);
            Assert.AreEqual(2, stdout.Split("= 3 : int32").Length - 1, stdout);
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
                options.Arguments = [RepoPaths.FrontEndAssembly, "--no-history"];
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

        // What the process writes is what a terminal would see: the hardware caret is hidden and
        // no caret shape is ever asked for; the prompt paints its own caret cell.
        await auto.WaitUntilAsync(_ => recorder.Output.Contains("\x1b[?25l", StringComparison.Ordinal));
        Assert.DoesNotContain("\x1b[1 q", recorder.Output, "no caret shape should reach the terminal");
        Assert.DoesNotContain("\x1b[6 q", recorder.Output, "no caret shape should reach the terminal");
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

    /// <summary>
    /// With --no-history no history file is written.
    /// </summary>
    [TestMethod]
    public async Task Pty_NoHistory_WritesNoFile()
    {
        TestSkip.Unless(!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("ILREPL_PTY_TESTS") == "1", "PTY test runs on Unix by default");
        var ct = TestContext.CancellationToken;
        var config = Directory.CreateTempSubdirectory("ilrepl-nohistory-").FullName;
        try
        {
            await using var terminal = Hex1bTerminal.CreateBuilder()
                .WithPtyProcess(options =>
                {
                    // The child is started through env so the config directory reaches it.
                    options.FileName = "/usr/bin/env";
                    options.Arguments = ["XDG_CONFIG_HOME=" + config, "NO_COLOR=", Hosting.HostLocator.FindDotnet(), RepoPaths.FrontEndAssembly, "--no-history"];
                    options.WorkingDirectory = RepoPaths.Root;
                })
                .WithHeadless()
                .WithDimensions(100, 30)
                .Build();

            var run = terminal.RunAsync(ct);
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(30));
            await auto.WaitUntilTextAsync("il[1]>");
            await auto.TypeAsync("nop", ct: ct);
            await auto.EnterAsync(ct: ct);
            await auto.WaitUntilTextAsync("1 instruction");
            await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
            Assert.AreEqual(0, await run);
            Assert.IsFalse(File.Exists(Path.Combine(config, "ilrepl", "history")), "no history file");
            Assert.IsFalse(Directory.Exists(Path.Combine(config, "ilrepl")), "not even the directory");
        }
        finally
        {
            Directory.Delete(config, recursive: true);
        }
    }

    /// <summary>
    /// A block typed in one run is one history entry in the file and comes back whole in the next run.
    /// </summary>
    [TestMethod]
    public async Task Pty_History_PersistsBlockAcrossRuns()
    {
        TestSkip.Unless(!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("ILREPL_PTY_TESTS") == "1", "PTY test runs on Unix by default");
        var ct = TestContext.CancellationToken;
        var config = Directory.CreateTempSubdirectory("ilrepl-history-").FullName;
        try
        {
            Hex1bTerminalBuilder Builder() => Hex1bTerminal.CreateBuilder()
                .WithPtyProcess(options =>
                {
                    // The child is started through env so the config directory reaches it.
                    options.FileName = "/usr/bin/env";
                    options.Arguments = ["XDG_CONFIG_HOME=" + config, "NO_COLOR=", Hosting.HostLocator.FindDotnet(), RepoPaths.FrontEndAssembly];
                    options.WorkingDirectory = RepoPaths.Root;
                })
                .WithHeadless()
                .WithDimensions(100, 30);

            await using (var first = Builder().Build())
            {
                var run = first.RunAsync(ct);
                var auto = new Hex1bTerminalAutomator(first, defaultTimeout: TimeSpan.FromSeconds(30));
                await auto.WaitUntilTextAsync("il[1]>");
                foreach (var line in new[] { ".method int32 Twice(int32 n) {", "ldarg n", "ldc.i4 2", "mul", "ret", "}" })
                {
                    await auto.TypeAsync(line, ct: ct);
                    await auto.EnterAsync(ct: ct);
                }

                await auto.WaitUntilTextAsync("end of method Twice");
                await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
                Assert.AreEqual(0, await run);
            }

            var file = Path.Combine(config, "ilrepl", "history");
            var content = await File.ReadAllTextAsync(file, ct);
            Assert.HasCount(1, content.Split('\n').Where(l => l.StartsWith("# ", StringComparison.Ordinal)).ToList(), "one entry:\n" + content);
            Assert.Contains("+.method int32 Twice(int32 n) {\n+  ldarg n\n+  ldc.i4 2\n+  mul\n+  ret\n+}\n", content);

            await using (var second = Builder().Build())
            {
                var run = second.RunAsync(ct);
                var auto = new Hex1bTerminalAutomator(second, defaultTimeout: TimeSpan.FromSeconds(30));
                await auto.WaitUntilTextAsync("il[1]>");
                await auto.UpAsync(ct: ct);
                await auto.WaitUntilTextAsync("editing 6 lines");
                await auto.WaitUntilTextAsync("il[1]> .method int32 Twice(int32 n) {");
                await auto.WaitUntilTextAsync("  ...> }");
                await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: ct);
                await auto.WaitUntilNoTextAsync("editing");
                await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
                Assert.AreEqual(0, await run);
            }
        }
        finally
        {
            Directory.Delete(config, recursive: true);
        }
    }

    /// <summary>
    /// A pasted method of two hundred lines goes through the real host in well under five seconds.
    /// </summary>
    [TestMethod]
    // Measure the wall-clock budget without other tests launching and driving competing child processes.
    [DoNotParallelize]
    public async Task Pty_Pastes200LineMethod_CompletesWithinFiveSeconds()
    {
        TestSkip.Unless(!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("ILREPL_PTY_TESTS") == "1",
            "PTY test runs on Unix by default");
        var ct = TestContext.CancellationToken;
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithPtyProcess(options =>
            {
                options.FileName = Hosting.HostLocator.FindDotnet();
                options.Arguments = [RepoPaths.FrontEndAssembly, "--no-history"];
                options.WorkingDirectory = RepoPaths.Root;
                options.Environment = new Dictionary<string, string> { ["TERM"] = "xterm-256color", ["NO_COLOR"] = "" };
            })
            .WithHeadless()
            .WithDimensions(100, 30)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(30));
        await auto.WaitUntilTextAsync("il[1]>");
        var lines = new List<string> { ".method int32 Big() {", "  ldc.i4 7" };
        lines.AddRange(Enumerable.Repeat("  nop", 196));
        lines.Add("  ret");
        lines.Add("}");
        Assert.HasCount(200, lines);
        await terminal.SendInputAsync(Encoding.UTF8.GetBytes("\x1b[200~" + string.Join('\n', lines) + "\n\x1b[201~"), ct);
        await auto.WaitUntilTextAsync("Enter sends 200 lines");
        var watch = Stopwatch.StartNew();
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("end of method Big");
        watch.Stop();
        TestContext.WriteLine($"200 lines through the host in {watch.Elapsed.TotalSeconds:F2} s");
        Assert.IsLessThan(TimeSpan.FromSeconds(5), watch.Elapsed, $"took {watch.Elapsed}");
        await auto.TypeAsync("call int32 Big()", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.TypeAsync("ret", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("= 7 : int32");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        Assert.AreEqual(0, await run);
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
