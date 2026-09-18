using System.Diagnostics;
using IlRepl.Processes;
using IlRepl.Protocol;

namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// Exercises session filename dispatch, explicit execution and failures through the actual command-line executable.
/// </summary>
[TestClass]
public sealed class SessionCliTests
{
    /// <summary>
    /// Cancels child processes and file operations when a test is interrupted.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Opening through either CLI form prints numbered saved input and historical output without repeating its side effects.
    /// </summary>
    /// <param name="option">Whether opening uses the explicit session option.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OpenSession_PrintsSavedHistoryWithoutExecuting(bool option)
    {
        using var files = new SessionWorkspaceFixture();
        await files.WriteAsync(files.CompletedDocument(new SessionEditor { Lines = ["// unsent draft", "ldc.i4 123"] }),
            TestContext.CancellationToken);
        File.Delete(files.MarkerPath);

        var result = await RunAsync(option ? ["--session", files.SessionPath] : [files.SessionPath]);

        Assert.AreEqual(0, result.Code, result.StdOut + result.StdErr);
        Assert.Contains("saved session history (no code executed)", result.StdOut);
        Assert.Contains("1: cell, succeeded (historical)", result.StdOut);
        Assert.Contains("il[1]> // saved café λ", result.StdOut);
        Assert.Contains("il[1]> ldc.i4.s 42", result.StdOut);
        Assert.Contains("saved stdout", result.StdOut);
        Assert.Contains("= 42 : int32", result.StdOut);
        Assert.Contains("end of saved history; no code executed", result.StdOut);
        Assert.Contains("editor draft (not executed)", result.StdOut);
        Assert.Contains("// unsent draft", result.StdOut);
        Assert.Contains("ldc.i4 123", result.StdOut);
        Assert.DoesNotContain("= 123 : int32", result.StdOut);
        Assert.IsFalse(File.Exists(files.MarkerPath));
    }

    /// <summary>
    /// Both explicit and positional session paths reopen accepted source without executing it.
    /// </summary>
    /// <param name="option">Whether the path follows the explicit session option.</param>
    /// <param name="uppercase">Whether the document suffix uses uppercase letters.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task OpenSession_DoesNotExecuteAtEndOfInput(bool option, bool uppercase)
    {
        using var files = new SessionWorkspaceFixture();
        var path = uppercase ? files.SessionPath.Replace(".ilrepl.json", ".ILREPL.JSON", StringComparison.Ordinal) : files.SessionPath;
        await File.WriteAllBytesAsync(path, SessionCodec.Write(files.PendingDocument()), TestContext.CancellationToken);
        string[] arguments = option ? ["--session", path] : [path];

        var (code, stdout, stderr) = await RunAsync(arguments);

        Assert.AreEqual(0, code, stderr + stdout);
        Assert.Contains("Session opened. Nothing has run yet. Saved output is shown for reference.", stdout);
        Assert.DoesNotContain("= 42 : int32", stdout);
        Assert.IsFalse(File.Exists(files.MarkerPath));
    }

    /// <summary>
    /// Explicit run executes the accepted body and terminates with the reported result.
    /// </summary>
    /// <param name="option">Whether opening uses the session option or positional path.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RunSession_ExecutesAndExits(bool option)
    {
        using var files = new SessionWorkspaceFixture();
        await files.WriteAsync(null, TestContext.CancellationToken);
        string[] arguments = option ? ["--session", files.SessionPath, "--run"] : [files.SessionPath, "--run"];

        var (code, stdout, stderr) = await RunAsync(arguments);

        Assert.AreEqual(0, code, stderr + stdout);
        Assert.Contains("= 42 : int32", stdout);
        Assert.AreEqual("executed", await File.ReadAllTextAsync(files.MarkerPath, TestContext.CancellationToken));
    }

