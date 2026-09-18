using System.Diagnostics;
using System.Text;
using IlRepl.Engine;
using IlRepl.Processes;

namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// Starts real terminal processes with isolated temporary paths that exercise native socket address limits.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class LocalSocketPathTests
{
    /// <summary>
    /// Supplies cancellation to the actual frontend and its owned execution processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Short Unicode paths and oversized ASCII or UTF-8 paths all permit execution without changing the child's temporary directory.
    /// </summary>
    /// <param name="kind">The temporary path's character and encoded-length partition.</param>
    [TestMethod]
    [DataRow("short")]
    [DataRow("ascii")]
    [DataRow("utf8")]
    public async Task Eval_UsesSocketFallbackWithoutChangingTemporaryDirectory(string kind)
    {
        var suffix = kind switch { "ascii" => new string('a', 80), "utf8" => new string('界', 28), _ => "界" };
        var root = OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp";
        var temporary = Path.Combine(root, "ilr-" + Guid.NewGuid().ToString("N")[..8] + "-" + suffix);
        Directory.CreateDirectory(temporary);
        try
        {
            if (kind == "utf8")
            {
                if (!OperatingSystem.IsWindows()) Assert.IsLessThanOrEqualTo(55, temporary.Length);
                Assert.IsGreaterThan(108, Encoding.UTF8.GetByteCount(Path.Combine(temporary, "ilr-0123456789abcdef", "host.sock")));
            }

            var start = new ProcessStartInfo(HostLocator.FindDotnet()) { WorkingDirectory = RepoPaths.Root };
            start.Environment["TMPDIR"] = temporary;
            start.Environment["TMP"] = temporary;
            start.Environment["TEMP"] = temporary;
            var source = "call string Path::GetTempPath(); ldstr " + LiteralParser.Escape(temporary + Path.DirectorySeparatorChar)
                + "; call bool string::op_Equality(string, string); call void Console::WriteLine(bool); ldc.i4.s 42; ret";
            foreach (var argument in new[] { RepoPaths.FrontEndAssembly, "--no-color", "-e", source })
                start.ArgumentList.Add(argument);
            var result = await ToolProcess.RunAsync(start, TestContext.CancellationToken);
            Assert.AreEqual(0, result.ExitCode, result.StandardError + result.StandardOutput);
            Assert.Contains("= 42 : int32", result.StandardOutput);
            Assert.Contains("True" + Environment.NewLine, result.StandardOutput);
            Assert.IsEmpty(Directory.EnumerateDirectories(temporary, "ilr-*"), "Exited owners must remove their socket directories.");
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }
}
