using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;
using System.Text.RegularExpressions;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Tests for <see cref="CilTokenizer"/>: the caret fixtures pin every style, the property tests
/// pin the shape of the tokens over every line the repository holds, and the listing tests pin
/// that the transcript colours nothing by hand.
/// </summary>
[TestClass]
public sealed partial class CilTokenizerTests
{
    private static readonly CilTokenizer Tokenizer = new(CilVocabularyBuilder.Vocabulary);

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Protocol", "Fixtures", "highlight");

    /// <summary>
    /// The fixture files, one row each.
    /// </summary>
    public static IEnumerable<object[]> Fixtures => Directory.GetFiles(FixtureDirectory, "*.il").Order(StringComparer.Ordinal).Select(f => new object[] { Path.GetFileName(f) });

    /// <summary>
    /// Every caret in a fixture finds a token that starts there, covers the run, and has the style.
    /// </summary>
    /// <param name="file">The fixture file.</param>
    [TestMethod]
    [DynamicData(nameof(Fixtures))]
    public void Fixtures_EveryCaretMatches(string file)
    {
        var path = Path.Combine(FixtureDirectory, file);
        var cases = HighlightFixture.Parse(path);
        Assert.IsNotEmpty(cases, file);
        var inComment = false;
        foreach (var c in cases)
        {
            var tokens = Tokenizer.Tokenize(c.Subject, ref inComment);
            if (c.Subject.Trim().Length == 0)
            {
                Assert.IsEmpty(tokens, $"{file}:{c.LineNumber}: a blank line has no tokens");
                continue;
            }

            foreach (var e in c.Expectations)
            {
                var token = tokens.FirstOrDefault(t => t.Start == e.Column && (e.Length == 0 || t.Length == e.Length));
                var found = tokens.Any(t => t.Start == e.Column && (e.Length == 0 || t.Length == e.Length));
                var actual = string.Join(" ", tokens.Select(t => $"{t.Style}@{t.Start}+{t.Length}'{c.Subject.Substring(t.Start, t.Length)}'"));
                Assert.IsTrue(found, $"{file}:{e.LineNumber}: no token at column {e.Column} covering {e.Length} in '{c.Subject}'; tokens: {actual}");
                Assert.AreEqual(e.Style, token.Style, $"{file}:{e.LineNumber}: '{c.Subject.Substring(e.Column, e.Length == 0 ? token.Length : e.Length)}' in '{c.Subject}'; tokens: {actual}");
            }
        }

        Assert.IsFalse(inComment, $"{file}: a block comment is still open at the end of the file");
    }

    /// <summary>
    /// Tokens are ordered, disjoint, and inside the line; every character that is not whitespace
    /// is inside exactly one; whitespace is inside a token only for a string, a comment, or a
    /// quoted name.
    /// </summary>
    [TestMethod]
    public void Tokens_AreOrderedDisjointAndCoverEveryNonSpaceCharacter()
    {
        foreach (var (source, line, tokens) in Corpus())
        {
            var next = 0;
            foreach (var token in tokens)
            {
                Assert.IsGreaterThanOrEqualTo(next, token.Start, $"{source}: token at {token.Start} overlaps or is out of order in '{line}'");
                Assert.IsTrue(token.Length > 0 && token.End <= line.Length, $"{source}: token at {token.Start} is empty or outside '{line}'");
                var text = line.Substring(token.Start, token.Length);
                if (token.Style is not (SpanStyle.String or SpanStyle.Comment) && !text.StartsWith('\'') && !text.StartsWith('"'))
                {
                    Assert.DoesNotContain(' ', text, $"{source}: a {token.Style} token holds whitespace in '{line}'");
                }

                for (var i = next; i < token.Start; i++)
                {
                    Assert.IsTrue(char.IsWhiteSpace(line[i]), $"{source}: '{line[i]}' at {i} is outside every token in '{line}'");
                }

                next = token.End;
            }

            for (var i = next; i < line.Length; i++)
            {
                Assert.IsTrue(char.IsWhiteSpace(line[i]), $"{source}: '{line[i]}' at {i} is outside every token in '{line}'");
            }
        }
    }

