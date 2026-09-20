using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Exercises metadata refresh independently of a running cell that loads an assembly before waiting for release.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class EngineAssemblyRefreshTests
{
    /// <summary>
    /// Supplies cancellation and isolated child-process reporting.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Completion and analysis see a real user-loaded assembly while the same invocation remains blocked.
    /// </summary>
    /// <param name="useHost">Whether to use the real child-process transport.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task UserAssemblyLoad_RefreshesAcceptedSnapshotBeforeExecutionSettles(bool useHost) =>
        IsolatedTestProcess.WithDirectoryAsync(TestContext, async directory =>
        {
            var ct = TestContext.CancellationToken;
            using var fixture = new SessionDependencyFixture();
            fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42);
            var image = Path.Join(directory, fixture.AssemblyName + ".dll");
            await File.WriteAllBytesAsync(image, fixture.PackageImage(fixture.AssemblyName, "1.0.0"), ct);
            var marker = Path.Join(directory, "entered");
            var release = Path.Join(directory, "release");
            await using var engine = useHost ? (IReplEngine)await HostPaths.StartEngineAsync(ct) : new InProcessEngine();
            Task<HandleReply>? running = null;
            try
            {
                foreach (var line in new[]
                {
                    "ldstr " + LiteralParser.Escape(image), "call System.Reflection.Assembly::LoadFrom(string)", "pop",
                    "ldstr " + LiteralParser.Escape(marker), "ldstr \"loaded\"", "call File::WriteAllText(string, string)",
                    "WAIT: ldstr " + LiteralParser.Escape(release), "call File::Exists(string)", "brtrue DONE",
                    "call Thread::Yield()", "pop", "br WAIT", "DONE: nop",
                })
                {
                    Assert.IsTrue((await engine.HandleAsync(line, ct)).Succeeded, line);
                }

                var before = engine.Status;
                var version = engine.AssemblyVersion;
                var prefix = "call [" + fixture.AssemblyName + "]DependencySamples.Values::Read";
                var request = new CompletionRequest([prefix], 0, prefix.Length, null, []);
                Assert.IsEmpty((await engine.CompleteAsync(request, ct)).Items, "The fixture is not loaded before user invocation.");
                running = engine.HandleAsync("ret", ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                while (!File.Exists(marker))
                {
                    await Task.Delay(5, timeout.Token);
                }

                Assert.IsFalse(running.IsCompleted);
                Assert.IsGreaterThan(version, await engine.WaitForAssembliesAsync(version, timeout.Token));

                var completion = await engine.CompleteAsync(request, timeout.Token);
                Assert.HasCount(1, completion.Items);
                Assert.AreEqual("Read()", completion.Items[0].Name);
                Assert.EndsWith("::Read()", completion.Items[0].InsertText);
                Assert.AreEqual(before.Revision, completion.Revision);
                Assert.AreEqual(engine.AssemblyVersion, completion.AssemblyVersion);
                var call = "call int32 [" + fixture.AssemblyName + "]DependencySamples.Values::Read()";
                var analysis = await engine.AnalyzeAsync(new AnalysisRequest([call, "ret"], 1, 0, 37), timeout.Token);
                Assert.IsEmpty(analysis.Diagnostics);
                Assert.AreEqual("[int32]", analysis.Stack!.Render());
                Assert.AreEqual(before.Revision, analysis.Revision);
                Assert.AreEqual(engine.AssemblyVersion, analysis.AssemblyVersion);
                Assert.AreEqual(before, engine.Status, "Metadata refresh must not publish uncommitted source or runtime state.");
                Assert.IsFalse(running.IsCompleted, "Both editing requests complete before the user invocation is released.");
                await File.WriteAllTextAsync(release, "continue", ct);
                Assert.IsTrue((await running).Succeeded);
                var completedCall = prefix[..completion.ReplaceStart] + completion.Items[0].InsertText
                    + prefix[(completion.ReplaceStart + completion.ReplaceLength)..];
                Assert.IsTrue((await engine.HandleAsync(completedCall, ct)).Succeeded);
                var result = await engine.HandleAsync("ret", ct);
                Assert.IsTrue(result.Succeeded);
                Assert.Contains(line => line.Kind == LineKind.Result && line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal),
                    result.Lines);
            }
            finally
            {
                if (running is not null)
                {
                    await File.WriteAllTextAsync(release, "continue", CancellationToken.None);
                    await running;
                }
            }
        });
}
