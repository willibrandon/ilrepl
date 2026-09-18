using System.Diagnostics;
using System.Runtime.InteropServices;
using IlRepl.Processes;

namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// Verifies native inspection through the packaged frontend with inherited operating-system runtime configuration.
/// </summary>
[TestClass]
public sealed class NativeCliTests
{
    /// <summary>
    /// Supplies cancellation to the actual frontend and its child workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A temporary directory beyond Darwin's socket limit still permits the worker diagnostics handshake and clean exit.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Inspect_LongTemporaryDirectoryUsesWorkingDiagnosticEndpoint()
    {
        using var files = new SessionWorkspaceFixture();
        var temporary = Path.Combine(files.DirectoryPath, new string('a', 55), new string('b', 55));
        Directory.CreateDirectory(temporary);
        Assert.IsGreaterThan(104, temporary.Length);
        var start = new ProcessStartInfo
        {
            FileName = HostLocator.FindDotnet(), WorkingDirectory = RepoPaths.Root,
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(RepoPaths.FrontEndAssembly);
        start.ArgumentList.Add("--no-color");
        start.ArgumentList.Add("--eval");
        start.ArgumentList.Add(".jit --info");
        start.Environment["TMPDIR"] = temporary;
        start.Environment["TMP"] = temporary;
        start.Environment["TEMP"] = temporary;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The frontend did not start.");
        try
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);

            await process.WaitForExitAsync(TestContext.CancellationToken);

            var stdout = await output;
            var stderr = await error;
            Assert.AreEqual(0, process.ExitCode, stdout + stderr);
            Assert.Contains("native inspection: complete", stdout);
            Assert.Contains("host capabilities: complete", stdout);
            Assert.Contains(RuntimeInformation.ProcessArchitecture.ToString(), stdout);
            Assert.Contains("FullOpts", stdout);
            Assert.DoesNotContain("no complete listing", stdout);
            Assert.IsEmpty(Directory.EnumerateDirectories(temporary, "ilrepl-native-*"));
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