    /// <summary>
    /// Spans concatenate to the line, and no two neighbours share a style.
    /// </summary>
    [TestMethod]
    public void ToSpans_ConcatenatesToTheLine()
    {
        foreach (var (source, line, tokens) in Corpus())
        {
            var spans = CilTokenizer.ToSpans(line, tokens, SpanStyle.Input);
            Assert.AreEqual(line, string.Concat(spans.Select(s => s.Text)), source);
            for (var i = 1; i < spans.Count; i++)
            {
                Assert.AreNotEqual(spans[i - 1].Style, spans[i].Style, $"{source}: neighbouring spans share a style in '{line}'");
            }

            Assert.IsTrue(spans.All(s => s.Text.Length > 0), source);
        }
    }

    /// <summary>
    /// The same line gives the same tokens every time, with or without the state overload.
    /// </summary>
    [TestMethod]
    public void Tokenize_IsPureAndRepeatable()
    {
        var lines = new[] { "call int32 [System.Runtime]System.Math::Max(int32, int32)", ".method public static int32 Fib(int32 n) cil managed {", "ldstr \"a\" // b" };
        foreach (var line in lines)
        {
            var first = Tokenizer.Tokenize(line);
            var second = Tokenizer.Tokenize(line);
            var closed = false;
            var third = Tokenizer.Tokenize(line, ref closed);
            Assert.AreSequenceEqual(first, second, line);
            Assert.AreSequenceEqual(first, third, line);
            Assert.IsFalse(closed);
        }
    }

    /// <summary>
    /// Every command the tokenizer colours is one the core accepts, and the other way round.
    /// </summary>
    [TestMethod]
    public void Commands_MatchReplCore()
    {
        foreach (var command in Tokenizer.Vocabulary.Commands)
        {
            Assert.AreEqual(SpanStyle.Command, Tokenizer.Tokenize(command)[0].Style, command);
            var core = new ReplCore();
            core.Handle(command);
            Assert.DoesNotContain("unknown command", string.Join("\n", core.Transcript.Lines.Select(l => l.PlainText)), command);
        }

        foreach (var item in Completer.Commands)
        {
            // The palette lists directives beside commands; the tokenizer, like the core, routes those as directives.
            Assert.IsTrue(Tokenizer.Vocabulary.Commands.Contains(item.Name) || ReplCore.Directives.Contains(item.Name), item.Name);
        }

        Assert.AreEqual(SpanStyle.Error, Tokenizer.Tokenize(".nosuchcommand")[0].Style);
    }

    /// <summary>
    /// Every directive the core takes tokenizes as one, and every opcode as an opcode.
    /// </summary>
    [TestMethod]
    public void Directives_AndOpcodes_MatchTheEngine()
    {
        foreach (var directive in ReplCore.Directives)
        {
            Assert.AreEqual(SpanStyle.Directive, Tokenizer.Tokenize(directive)[0].Style, directive);
        }

        foreach (var name in OpcodeTable.Names.Where(n => !OpcodeTable.IsReserved(n)).Append("no."))
        {
            Assert.AreEqual(SpanStyle.Opcode, Tokenizer.Tokenize(name)[0].Style, name);
        }

        Assert.AreEqual(SpanStyle.Error, Tokenizer.Tokenize("prefix1")[0].Style);
    }

    /// <summary>
    /// An open block comment carries from line to line and closes where it closes.
    /// </summary>
    [TestMethod]
    public void BlockComment_StateCarriesAcrossLines()
    {
        var open = false;
        var first = Tokenizer.Tokenize("add /* open", ref open);
        Assert.IsTrue(open);
        Assert.AreEqual(SpanStyle.Opcode, first[0].Style);
        Assert.AreEqual(SpanStyle.Comment, first[1].Style);
        var second = Tokenizer.Tokenize("still */ sub", ref open);
        Assert.IsFalse(open);
        Assert.AreEqual(SpanStyle.Comment, second[0].Style);
        Assert.AreEqual(SpanStyle.Opcode, second[1].Style);
        Assert.AreEqual("sub", "still */ sub".Substring(second[1].Start, second[1].Length));
    }

