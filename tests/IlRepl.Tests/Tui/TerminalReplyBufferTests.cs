using System.Text;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using Hex1b.Tokens;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Exercises terminal reply framing across native read partitions without replacing the real byte parser.
/// </summary>
[TestClass]
public sealed class TerminalReplyBufferTests
{
    /// <summary>
    /// Supplies cancellation to the real terminal and editor ordering regression.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Supplies every split of the queried OSC and APC reply forms, including their introducers and terminators.
    /// </summary>
    public static IEnumerable<(string Reply, int Split)> ReplyPartitions()
    {
        string[] replies = ["\x1b]11;rgb:1111/2222/3333\x1b\\", "\x1b]11;rgb:a/b/c\a", "\x1b_Gi=991122;OK\x1b\\"];
        foreach (var reply in replies)
            for (var split = 1; split < reply.Length; split++) yield return (reply, split);
    }

    /// <summary>
    /// A split reply cannot reach the parser as printable payload, even when the keyboard ambiguity deadline expires.
    /// </summary>
    /// <param name="reply">The actual terminal reply bytes.</param>
    /// <param name="split">The native read boundary within that reply.</param>
    [TestMethod]
    [DynamicData(nameof(ReplyPartitions))]
    public void Reply_EveryPartitionWaitsForItsTerminator(string reply, int split)
    {
        var buffer = new TerminalReplyBuffer();
        var bytes = Encoding.UTF8.GetBytes(reply);
        Assert.IsTrue(buffer.Append(bytes.AsMemory(0, split)).IsEmpty, "An incomplete reply must not enter the keyboard parser.");
        if (split > 1)
        {
            Assert.IsFalse(buffer.NeedsEscapeTimeout, "A recognized control string is not an ambiguous Escape key.");
            Assert.IsTrue(buffer.FlushEscape().IsEmpty, "The Escape deadline must never release a reply prefix.");
        }
        var completed = buffer.Append(bytes.AsMemory(split));
        Assert.AreSequenceEqual(bytes, completed.ToArray());
        var parsed = AnsiTokenizer.Tokenize(Encoding.UTF8.GetString(completed.Span));
        Assert.HasCount(1, parsed);
        Assert.IsTrue(parsed[0] is OscToken or KgpToken);
        Assert.IsFalse(buffer.NeedsEscapeTimeout);
        Assert.AreEqual("next λ日本", Encoding.UTF8.GetString(buffer.Append("next λ日本"u8.ToArray()).Span));
    }

