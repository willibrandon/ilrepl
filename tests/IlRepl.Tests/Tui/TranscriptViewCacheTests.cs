using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Verifies that transcript presentation reuse preserves content and invalidates at actual presentation boundaries.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class TranscriptViewCacheTests
{
    /// <summary>
    /// Unchanged frames reuse widgets and folding while appended content preserves earlier line identities.
    /// </summary>
    [TestMethod]
    public void UnchangedFrames_ReuseWidgetsAndFoldedRows()
    {
        var transcript = new Transcript();
        var cache = new TranscriptViewCache();
        var feedback = new YankFeedback();
        transcript.Add(LineKind.Output, "The same long line should keep its folded rows across repeated prompt redraws.");
        var first = cache.Get(transcript, 24, feedback);
        var line = (TranscriptLineWidget)first[0];
        var rows = line.Rows;
        Assert.IsGreaterThan(1, rows.Count);
        Assert.AreSame(first, cache.Get(transcript, 24, feedback));
        Assert.AreSame(rows, line.Rows);
        transcript.Add(LineKind.Output, "next");
        var appended = cache.Get(transcript, 24, feedback);
        Assert.AreSame(line, appended[0]);
        Assert.AreSame(rows, ((TranscriptLineWidget)appended[0]).Rows);
        Assert.HasCount(2, appended);
        Assert.AreEqual("next", ((TranscriptLineWidget)appended[1]).Line.PlainText);
    }

    /// <summary>
    /// Resizing rewraps full Unicode content and record copies cannot reuse folds made for a different width.
    /// </summary>
    [TestMethod]
    public void Resize_RebuildsFoldedRowsWithoutLosingText()
    {
        var transcript = new Transcript();
        var cache = new TranscriptViewCache();
        var feedback = new YankFeedback();
        var text = "世界 alpha beta gamma delta epsilon zeta theta";
        transcript.Add(LineKind.Output, text);
        var wide = (TranscriptLineWidget)cache.Get(transcript, 80, feedback)[0];
        var narrow = (TranscriptLineWidget)cache.Get(transcript, 20, feedback)[0];
        Assert.HasCount(1, wide.Rows);
        Assert.IsGreaterThan(1, narrow.Rows.Count);
        Assert.AreNotSame(wide.Rows, narrow.Rows);
        var flattened = narrow.Rows.Select(row => string.Concat(row.Select(span => span.Text))).Select(row => row.Trim());
        Assert.AreEqual(text, string.Join(' ', flattened));
        var copy = wide with { Width = 20 };
        Assert.AreNotSame(wide.Rows, copy.Rows);
        Assert.HasCount(narrow.Rows.Count, copy.Rows);
    }

    /// <summary>
    /// Retention and clear discard old line widgets even when the prompt itself never changes.
    /// </summary>
    [TestMethod]
    public void RetentionAndClear_RemoveDroppedRows()
    {
        var transcript = new Transcript { MaxLines = 2 };
        var cache = new TranscriptViewCache();
        var feedback = new YankFeedback();
        transcript.Add(LineKind.Output, "first");
        transcript.Add(LineKind.Output, "second");
        var first = cache.Get(transcript, 80, feedback);
        transcript.Add(LineKind.Output, "third");
        var retained = cache.Get(transcript, 80, feedback);
        Assert.HasCount(2, retained);
        Assert.AreSame(first[1], retained[0]);
        Assert.AreEqual("third", ((TranscriptLineWidget)retained[1]).Line.PlainText);
        transcript.Clear();
        Assert.IsEmpty(cache.Get(transcript, 80, feedback));
    }
}
