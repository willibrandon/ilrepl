using System.Diagnostics;
using IlRepl.Engine;
using IlRepl.Processes;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Verifies that completed host disposal releases actual loaded images and the child's working directory.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class HostProcessCleanupTests
{
    /// <summary>
    /// Supplies cancellation for real host execution and termination.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Graceful and forced shutdown release path-loaded assemblies before filesystem cleanup begins.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Disposal_ReleasesLoadedAssemblyAndWorkingDirectory(bool terminate)
    {
        var token = TestContext.CancellationToken;
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42);
        var directory = Directory.CreateDirectory(Path.Join(fixture.DirectoryPath, "working")).FullName;
        var image = Path.Join(directory, fixture.AssemblyName + ".dll");
        await File.WriteAllBytesAsync(image, fixture.PackageImage(fixture.AssemblyName, "1.0.0"), token);
        await using var engine = await HostProcessEngine.StartAsync(HostPaths.HostAssembly, directory, null, token);
        using var process = Process.GetProcessById(engine.ProcessId);
        _ = process.SafeHandle;
        Assert.IsTrue((await engine.HandleAsync(".load " + LiteralParser.Escape(image), token)).Succeeded);
        Assert.IsTrue((await engine.HandleAsync(
            "call int32 [" + fixture.AssemblyName + "]DependencySamples.Values::Read()", token)).Succeeded);
        var result = await engine.HandleAsync("ret", token);
        Assert.IsTrue(result.Succeeded);
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), result.Lines);
        if (terminate)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.ProgressChanged += progress =>
            {
                if (progress.IsRunning && progress.Phase == ExecutionPhase.UserCode)
                {
                    entered.TrySetResult();
                }
            };

            Assert.IsTrue((await engine.HandleAsync("LOOP: br LOOP", token)).Succeeded);
            var running = engine.HandleAsync(".run", token);
            await entered.Task.WaitAsync(token);
            await engine.TerminateAsync(token);
            await Assert.ThrowsExactlyAsync<HostProtocolException>(() => running);
            Assert.IsTrue(running.IsCompleted, "Termination must settle the active raw-host request.");
        }

        await engine.DisposeAsync();
        if (OperatingSystem.IsWindows())
        {
            Assert.IsTrue(process.WaitForExit(0), "The Windows process object must be signalled.");
        }

        process.Dispose();
        Directory.Delete(directory, recursive: true);
        Assert.IsFalse(Directory.Exists(directory));
    }
}