    /// <summary>
    /// Ordinary Unicode bytes, Alt chords, arrow keys, and modified arrows retain their exact order across every boundary.
    /// </summary>
    /// <param name="text">The ordinary input bytes whose meaning the terminal parser owns.</param>
    [TestMethod]
    [DataRow("typing λ日本")]
    [DataRow("\x1bx")]
    [DataRow("\x1b[A")]
    [DataRow("\x1b[1;5D")]
    public void OrdinaryInput_PassesEveryPartitionUnchanged(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var buffer = new TerminalReplyBuffer();
            var first = buffer.Append(bytes.AsMemory(0, split)).ToArray();
            var second = buffer.Append(bytes.AsMemory(split)).ToArray();
            Assert.AreSequenceEqual(bytes, first.Concat(second).ToArray(), "Split " + split);
            Assert.IsFalse(buffer.NeedsEscapeTimeout);
        }
    }

    /// <summary>
    /// A bracketed paste preserves literal reply-looking bytes even when every marker and UTF-8 character is split.
    /// </summary>
    [TestMethod]
    public void BracketedPaste_PreservesLiteralControlStrings()
    {
        var buffer = new TerminalReplyBuffer();
        var bytes = Encoding.UTF8.GetBytes("\x1b[200~// λ日本\x1b]11;rgb:1/2/3\a\x1b_Gi=1;OK\x1b\\\x1b[201~");
        var output = new List<byte>();
        foreach (var value in bytes) output.AddRange(buffer.Append(new byte[] { value }).ToArray());
        Assert.AreSequenceEqual(bytes, output);
        Assert.IsTrue(buffer.Append("\x1b]11;rgb:1/2/3"u8.ToArray()).IsEmpty,
            "The closing paste marker must restore reply framing.");
        Assert.IsFalse(buffer.NeedsEscapeTimeout);
    }

    /// <summary>
    /// Multiple replies surrounding ordinary text remain complete parser tokens without swallowing the adjacent input.
    /// </summary>
    [TestMethod]
    public void RepliesAndText_KeepTheirOriginalOrder()
    {
        var buffer = new TerminalReplyBuffer();
        const string text = "before λ\x1b]11;rgb:a/b/c\a between \x1b_Gi=1;OK\x1b\\after 日本";
        var result = buffer.Append(Encoding.UTF8.GetBytes(text));
        Assert.AreEqual(text, Encoding.UTF8.GetString(result.Span));
        var tokens = AnsiTokenizer.Tokenize(Encoding.UTF8.GetString(result.Span));
        Assert.AreEqual("before λ between after 日本", string.Concat(tokens.OfType<TextToken>().Select(token => token.Text)));
        Assert.HasCount(1, tokens.OfType<OscToken>());
        Assert.HasCount(1, tokens.OfType<KgpToken>());
    }

    /// <summary>
    /// Oversized protocol responses are discarded through their terminator without exposing payload or consuming later input.
    /// </summary>
    [TestMethod]
    public void OversizedReply_DropsPayloadAndResumesAtTerminator()
    {
        var buffer = new TerminalReplyBuffer();
        Assert.IsTrue(buffer.Append("\x1b_Gi=1;"u8.ToArray()).IsEmpty);
        var chunk = Encoding.ASCII.GetBytes(new string('x', 1024));
        for (var index = 0; index < 100; index++) Assert.IsTrue(buffer.Append(chunk).IsEmpty);
        Assert.IsTrue(buffer.FlushEscape().IsEmpty);
        Assert.AreEqual("after λ", Encoding.UTF8.GetString(buffer.Append("\x1b\\after λ"u8.ToArray()).Span));
        Assert.AreEqual("\x1b]11;rgb:a/b/c\a", Encoding.UTF8.GetString(buffer.Append("\x1b]11;rgb:a/b/c\a"u8.ToArray()).Span));
    }

    /// <summary>
    /// The complete reply size includes its introducer and terminator for both supported control string forms.
    /// </summary>
    /// <param name="osc">Whether to use an OSC reply ending with BEL instead of an APC reply ending with ST.</param>
    /// <param name="length">The total reply byte count around the exact retention boundary.</param>
    [TestMethod]
    [DataRow(true, 65_535)]
    [DataRow(true, 65_536)]
    [DataRow(true, 65_537)]
    [DataRow(false, 65_535)]
    [DataRow(false, 65_536)]
    [DataRow(false, 65_537)]
    public void ReplySize_IncludesIntroducerAndTerminator(bool osc, int length)
    {
        var buffer = new TerminalReplyBuffer();
        var prefix = osc ? "\x1b]11;" : "\x1b_Gi=1;";
        var suffix = osc ? "\a" : "\x1b\\";
        var bytes = Encoding.ASCII.GetBytes(prefix + new string('x', length - prefix.Length - suffix.Length) + suffix);
        Assert.IsTrue(buffer.Append(bytes.AsMemory(0, bytes.Length - suffix.Length)).IsEmpty);
        var completed = buffer.Append(bytes.AsMemory(bytes.Length - suffix.Length));
        if (length <= 65_536) Assert.AreSequenceEqual(bytes, completed.ToArray());
        else Assert.IsTrue(completed.IsEmpty, "An oversized reply must be discarded through its terminator.");
        Assert.AreEqual("next λ", Encoding.UTF8.GetString(buffer.Append("next λ"u8.ToArray()).Span));
        Assert.IsFalse(buffer.NeedsEscapeTimeout);
    }

    /// <summary>
    /// Ending raw mode abandons an unfinished reply so a subsequent entry begins with normal input.
    /// </summary>
    [TestMethod]
    public void Reset_DoesNotCarryAReplyIntoTheNextEntry()
    {
        var buffer = new TerminalReplyBuffer();
        Assert.IsTrue(buffer.Append("\x1b]11;rgb:"u8.ToArray()).IsEmpty);
        buffer.Reset();
        Assert.AreEqual("fresh λ", Encoding.UTF8.GetString(buffer.Append("fresh λ"u8.ToArray()).Span));
        Assert.IsFalse(buffer.NeedsEscapeTimeout);
    }

    /// <summary>
    /// Consecutive Escapes produce independent key segments between ordinary input without publishing a bare escape prefix.
    /// </summary>
    [TestMethod]
    public void ConsecutiveEscapes_StaySeparateFromAdjacentInput()
    {
        var buffer = new TerminalReplyBuffer();
        Assert.AreEqual("before λ", Encoding.UTF8.GetString(buffer.Append("before λ\x1b\x1b\x1b"u8.ToArray()).Span));
        for (var index = 0; index < 2; index++)
        {
            var tokens = AnsiTokenizer.Tokenize(Encoding.UTF8.GetString(buffer.ReadBuffered().Span));
            Assert.HasCount(1, tokens);
            Assert.IsTrue(tokens[0] is OscToken { Command: "7777", Payload: "ilrepl-escape" });
        }
        Assert.IsTrue(buffer.ReadBuffered().IsEmpty);
        Assert.IsTrue(buffer.NeedsEscapeTimeout);
        var final = AnsiTokenizer.Tokenize(Encoding.UTF8.GetString(buffer.FlushEscape().Span));
        Assert.HasCount(1, final);
        Assert.IsTrue(final[0] is OscToken { Command: "7777", Payload: "ilrepl-escape" });
        Assert.IsFalse(buffer.NeedsEscapeTimeout);
        Assert.IsTrue(buffer.ReadBuffered().IsEmpty);
        Assert.AreEqual("after 日本", Encoding.UTF8.GetString(buffer.Append("after 日本"u8.ToArray()).Span));
        Assert.IsTrue(buffer.ReadBuffered().IsEmpty);
        Assert.AreEqual("before", Encoding.UTF8.GetString(buffer.Append("before\x1b\x1b"u8.ToArray()).Span));
        buffer.Reset();
        Assert.IsTrue(buffer.ReadBuffered().IsEmpty, "A new raw-mode entry must not inherit queued Escape keys.");
        Assert.IsTrue(buffer.FlushEscape().IsEmpty);
    }

    /// <summary>
    /// A standalone Escape marker closes help in order and later Unicode reaches the editor without any protocol text.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Escape_StaysOrderedWithEarlierAndLaterInput()
    {
        var token = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        PromptState? prompt = null;
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, new Transcript(),
            onPrompt: value => prompt = value).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("// retained λ", ct: token);
        await auto.WaitUntilTextAsync("// retained λ");
        var buffer = new TerminalReplyBuffer();
        // F1 and the incomplete Escape begin in one native read. The framer releases F1 first.
        await adapter.SendAsync(buffer.Append("\x1bOP\x1b"u8.ToArray()).ToArray());
        await auto.WaitUntilAsync(snapshot => snapshot.GetLine(0).Trim().Equals("help", StringComparison.Ordinal));
        Assert.IsTrue(buffer.NeedsEscapeTimeout);
        var escape = buffer.FlushEscape();
        Assert.IsFalse(escape.IsEmpty);
        Assert.IsFalse(buffer.NeedsEscapeTimeout);
        Assert.IsTrue(buffer.FlushEscape().IsEmpty, "One Escape must produce exactly one key event.");
        await adapter.SendAsync(escape.ToArray());
        await adapter.SendAsync(buffer.Append(" after 日本"u8.ToArray()).ToArray());
        await auto.WaitUntilAsync(snapshot => !snapshot.GetLine(0).Trim().Equals("help", StringComparison.Ordinal)
            && prompt!.Text == "// retained λ after 日本");
        Assert.IsTrue(engine.Status.CellIsEmpty);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await run.WaitAsync(AppTest.Timeout, token);
    }

    /// <summary>
    /// Two consecutive Escapes close real help and then dismiss the real editor palette before later Unicode input.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ConsecutiveEscapes_CloseHelpThenDismissPalette()
    {
        var token = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        PromptState? prompt = null;
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, new Transcript(),
            onPrompt: value => prompt = value).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("// retained λ", ct: token);
        await auto.WaitUntilTextAsync("// retained λ");
        await auto.KeyAsync(Hex1bKey.F1, ct: token);
        await auto.WaitUntilAsync(snapshot => snapshot.GetLine(0).Trim().Equals("help", StringComparison.Ordinal));
        Assert.IsFalse(prompt!.PaletteDismissed);
        var buffer = new TerminalReplyBuffer();
        await adapter.SendAsync(buffer.Append("\x1b\x1b"u8.ToArray()).ToArray());
        Assert.IsTrue(buffer.ReadBuffered().IsEmpty);
        Assert.IsTrue(buffer.NeedsEscapeTimeout);
        await auto.WaitUntilAsync(snapshot => prompt.Help is null && snapshot.ContainsText("// retained λ"));
        Assert.IsFalse(prompt.PaletteDismissed, "The first Escape must only close help.");
        await adapter.SendAsync(buffer.FlushEscape().ToArray());
        await auto.WaitUntilAsync(_ => prompt.PaletteDismissed);
        Assert.AreEqual("// retained λ", prompt.Text, "Neither key may expose the private marker as input.");
        await adapter.SendAsync(buffer.Append(" after 日本"u8.ToArray()).ToArray());
        await auto.WaitUntilTextAsync("// retained λ after 日本");
        Assert.AreEqual("// retained λ after 日本", prompt.Text);
        Assert.IsTrue(engine.Status.CellIsEmpty);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await run.WaitAsync(AppTest.Timeout, token);
    }

    /// <summary>
    /// A marker embedded in an external mixed batch cannot inject Escape ahead of that batch's ordinary text.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task EscapeMarker_RejectsMixedTokenBatches()
    {
        var pressed = 0;
        var filter = new TerminalSizeFilter();
        filter.EscapePressed += () => pressed++;
        var marker = AnsiTokenizer.Tokenize("\x1b]7777;ilrepl-escape\a")[0];
        await filter.OnInputAsync([new TextToken("before"), marker], TimeSpan.Zero, TestContext.CancellationToken);
        Assert.AreEqual(0, pressed);
        await filter.OnInputAsync([marker, new TextToken("after")], TimeSpan.Zero, TestContext.CancellationToken);
        Assert.AreEqual(0, pressed);
        await filter.OnInputAsync([marker], TimeSpan.Zero, TestContext.CancellationToken);
        Assert.AreEqual(1, pressed);
    }
}
