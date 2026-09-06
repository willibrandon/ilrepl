using System.Text.Json;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Tests for the source-generated JSON contract.
/// </summary>
[TestClass]
public sealed class ProtocolJsonTests
{
    /// <summary>
    /// A reply round-trips with camel-cased names and enum names as strings.
    /// </summary>
    [TestMethod]
    public void HandleReply_RoundTrips()
    {
        var reply = new HandleReply(true, false,
            [new TranscriptLine(LineKind.Result, [new TranscriptSpan("= 1", SpanStyle.Number)])],
            SessionStatus.Initial);
        var json = JsonSerializer.Serialize(reply, ProtocolJsonContext.Default.HandleReply);
        Assert.Contains("\"kind\":\"Result\"", json);
        Assert.Contains("\"style\":\"Number\"", json);

        var back = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.HandleReply);
        Assert.IsNotNull(back);
        Assert.AreEqual(reply.Lines[0].PlainText, back.Lines[0].PlainText);
        Assert.AreEqual(SpanStyle.Number, back.Lines[0].Spans[0].Style);
        Assert.AreEqual(reply.Status, back.Status);
    }

    /// <summary>
    /// The transcript keeps only the last MaxLines lines.
    /// </summary>
    [TestMethod]
    public void Transcript_TrimsToMaxLines()
    {
        var transcript = new Transcript { MaxLines = 3 };
        for (var i = 0; i < 5; i++)
        {
            transcript.Add(LineKind.Info, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.HasCount(3, transcript.Lines);
        Assert.AreEqual("2", transcript.Lines[0].PlainText);
        Assert.AreEqual(5, transcript.TotalAdded);
    }
}
