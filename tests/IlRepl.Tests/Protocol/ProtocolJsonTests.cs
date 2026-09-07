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

    /// <summary>
    /// The open method and the method count survive the wire, and a fresh status omits the null.
    /// </summary>
    [TestMethod]
    public void SessionStatus_RoundTripsMethodFields()
    {
        var status = SessionStatus.Initial with { OpenMethod = "Fib", Methods = 2 };
        var json = JsonSerializer.Serialize(status, ProtocolJsonContext.Default.SessionStatus);
        Assert.Contains("\"openMethod\":\"Fib\"", json);
        Assert.Contains("\"methods\":2", json);
        Assert.AreEqual(status, JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.SessionStatus));

        var initial = JsonSerializer.Serialize(SessionStatus.Initial, ProtocolJsonContext.Default.SessionStatus);
        Assert.DoesNotContain("openMethod", initial);
        var back = JsonSerializer.Deserialize(initial, ProtocolJsonContext.Default.SessionStatus);
        Assert.IsNotNull(back);
        Assert.IsNull(back.OpenMethod);
        Assert.AreEqual(0, back.Methods);
    }

    /// <summary>
    /// The type fields round-trip, and a fresh status omits the open type.
    /// </summary>
    [TestMethod]
    public void SessionStatus_RoundTripsTypeFields()
    {
        var status = SessionStatus.Initial with { OpenType = "Outer/Inner", Types = 3 };
        var json = JsonSerializer.Serialize(status, ProtocolJsonContext.Default.SessionStatus);
        Assert.Contains("\"openType\":\"Outer/Inner\"", json);
        Assert.Contains("\"types\":3", json);
        var back = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.SessionStatus)!;
        Assert.AreEqual("Outer/Inner", back.OpenType);
        Assert.AreEqual(3, back.Types);
        var initial = JsonSerializer.Serialize(SessionStatus.Initial, ProtocolJsonContext.Default.SessionStatus);
        Assert.DoesNotContain("openType", initial);
        Assert.AreEqual(0, JsonSerializer.Deserialize(initial, ProtocolJsonContext.Default.SessionStatus)!.Types);
    }
}
