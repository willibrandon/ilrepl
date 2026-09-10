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

    /// <summary>
    /// The revision travels with the status and tells two otherwise equal statuses apart.
    /// </summary>
    [TestMethod]
    public void SessionStatus_RoundTripsRevision()
    {
        var status = SessionStatus.Initial with { Revision = 42 };
        var json = JsonSerializer.Serialize(status, ProtocolJsonContext.Default.SessionStatus);
        Assert.Contains("\"revision\":42", json);
        var back = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.SessionStatus);
        Assert.AreEqual(status, back);
        Assert.AreNotEqual(SessionStatus.Initial, back);
        Assert.AreEqual(0, SessionStatus.Initial.Revision);
    }

    /// <summary>
    /// A mark round-trips, and a null count is left out of the JSON.
    /// </summary>
    [TestMethod]
    public void SessionMark_RoundTrips()
    {
        var mark = new SessionMark(7, 3, 1, 0, 4, true, EchoStack: false, ShowTiming: true, BraceSeen: false);
        var json = JsonSerializer.Serialize(mark, ProtocolJsonContext.Default.SessionMark);
        Assert.Contains("\"generation\":7", json);
        Assert.Contains("\"openTypeLines\":4", json);
        Assert.Contains("\"openMethodLines\":0", json);
        Assert.Contains("\"braceSeen\":false", json);
        Assert.Contains("\"echoStack\":false", json);
        Assert.Contains("\"showTiming\":true", json);
        Assert.AreEqual(mark, JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.SessionMark));

        var status = SessionStatus.Initial with { Mark = mark, OpenDepth = 2 };
        var statusJson = JsonSerializer.Serialize(status, ProtocolJsonContext.Default.SessionStatus);
        Assert.Contains("\"openDepth\":2", statusJson);
        Assert.AreEqual(status, JsonSerializer.Deserialize(statusJson, ProtocolJsonContext.Default.SessionStatus));
    }

    /// <summary>
    /// The styles the tokenizer added travel by name, and the vocabulary round-trips with its enum values as strings.
    /// </summary>
    [TestMethod]
    public void Vocabulary_AndNewStyles_RoundTrip()
    {
        var line = new TranscriptLine(LineKind.Input, [new TranscriptSpan("Max", SpanStyle.Member), new TranscriptSpan("(", SpanStyle.Punctuation), new TranscriptSpan("// c", SpanStyle.Comment), new TranscriptSpan(".locals", SpanStyle.Directive)]);
        var json = JsonSerializer.Serialize(line, ProtocolJsonContext.Default.TranscriptLine);
        Assert.Contains("\"style\":\"Member\"", json);
        Assert.Contains("\"style\":\"Punctuation\"", json);
        var backLine = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.TranscriptLine);
        Assert.IsNotNull(backLine);
        Assert.AreSequenceEqual(line.Spans, backLine.Spans);

        var vocabulary = new CilVocabulary(new Dictionary<string, CilOperandKind> { ["ldc.i4"] = CilOperandKind.Integer, ["no."] = CilOperandKind.Integer }, [".locals"], [".show", ".?"], ["instance"], ["int32"]);
        var vocabularyJson = JsonSerializer.Serialize(vocabulary, ProtocolJsonContext.Default.CilVocabulary);
        Assert.Contains("\"ldc.i4\":\"Integer\"", vocabularyJson);
        var back = JsonSerializer.Deserialize(vocabularyJson, ProtocolJsonContext.Default.CilVocabulary);
        Assert.IsNotNull(back);
        Assert.AreEqual(CilOperandKind.Integer, back.Opcodes["no."]);
        Assert.AreSequenceEqual(vocabulary.Commands, back.Commands);
    }
}
