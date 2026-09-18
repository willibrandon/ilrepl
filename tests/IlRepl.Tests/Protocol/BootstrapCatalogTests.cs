using System.Text.Json;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Keeps the AOT frontend's embedded vocabulary identical to the authoritative engine and live handshake.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class BootstrapCatalogTests
{
    /// <summary>
    /// Supplies cancellation for the real host handshake.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Generated startup data matches every command, opcode detail, tokenizer word, and initial session field.
    /// </summary>
    [TestMethod]
    public async Task GeneratedCatalog_MatchesEngineAndHostHello()
    {
        var embedded = BootstrapCatalog.Hello;
        using var core = new ReplCore();
        var authoritative = new HostHello(Completer.Catalog, CilVocabularyBuilder.Vocabulary, core.Status);
        Assert.AreEqual(JsonSerializer.Serialize(authoritative, ProtocolJsonContext.Default.HostHello),
            JsonSerializer.Serialize(embedded, ProtocolJsonContext.Default.HostHello),
            "Regenerate the bootstrap catalog with scripts/Generate-BootstrapCatalog.cs when authoritative tables change.");
        await using var engine = await HostPaths.StartEngineAsync(TestContext.CancellationToken);
        var actual = new HostHello(engine.Catalog, engine.Vocabulary, engine.Status);
        Assert.AreEqual(JsonSerializer.Serialize(embedded, ProtocolJsonContext.Default.HostHello),
            JsonSerializer.Serialize(actual, ProtocolJsonContext.Default.HostHello));
    }

    /// <summary>
    /// Session restart is discoverable as a verb and accepts no filename or option operand.
    /// </summary>
    [TestMethod]
    public async Task SessionRestart_IsDiscoverableAndHasNoOperand()
    {
        await using var engine = new InProcessEngine();
        const string source = ".session rest";
        var reply = await engine.CompleteAsync(new CompletionRequest([source], 0, source.Length, null, []),
            TestContext.CancellationToken);
        Assert.Contains(item => item.InsertText == "restart", reply.Items);
        var classifier = new CaretClassifier(new CilTokenizer(engine.Vocabulary));
        Assert.IsFalse(classifier.Classify(".session restart ", 17, inBlockComment: false).IsOperand);
        Assert.Contains("restart", engine.Catalog.Single(item => item.Name == ".session").Detail);
    }
}