    /// <summary>
    /// Invalid execution and duplicate file combinations fail before any session code runs.
    /// </summary>
    /// <param name="combination">The invalid option combination to request.</param>
    [TestMethod]
    [DataRow("missing")]
    [DataRow("eval")]
    [DataRow("duplicate")]
    [DataRow("script")]
    public async Task SessionOptions_RejectConflictingInputs(string combination)
    {
        using var files = new SessionWorkspaceFixture();
        await files.WriteAsync(null, TestContext.CancellationToken);
        var script = Path.Combine(files.DirectoryPath, "script.il");
        await File.WriteAllTextAsync(script, "ldc.i4.1\nret", TestContext.CancellationToken);
        string[] arguments = combination switch
        {
            "missing" => ["--run"],
            "eval" => [files.SessionPath, "--run", "--eval", "ldc.i4.1; ret"],
            "duplicate" => [files.SessionPath, "--session", files.SessionPath],
            _ => [script, "--session", files.SessionPath],
        };

        var (code, stdout, stderr) = await RunAsync(arguments);

        Assert.AreEqual(2, code, stdout + stderr);
        Assert.IsNotEmpty(stderr);
        Assert.DoesNotContain("= 42 : int32", stdout);
        Assert.IsFalse(File.Exists(files.MarkerPath));
    }

    /// <summary>
    /// Missing files are usage failures while malformed session content is a document failure.
    /// </summary>
    /// <param name="missing">Whether the file is absent rather than malformed.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OpenSession_FailureUsesExpectedExitCode(bool missing)
    {
        using var files = new SessionWorkspaceFixture();
        if (!missing)
        {
            await File.WriteAllTextAsync(files.SessionPath, "{ invalid", TestContext.CancellationToken);
        }

        var (code, stdout, stderr) = await RunAsync([files.SessionPath]);

        Assert.AreEqual(missing ? 2 : 1, code, stdout + stderr);
        Assert.IsNotEmpty(stderr);
        Assert.DoesNotContain("Session opened.", stdout);
        Assert.IsFalse(File.Exists(files.MarkerPath));
    }

    /// <summary>
    /// A real host reports an unreadable session as an input failure without modifying or executing its source.
    /// </summary>
    [TestMethod]
    public async Task OpenSession_ExclusiveFileLockUsesInputFailureExitCode()
    {
        using var files = new SessionWorkspaceFixture();
        await files.WriteAsync(null, TestContext.CancellationToken);
        var original = await File.ReadAllBytesAsync(files.SessionPath, TestContext.CancellationToken);
        (int Code, string StdOut, string StdErr) result;
        await using (var held = new FileStream(files.SessionPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await RunAsync([files.SessionPath, "--run"]);
        }

        Assert.AreEqual(2, result.Code, result.StdOut + result.StdErr);
        Assert.Contains(Path.GetFileName(files.SessionPath), result.StdErr);
        Assert.DoesNotContain("Session opened.", result.StdOut);
        Assert.DoesNotContain("= 42 : int32", result.StdOut);
        Assert.IsFalse(File.Exists(files.MarkerPath));
        Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(files.SessionPath, TestContext.CancellationToken));
    }

    /// <summary>
    /// The familiar save and load commands use the session suffix without writing or loading a PE image.
    /// </summary>
    [TestMethod]
    public async Task SessionSuffix_DispatchesSaveAndLoadAliases()
    {
        using var files = new SessionWorkspaceFixture();
        var path = Path.Combine(files.DirectoryPath, "aliases.ILREPL.JSON");
        var saved = await RunAsync(["--eval", $".method int32 Value() {{; ldc.i4.7; ret; }}; .save \"{path}\""]);

        Assert.AreEqual(0, saved.Code, saved.StdOut + saved.StdErr);
        var document = SessionCodec.Read(await File.ReadAllBytesAsync(path, TestContext.CancellationToken));
        Assert.Contains(entry => entry.Source.Contains(".method int32 Value() {"), document.Entries);

        var opened = await RunAsync(["--eval", $".load \"{path}\"; .methods"]);

        Assert.AreEqual(0, opened.Code, opened.StdOut + opened.StdErr);
        Assert.Contains("Session opened. Nothing has run yet. Saved output is shown for reference.", opened.StdOut);
        Assert.Contains("Value", opened.StdOut);
        Assert.DoesNotContain("= 7 : int32", opened.StdOut);
    }

    private async Task<(int Code, string StdOut, string StdErr)> RunAsync(string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = HostLocator.FindDotnet(),
            WorkingDirectory = RepoPaths.Root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(RepoPaths.FrontEndAssembly);
        startInfo.ArgumentList.Add("--no-color");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The frontend did not start.");
        try
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
            await process.WaitForExitAsync(TestContext.CancellationToken);
            return (process.ExitCode, await output, await error);
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
}