    /// <summary>
    /// The echo, <c>.show</c>, <c>.dis</c>, and <c>.il</c> colour their IL with the tokenizer and
    /// nothing else: with the prompt, offsets, padding, and the stack column taken away, every
    /// line's spans are what the tokenizer gives for its text.
    /// </summary>
    [TestMethod]
    public void Listings_UseTheTokenizer()
    {
        var core = new ReplCore();
        foreach (var line in new[]
        {
            ".locals init (int32 i, string s)",
            "ldc.i4 6", "stloc i", "ldstr \"x\" // note", "stloc s",
            ".try {", "ldloc i", "ldc.i4 0", "div", "pop", "leave DONE",
            "} catch [System.Runtime]System.Exception {", "pop", "leave DONE", "}",
            "DONE: ldloc i",
            ".method int32 Fib(int32 n) {", "ldarg n", "ldc.i4 2", "blt BASE", "ldarg n", "ldc.i4 1", "sub", "call int32 Fib(int32)",
            "ldarg n", "ldc.i4 2", "sub", "call int32 Fib(int32)", "add", "ret", "BASE: ldarg n", "ret", "}",
            ".class public Point extends [System.Runtime]System.ValueType {", ".field public int32 X",
            ".method public instance int32 Twice() {", "ldarg.0", "ldfld int32 Point::X", "ldc.i4 2", "mul", "ret", "}", ".show", "}",
            ".show", ".dis Fib", ".dis instance int32 Point::Twice()", ".il",
        })
        {
            Assert.IsTrue(core.Handle(line).Succeeded, line + "\n" + string.Join("\n", core.Transcript.Lines.TakeLast(3).Select(l => l.PlainText)));
        }

        var checkedLines = 0;
        var inComment = false;
        foreach (var line in core.Transcript.Lines)
        {
            IReadOnlyList<TranscriptSpan> spans;
            IReadOnlyList<TranscriptSpan> expected;
            if (line.Kind == LineKind.Input)
            {
                Assert.AreEqual(SpanStyle.Prompt, line.Spans[0].Style);
                spans = line.Spans.Skip(1).ToList();
                var text = string.Concat(spans.Select(s => s.Text));
                expected = Tokenizer.Spans(text, ref inComment, SpanStyle.Input);
            }
            else if (line.Kind == LineKind.Listing)
            {
                var structural = new List<TranscriptSpan>(line.Spans);
                if (structural.Count > 1 && structural[0].Style == SpanStyle.Dim && OffsetColumn().IsMatch(structural[0].Text))
                {
                    structural.RemoveAt(0);
                    if (structural[^1].Style == SpanStyle.Dim && structural[^1].Text.StartsWith(' '))
                    {
                        structural.RemoveAt(structural.Count - 1);
                        if (structural.Count > 0 && structural[^1].Style == SpanStyle.Default && structural[^1].Text.Trim().Length == 0)
                        {
                            structural.RemoveAt(structural.Count - 1);
                        }
                    }
                }

                spans = structural;
                var text = string.Concat(spans.Select(s => s.Text));
                expected = Tokenizer.Spans(text);
            }
            else
            {
                continue;
            }

            Assert.AreSequenceEqual(expected, spans, line.PlainText);
            checkedLines++;
        }

        Assert.IsGreaterThan(80, checkedLines);
        Assert.DoesNotContain(l => l.Kind == LineKind.Listing && l.Spans.Any(s => s.Style == SpanStyle.Error), core.Transcript.Lines, "nothing the REPL prints is an error token");
    }

    private static IEnumerable<(string Source, string Line, IReadOnlyList<CilToken> Tokens)> Corpus()
    {
        foreach (var file in Directory.GetFiles(FixtureDirectory, "*.il").Order(StringComparer.Ordinal))
        {
            var inComment = false;
            foreach (var c in HighlightFixture.Parse(file))
            {
                yield return (Path.GetFileName(file) + ":" + c.LineNumber, c.Subject, Tokenizer.Tokenize(c.Subject, ref inComment));
            }
        }

        foreach (var help in HelpText.Lines())
        {
            yield return ("help", help.PlainText, Tokenizer.Tokenize(help.PlainText));
        }

        foreach (var file in Directory.GetFiles(RepoPaths.Transcripts, "*.il").Order(StringComparer.Ordinal))
        {
            var inComment = false;
            var number = 0;
            foreach (var line in File.ReadAllLines(file))
            {
                number++;
                yield return (Path.GetFileName(file) + ":" + number, line, Tokenizer.Tokenize(line, ref inComment));
            }
        }
    }

    // The offset column of a listing row, or the blank column of a block row.
    [GeneratedRegex("^  ([0-9a-f]{4} |[0-9]{3}  )$|^       $")]
    private static partial Regex OffsetColumn();
}
