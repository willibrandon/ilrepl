using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using IlRepl.Processes;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Responsiveness;

/// <summary>
/// Validates small generated workloads through actual metadata readers and session hydration.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class ResponsivenessFixtureTests
{
    /// <summary>
    /// Supplies cancellation to real engine operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The generated assembly has exact metadata dimensions and exposes real callable members to completion.
    /// </summary>
    [TestMethod]
    public async Task Catalog_HasRequestedMetadataAndCompletableMethods()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-responsiveness-").FullName;
        try
        {
            var path = ResponsivenessFixtures.Assembly(directory, 32, 10);
            using var image = File.OpenRead(path);
            using var pe = new PEReader(image);
            var metadata = pe.GetMetadataReader();
            Assert.HasCount(33, metadata.TypeDefinitions);
            Assert.HasCount(320, metadata.MethodDefinitions);
            await using var engine = await HostProcessEngine.StartAsync(cancellationToken: TestContext.CancellationToken);
            var loaded = await engine.HandleAsync(".load " + path, TestContext.CancellationToken);
            Assert.IsTrue(loaded.Succeeded);
            const string text = "call Responsiveness.CatalogType00031::Method";
            var reply = await engine.CompleteAsync(new CompletionRequest([text], 0, text.Length, null, []),
                TestContext.CancellationToken);
            Assert.HasCount(10, reply.Items);
            Assert.Contains(item => item.InsertText.Contains("Method09", StringComparison.Ordinal), reply.Items);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Generated historical cells round trip and hydrate real definitions without running old cells.
    /// </summary>
    [TestMethod]
    public async Task Session_HydratesDefinitionsAndRetainsHistoricalCells()
    {
        var document = SessionCodec.Read(SessionCodec.Write(ResponsivenessFixtures.Session(20, 4)));
        Assert.HasCount(20, document.Cells);
        await using var engine = new InProcessEngine();
        var restored = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Hydrate }, Document = document,
        }, TestContext.CancellationToken);
        Assert.IsTrue(restored.Reply.Succeeded);
        Assert.IsTrue((await engine.HandleAsync("call int32 Retained3()", TestContext.CancellationToken)).Succeeded);
        var result = await engine.HandleAsync("ret", TestContext.CancellationToken);
        Assert.IsTrue(result.Succeeded);
        Assert.Contains(line => line.PlainText.Contains("= 3 : int32", StringComparison.Ordinal), result.Lines);
        Assert.HasCount(200, ResponsivenessFixtures.Draft(200).Split('\n'));
        Assert.AreEqual("ldtoken List<List<int32>>", ResponsivenessFixtures.Generic(2));
    }
}
