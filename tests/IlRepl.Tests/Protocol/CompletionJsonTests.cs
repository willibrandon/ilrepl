using System.Text.Json;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Completion documents, anchors and query identities survive the source-generated wire contract.
/// </summary>
[TestClass]
public sealed class CompletionJsonTests
{
    /// <summary>
    /// The complete request round-trips without losing future lines, explicit mode or anchored definitions.
    /// </summary>
    [TestMethod]
    public void Request_RoundTripsEveryQueryComponent()
    {
        var request = new CompletionRequest(["call M<str", "DONE: nop"], 0, 10, "page", [new(0, 5, 7, "owner")], true);
        var json = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.CompletionRequest);
        var restored = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.CompletionRequest);
        Assert.IsNotNull(restored);
        Assert.AreEqual(new CompletionDocumentKey(request), new CompletionDocumentKey(restored));
        Assert.AreEqual(request.Cursor, restored.Cursor);
    }

    /// <summary>
    /// The reply retains its edit range, generic continuation, complete detail and paging identity.
    /// </summary>
    [TestMethod]
    public void Reply_RoundTripsEveryCandidateComponent()
    {
        var item = new CompletionItem("Dictionary<TKey, TValue>", "type arguments", "System.Collections.Generic", false)
        {
            Kind = CompletionKind.TypeArguments, Insert = "Dictionary<", Continues = true, Continuation = "owner",
            CaretOffset = 11, FullDetail = "Dictionary<TKey, TValue>\nSystem.Collections.Generic", Owner = "T of List<T>",
        };
        var reply = new CompletionReply(CompletionKind.Types, 8, 4, [item], "next", 90, true, 12, "query", 3, ["List<T>"]);
        var json = JsonSerializer.Serialize(reply, ProtocolJsonContext.Default.CompletionReply);
        var restored = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.CompletionReply);
        Assert.IsNotNull(restored);
        Assert.AreEqual(reply with { Items = restored.Items, Owners = restored.Owners }, restored);
        Assert.AreEqual(item, restored.Items.Single());
        Assert.AreSequenceEqual(reply.Owners, restored.Owners);
    }

    /// <summary>
    /// The first-word catalog keeps its existing four-field JSON shape.
    /// </summary>
    [TestMethod]
    public void CatalogItem_PreservesItsWireShape()
    {
        var item = new CompletionItem("call", "", "call a method", true);
        var json = JsonSerializer.Serialize(item, ProtocolJsonContext.Default.CompletionItem);
        Assert.AreEqual("{\"name\":\"call\",\"detail\":\"\",\"description\":\"call a method\",\"takesOperand\":true}", json);
    }
}
