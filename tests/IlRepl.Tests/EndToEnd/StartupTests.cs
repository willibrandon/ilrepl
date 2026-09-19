using System.Diagnostics;
using System.Text.Json;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Engine;
using IlRepl.Processes;
using IlRepl.Protocol;
using IlRepl.Tests.Tui;

namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// Verifies interactive startup ordering using actual terminal frames and independently recorded child process boundaries.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class StartupTests
{
    /// <summary>
    /// Supplies cancellation to terminal input and process cleanup.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The focused prompt renders before the frontend starts its host or Unix supervisor, and accepts a real subsequent cell.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task FirstFrame_PrecedesExecutionProcessStartup()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithPtyProcess(options =>
        {
            options.FileName = HostLocator.FindDotnet();
            options.Arguments = [RepoPaths.FrontEndAssembly, "--no-history"];
            options.WorkingDirectory = files.DirectoryPath;
            options.Environment = new Dictionary<string, string>
            {
                ["TERM"] = "xterm-256color", ["NO_COLOR"] = "", ["ILREPL_MEASUREMENTS_DIRECTORY"] = files.DirectoryPath,
            };
        }).WithHeadless().WithDimensions(100, 30).Build();

        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(20));
        try
        {
            await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("il[1]>") && FrameRecorder.FindCaret(snapshot) is not null);
            await auto.TypeAsync("ldc.i4 42", ct: token);
            await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("ldc.i4 42") && snapshot.ContainsText("stack ")
                && !snapshot.ContainsText("starting execution host"));
            await auto.EnterAsync(ct: token);
            await auto.WaitUntilTextAsync("stack [int32]");
            await auto.TypeAsync("ret", ct: token);
            await auto.EnterAsync(ct: token);
            await auto.WaitUntilTextAsync("= 42 : int32");
            await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
            Assert.AreEqual(0, await run.WaitAsync(token));
        }
        finally
        {
            await terminal.DisposeAsync();
        }

        var records = new List<ProcessMeasurement>();
        foreach (var path in Directory.EnumerateFiles(files.DirectoryPath, "*.json"))
        {
            var record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, token),
                MeasurementJsonContext.Default.ProcessMeasurement);
            Assert.IsNotNull(record);
            records.Add(record);
        }

        // A host that leaves through its hard exit, or that the frontend stops after its grace period, writes no record.
        // Which records exist, and how long the frontend took to leave, tell the two apart.
        TestContext.WriteLine("Measurement records: " + string.Join("; ", records.Select(record => record.Role + " "
            + string.Join(",", record.Stages.OrderBy(stage => stage.Value).Select(stage => stage.Key)))));
        var frontend = records.Single(record => record.Role == "frontend");
        if (frontend.Stages.TryGetValue("exit", out var left) && frontend.Stages.TryGetValue("host-ready", out var ready))
        {
            var elapsed = Stopwatch.GetElapsedTime(ready, left).TotalMilliseconds;
            TestContext.WriteLine($"Frontend left {elapsed:F0} ms after the host was ready.");
        }

        var rendered = frontend.Stages["prompt-rendered"];
        Assert.IsGreaterThanOrEqualTo(rendered, frontend.Stages["host-starting"]);
        Assert.IsGreaterThan(frontend.Stages["host-starting"], frontend.Stages["host-ready"]);
        string[] roles = OperatingSystem.IsWindows() ? ["host"] : ["host", "supervisor"];
        foreach (var role in roles)
        {
            var process = records.Single(record => record.Role == role);
            Assert.IsGreaterThan(rendered, process.Stages["entry"], role + " started before the prompt rendered.");
        }
    }

    /// <summary>
    /// A host that has to leave through its hard exit, because user code is still running when the frontend quits, still writes its record.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task HostRecord_SurvivesTheHardExit()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithPtyProcess(options =>
        {
            options.FileName = HostLocator.FindDotnet();
            options.Arguments = [RepoPaths.FrontEndAssembly, "--no-history"];
            options.WorkingDirectory = files.DirectoryPath;
            options.Environment = new Dictionary<string, string>
            {
                ["TERM"] = "xterm-256color", ["NO_COLOR"] = "", ["ILREPL_MEASUREMENTS_DIRECTORY"] = files.DirectoryPath,
            };
        }).WithHeadless().WithDimensions(100, 30).Build();

        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(20));
        try
        {
            await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("il[1]>") && FrameRecorder.FindCaret(snapshot) is not null);
            string[] lines =
            [
                "ldstr " + LiteralParser.Escape(files.MarkerPath), "ldstr \"running\"",
                "call void System.IO.File::WriteAllText(string, string)", "ldc.i4 20000",
                "call void System.Threading.Thread::Sleep(int32)",
            ];

            for (var index = 0; index < lines.Length; index++)
            {
                var count = index == 0 ? "1 instruction " : (index + 1) + " instructions";
                await auto.TypeAsync(lines[index], ct: token);

                // Enter before the host is ready keeps the line in the prompt, so the first line waits for the host.
                await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("stack ") && !snapshot.ContainsText("starting execution host"));
                await auto.EnterAsync(ct: token);
                await auto.WaitUntilAsync(snapshot => snapshot.ContainsText(count));
            }

            // The sleeping cell cannot be cancelled, so the host's engine cannot settle and the host takes its hard exit.
            await auto.TypeAsync("ret", ct: token);
            await auto.EnterAsync(ct: token);
            await auto.WaitUntilAsync(_ => File.Exists(files.MarkerPath));
            await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
            await run.WaitAsync(token);
        }
        finally
        {
            await terminal.DisposeAsync();
        }

        var roles = new List<string>();
        foreach (var path in Directory.EnumerateFiles(files.DirectoryPath, "*.json"))
        {
            var record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, token),
                MeasurementJsonContext.Default.ProcessMeasurement);
            Assert.IsNotNull(record);
            roles.Add(record.Role);
        }

        Assert.Contains("host", roles, "The host left no record: " + string.Join(", ", roles));
    }
}
