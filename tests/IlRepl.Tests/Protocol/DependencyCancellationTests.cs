using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Interrupts real NuGet transfers and SDK builds without replacing the host or adopting incomplete references.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class DependencyCancellationTests
{
    /// <summary>
    /// Supplies cancellation for child processes and condition-based waits.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Cancelling a real package transfer preserves accepted IL and the runtime without publishing the candidate graph.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task PackageTransfer_InterruptPreservesRuntimeAndAcceptedSource()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42);
        await using var feed = new SessionAuthenticatedFeed(
            Path.Join(fixture.FeedPath, fixture.AssemblyName + ".1.0.0.nupkg"), fixture.AssemblyName, "1.0.0")
        { HoldPackage = true };
        fixture.UseAuthenticatedFeed(feed);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        Assert.IsTrue((await controller.HandleAsync("ldc.i4.s 42", TestContext.CancellationToken)).Succeeded);
        var epoch = controller.AssemblyVersion >> 32;
        var running = controller.HandleAsync(".load nuget:" + fixture.AssemblyName + ",[1.0.0]", TestContext.CancellationToken);
        await feed.PackageRequested.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
        Assert.IsFalse(running.IsCompleted);
        Assert.AreEqual(ExecutionPhase.Cooperative, controller.Progress.Phase);
        Assert.IsTrue(await controller.InterruptAsync(controller.Progress.Identity, TestContext.CancellationToken));
        await Assert.ThrowsAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.CancellationToken));
        await AssertPreservedAsync(controller, epoch);
        Assert.IsFalse(File.Exists(Path.Join(fixture.PackageCachePath, fixture.AssemblyName.ToLowerInvariant(),
            "1.0.0", ".nupkg.metadata")));
    }

    /// <summary>
    /// Cancelling an active SDK target terminates the build process and retains the original running experiment.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task ProjectBuild_InterruptTerminatesChildAndPreservesRuntime()
    {
        using var fixture = new SessionDependencyFixture();
        var project = fixture.WriteProject();
        var marker = Path.Join(fixture.DirectoryPath, "build.pid");
        var release = Path.Join(fixture.DirectoryPath, "build.release");
        var xml = XDocument.Load(project);
        xml.Root!.Add(new XElement("UsingTask", new XAttribute("TaskName", "AwaitCancellationGate"),
            new XAttribute("TaskFactory", "RoslynCodeTaskFactory"),
            new XAttribute("AssemblyFile", "$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll"),
            new XElement("ParameterGroup", new XElement("Marker", new XAttribute("Required", "true")),
                new XElement("Release", new XAttribute("Required", "true"))),
            new XElement("Task", new XElement("Code", new XAttribute("Type", "Fragment"), new XAttribute("Language", "cs"),
                new XCData("System.IO.File.WriteAllText(Marker + \".pending\","
                    + "System.Diagnostics.Process.GetCurrentProcess().Id.ToString());"
                    + "System.IO.File.Move(Marker + \".pending\", Marker);"
                    + "while (!System.IO.File.Exists(Release)) System.Threading.Thread.Yield();")))));
        xml.Root.Add(new XElement("Target", new XAttribute("Name", "AwaitInterrupt"), new XAttribute("BeforeTargets", "CoreCompile"),
            new XElement("AwaitCancellationGate", new XAttribute("Marker", marker), new XAttribute("Release", release))));
        xml.Save(project);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        Assert.IsTrue((await controller.HandleAsync("ldc.i4.s 42", TestContext.CancellationToken)).Succeeded);
        var epoch = controller.AssemblyVersion >> 32;
        var running = controller.HandleAsync(".load \"" + project + "\"", TestContext.CancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            while (!File.Exists(marker))
            {
                if (running.IsCompleted)
                {
                    Assert.Fail(string.Join('\n', (await running).Lines.Select(line => line.PlainText)));
                }

                await Task.Delay(10, deadline.Token);
            }

            var pid = int.Parse(await File.ReadAllTextAsync(marker, deadline.Token), CultureInfo.InvariantCulture);
            using var build = Process.GetProcessById(pid);
            Assert.IsFalse(build.HasExited);
            Assert.AreEqual(ExecutionPhase.Cooperative, controller.Progress.Phase);
            Assert.IsTrue(await controller.InterruptAsync(controller.Progress.Identity, TestContext.CancellationToken));
            await Assert.ThrowsAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10),
                TestContext.CancellationToken));
            await build.WaitForExitAsync(deadline.Token);
            Assert.IsTrue(build.HasExited);
            await AssertPreservedAsync(controller, epoch);
        }
        finally
        {
            await File.WriteAllTextAsync(release, "release", CancellationToken.None);
            if (!running.IsCompleted)
            {
                await controller.InterruptAsync(controller.Progress.Identity, CancellationToken.None);
                try
                {
                    await running.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private async Task AssertPreservedAsync(SessionController controller, long epoch)
    {
        Assert.AreEqual(epoch, controller.AssemblyVersion >> 32);
        Assert.AreEqual(SessionRuntimeState.Ready, controller.RuntimeState);
        Assert.IsFalse(controller.Progress.IsRunning);
        var capture = await controller.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Capture },
        }, TestContext.CancellationToken);

        Assert.IsEmpty(capture.Document.References);
        var result = await controller.HandleAsync("ret", TestContext.CancellationToken);
        Assert.IsTrue(result.Succeeded);
        Assert.Contains(line => line.Kind == LineKind.Result && line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal),
            result.Lines);
    }
}
