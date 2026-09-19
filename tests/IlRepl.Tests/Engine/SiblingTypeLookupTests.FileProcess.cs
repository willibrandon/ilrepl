using System.Diagnostics;

namespace IlRepl.Tests.Engine;

/// <summary>
/// File-based sibling activation releases actual runtime assembly locks before its parent removes the fixture files.
/// </summary>
public sealed partial class SiblingTypeLookupTests
{
    private const string FileProbeDirectory = "ILREPL_SIBLING_LOOKUP_PROBE_DIRECTORY";

    /// <summary>
    /// All file activation overloads execute in a real child process on every platform without adding a skipped probe test.
    /// </summary>
    /// <returns>The completed child execution and unconditional parent cleanup.</returns>
    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task Edit_FileNameOnlySiblingsPreserveContext()
    {
        var directory = Environment.GetEnvironmentVariable(FileProbeDirectory);
        if (directory is not null)
        {
            await RunCaseAsync("activator-from", "plain", 2, false, false, false, "literal", directory);
            await RunCaseAsync("activator-from", "nested", 3, false, true, false, "return", directory);
            await RunCaseAsync("activator-from", "generic", 8, true, true, false, "argument", directory);
            return;
        }

        directory = Path.Combine(Path.GetTempPath(), "sibling-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!, WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };

            start.ArgumentList.Add("--filter");
            start.ArgumentList.Add("FullyQualifiedName~SiblingTypeLookupTests.Edit_FileNameOnlySiblingsPreserveContext");
            start.Environment[FileProbeDirectory] = directory;
            using var child = Process.Start(start) ?? throw new InvalidOperationException("the sibling activation probe did not start");
            var output = child.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
            var error = child.StandardError.ReadToEndAsync(TestContext.CancellationToken);
            try
            {
                await child.WaitForExitAsync(TestContext.CancellationToken);
                Assert.AreEqual(0, child.ExitCode, await output + Environment.NewLine + await error);
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
