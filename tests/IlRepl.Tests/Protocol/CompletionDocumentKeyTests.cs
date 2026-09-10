using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Completion query identity includes every meaningful document change and compares copied anchors by value.
/// </summary>
[TestClass]
public sealed class CompletionDocumentKeyTests
{
    /// <summary>
    /// Equivalent documents and anchors compare equally even when their collection instances and cursors differ.
    /// </summary>
    [TestMethod]
    public void Equality_UsesContentsAndExcludesOnlyTheCursor()
    {
        var first = new CompletionDocumentKey(new CompletionRequest(
            ["call List<", "DONE: ret"], 0, 10, null, [new ContinuationAnchor(0, 5, 10, "selected")], true));
        var second = new CompletionDocumentKey(new CompletionRequest(
            ["call List<", "DONE: ret"], 0, 10, "page", [new ContinuationAnchor(0, 5, 10, "selected")], true));
        Assert.AreEqual(first, second);
        Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
        Assert.AreEqual("page", first.Request("page").Cursor);
    }

    /// <summary>
    /// A changed suffix, future label, caret, mode or generic selection makes an old page belong to another query.
    /// </summary>
    [TestMethod]
    public void Equality_IncludesTheWholeQuery()
    {
        var request = new CompletionRequest(["call List<int32>", "DONE: ret"], 0, 10, null,
            [new ContinuationAnchor(0, 5, 10, "selected")]);
        var key = new CompletionDocumentKey(request);
        CompletionRequest[] changes = [request with { Lines = ["call List<string>", "DONE: ret"] },
            request with { Lines = ["call List<int32>", "OTHER: ret"] }, request with { Caret = 9 },
            request with { Explicit = true }, request with { Anchors = [new ContinuationAnchor(0, 5, 10, "other")] }];
        foreach (var change in changes)
        {
            Assert.AreNotEqual(key, new CompletionDocumentKey(change));
        }
    }

    /// <summary>
    /// Mutating a caller's arrays after capture cannot change a key or the request reconstructed from it.
    /// </summary>
    [TestMethod]
    public void Capture_OwnsItsDocumentAndAnchors()
    {
        string[] lines = ["call List<"];
        ContinuationAnchor[] anchors = [new(0, 5, 10, "selected")];
        var key = new CompletionDocumentKey(new CompletionRequest(lines, 0, 10, null, anchors));
        lines[0] = "ret";
        anchors[0] = new ContinuationAnchor(0, 0, 1, "other");
        Assert.AreEqual("call List<", key.Lines[0]);
        Assert.AreEqual("selected", key.Anchors[0].Token);
    }
}
