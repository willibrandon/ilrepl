#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:project ../src/IlRepl.Engine/IlRepl.Engine.csproj
#:project ../src/IlRepl.Tui/IlRepl.Tui.csproj
#:property PublishAot=false

// Colours the CIL in the docs with the terminal's own tokenizer and palette. A fenced block
// tagged cil is tokenized line by line. A block tagged ilrepl is a transcript: its input lines
// are replayed through the engine, one session per page since a page's transcripts continue
// one another, and when the replay reads exactly as the block does, every line carries the
// spans the terminal drew. Otherwise the input lines are tokenized and the output lines are
// styled by the rules the engine styles its own by, and the block is named so the drift can be
// seen. A block showing the editor's own rows is styled that way without a replay. The result
// goes to docs/src/generated/cil-tokens.json, with the palette for a dark ground and the one for
// a light ground, which the site reads at build. The engine needs the JIT, which a file-based
// app's default of publishing native would take away.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hex1b.Theming;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

var root = FindRoot();
var docs = Path.Combine(root, "docs", "src", "content", "docs");
var output = Path.Combine(root, "docs", "src", "generated", "cil-tokens.json");
var tokenizer = new CilTokenizer(CilVocabularyBuilder.Vocabulary);
var blocks = new SortedDictionary<string, (string Where, string Language, List<IReadOnlyList<TranscriptSpan>> Lines)>(StringComparer.Ordinal);
var warnings = new List<string>();
var replayed = 0;
var cwd = Directory.GetCurrentDirectory();

foreach (var file in Directory.EnumerateFiles(docs, "*.md*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
{
    var lines = File.ReadAllLines(file);
    var relative = Path.GetRelativePath(docs, file).Replace('\\', '/');

    // One session per page, in a scratch directory where a .save writes nowhere that matters
    // and a .load of a sample finds it as it would from the repository root.
    var scratch = Directory.CreateTempSubdirectory("ilrepl-docs-");
    Directory.CreateSymbolicLink(Path.Combine(scratch.FullName, "samples"), Path.Combine(root, "samples"));
    Directory.SetCurrentDirectory(scratch.FullName);
    await using var engine = new InProcessEngine();
    try
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var language = lines[i] switch { "```cil" => "cil", "```ilrepl" => "ilrepl", _ => null };
            if (language is null)
            {
                continue;
            }

            var start = i + 1;
            var end = start;
            while (end < lines.Length && lines[end] != "```")
            {
                end++;
            }

            var body = lines[start..end].ToList();
            while (body.Count > 0 && body[^1].Trim().Length == 0)
            {
                body.RemoveAt(body.Count - 1);
            }

            var where = $"{relative}:{start + 1}";
            var spans = language == "cil" ? Cil(body) : await TranscriptAsync(engine, body, where);
            blocks[Key(body)] = (where, language, spans);
            i = end;
        }
    }
    finally
    {
        Directory.SetCurrentDirectory(cwd);
        scratch.Delete(true);
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using (var stream = File.Create(output))
using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
{
    json.WriteStartObject();
    json.WriteStartObject("palette");
    foreach (var (ground, colour) in new (string, Func<SpanStyle, Hex1bColor>)[] { ("dark", SpanPalette.Color), ("light", SpanPalette.LightColor) })
    {
        json.WriteStartObject(ground);
        foreach (var style in Enum.GetValues<SpanStyle>())
        {
            var color = colour(style);
            if (!color.IsDefault)
            {
                json.WriteString(style.ToString(), $"#{color.R:x2}{color.G:x2}{color.B:x2}");
            }
        }

        json.WriteEndObject();
    }

    json.WriteEndObject();
    json.WriteStartObject("blocks");
    foreach (var (key, block) in blocks)
    {
        json.WriteStartObject(key);
        json.WriteString("where", block.Where);
        json.WriteString("language", block.Language);
        json.WriteStartArray("lines");
        foreach (var line in block.Lines)
        {
            json.WriteStartArray();
            var offset = 0;
            foreach (var span in line)
            {
                if (span.Style != SpanStyle.Default && span.Text.Length > 0)
                {
                    json.WriteStartArray();
                    json.WriteNumberValue(offset);
                    json.WriteNumberValue(span.Text.Length);
                    json.WriteStringValue(span.Style.ToString());
                    json.WriteEndArray();
                }

                offset += span.Text.Length;
            }

            json.WriteEndArray();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    json.WriteEndObject();
    json.WriteEndObject();
}

Console.WriteLine($"{blocks.Count} blocks coloured ({replayed} transcripts replayed exactly) into {Path.GetRelativePath(root, output)}");
foreach (var warning in warnings)
{
    Console.WriteLine("  " + warning);
}

return 0;

// The block's text as the site sees it: its lines, trailing blank lines dropped, joined by newlines.
static string Key(IReadOnlyList<string> body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', body))));

List<IReadOnlyList<TranscriptSpan>> Cil(IReadOnlyList<string> body)
{
    var comment = false;
    var result = new List<IReadOnlyList<TranscriptSpan>>();
    foreach (var line in body)
    {
        result.Add(tokenizer.Spans(line, ref comment));
    }

    return result;
}

async Task<List<IReadOnlyList<TranscriptSpan>>> TranscriptAsync(InProcessEngine engine, IReadOnlyList<string> body, string where)
{
    var inputs = body.Where(l => Regex.IsMatch(l, @"^il\[\d+\]> ")).Select(l => l[(l.IndexOf("> ", StringComparison.Ordinal) + 2)..]).ToList();
    if (inputs.Count == 0 || body.Any(l => l.StartsWith("  ...> ", StringComparison.Ordinal)))
    {
        // The editor's own rows: a view of typing, not of the engine, so plain text is the
        // editor's own, as in a cil block.
        return StyledLines(body, SpanStyle.Default);
    }

    // A transcript may end on the bare prompt that came next; it is not the engine's to say.
    var trailingPrompt = body.Count > 0 && Regex.IsMatch(body[^1], @"^il\[\d+\]>\s*$");
    var compared = trailingPrompt ? body.Take(body.Count - 1).ToList() : body;
    var produced = new List<TranscriptLine>();
    try
    {
        foreach (var input in inputs)
        {
            var reply = await engine.HandleAsync(input, CancellationToken.None);
            produced.AddRange(reply.Lines);
            if (reply.Quit)
            {
                break;
            }
        }
    }
    catch (Exception ex)
    {
        warnings.Add($"{where}: the replay failed: {ex.Message}");
        produced = null;
    }

    if (produced is not null)
    {
        var mismatch = -1;
        for (var i = 0; i < Math.Max(compared.Count, produced.Count); i++)
        {
            if (i >= compared.Count || i >= produced.Count || produced[i].PlainText != compared[i])
            {
                mismatch = i;
                break;
            }
        }

        if (mismatch < 0)
        {
            replayed++;
            var exact = produced.Select(l => l.Spans).ToList();
            if (trailingPrompt)
            {
                exact.Add([new TranscriptSpan(body[^1], SpanStyle.Prompt)]);
            }

            return exact;
        }

        var expected = mismatch < compared.Count ? compared[mismatch] : "(end)";
        var got = mismatch < produced.Count ? produced[mismatch].PlainText : "(end)";
        warnings.Add($"{where}: the replay reads differently at line {mismatch + 1}: the page has '{expected}', the engine says '{got}'");
    }

    // An echoed line's plain text wears the input style, as the engine echoes it.
    return StyledLines(body, SpanStyle.Input);
}

// A transcript styled line by line, with a block comment carried from one input line to the next
// as the engine carries it, and the given style for the plain text of an input line.
List<IReadOnlyList<TranscriptSpan>> StyledLines(IReadOnlyList<string> body, SpanStyle plain)
{
    var comment = false;
    var result = new List<IReadOnlyList<TranscriptSpan>>();
    foreach (var line in body)
    {
        result.Add(Styled(line, ref comment, plain));
    }

    return result;
}

// The rules the engine styles its own lines by, for a transcript the replay could not reproduce.
IReadOnlyList<TranscriptSpan> Styled(string line, ref bool comment, SpanStyle plain)
{
    var m = Regex.Match(line, @"^(il\[\d+\]> |  \.\.\.> )(.*)$");
    if (m.Success)
    {
        var gutter = new TranscriptSpan(m.Groups[1].Value, m.Groups[1].Value.StartsWith("il", StringComparison.Ordinal) ? SpanStyle.Prompt : SpanStyle.Dim);
        return [gutter, .. tokenizer.Spans(m.Groups[2].Value, ref comment, plain)];
    }

    m = Regex.Match(line, @"^(  ┊ \[)(.*?)(\])( ◂ top)?$");
    if (m.Success)
    {
        var spans = new List<TranscriptSpan> { new(m.Groups[1].Value, SpanStyle.Dim) };
        var names = m.Groups[2].Value.Length == 0 ? [] : m.Groups[2].Value.Split(", ");
        for (var i = 0; i < names.Length; i++)
        {
            if (i > 0)
            {
                spans.Add(new TranscriptSpan(", ", SpanStyle.Dim));
            }

            spans.Add(new TranscriptSpan(names[i], i == names.Length - 1 ? SpanStyle.TopType : SpanStyle.Type));
        }

        spans.Add(new TranscriptSpan(m.Groups[3].Value, SpanStyle.Dim));
        if (m.Groups[4].Success)
        {
            spans.Add(new TranscriptSpan(m.Groups[4].Value, SpanStyle.Dim));
        }

        return spans;
    }

    m = Regex.Match(line, @"^(  = )(.*?)( : .*)?$");
    if (m.Success)
    {
        return m.Groups[3].Success
            ? [new(m.Groups[1].Value, SpanStyle.Dim), new(m.Groups[2].Value), new(m.Groups[3].Value, SpanStyle.Dim)]
            : [new(m.Groups[1].Value, SpanStyle.Dim), new(m.Groups[2].Value)];
    }

    m = Regex.Match(line, @"^(  error: )(.*)$");
    if (m.Success)
    {
        return [new(m.Groups[1].Value, SpanStyle.Error), new(m.Groups[2].Value)];
    }

    // An instruction row of a listing: the offset column, the instruction, the stack column.
    m = Regex.Match(line, @"^(\s*[0-9a-f]{3,4}\s+)(.*?)(\s{2,}\[.*\])?$");
    if (m.Success)
    {
        var spans = new List<TranscriptSpan> { new(m.Groups[1].Value, SpanStyle.Dim) };
        spans.AddRange(tokenizer.Spans(m.Groups[2].Value));
        if (m.Groups[3].Success)
        {
            spans.Add(new TranscriptSpan(m.Groups[3].Value, SpanStyle.Dim));
        }

        return spans;
    }

    // A label, a directive, or a brace row of a listing is IL, and reads as the tokenizer says.
    if (Regex.IsMatch(line, @"^\s*([A-Za-z_][A-Za-z0-9_]*:\s*$|\.[a-z]|\{|\})"))
    {
        return tokenizer.Spans(line);
    }

    return [new(line, SpanStyle.Dim)];
}

static string FindRoot()
{
    var directory = Directory.GetCurrentDirectory();
    while (directory is not null && !Directory.Exists(Path.Combine(directory, "docs", "src", "content", "docs")))
    {
        directory = Path.GetDirectoryName(directory);
    }

    return directory ?? throw new InvalidOperationException("run from inside the repository");
}
