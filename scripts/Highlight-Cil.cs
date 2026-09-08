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
// a light ground, which the site reads at build. The splash page's hero transcript, kept in
// docs/src/hero.ilrepl, is replayed the same way into a component of spans with one class per
// style, beside a stylesheet that gives each class its colour on either ground. The engine
// needs the JIT, which a file-based app's default of publishing native would take away.

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
var blocks = new SortedDictionary<string, (string Where, string Language, bool Editor, List<IReadOnlyList<TranscriptSpan>> Lines)>(StringComparer.Ordinal);
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
            // Source, or a view of the editor, is drawn as the editor draws it: an error is
            // underlined under its own colour. A transcript's echo has the error in red.
            var editor = language == "cil" || IsEditorView(body);
            blocks[Key(body)] = (where, language, editor, spans);
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
        json.WriteBoolean("editor", block.Editor);
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

await WriteHeroAsync();

Console.WriteLine($"{blocks.Count} blocks coloured ({replayed} transcripts replayed exactly) into {Path.GetRelativePath(root, output)}");
foreach (var warning in warnings)
{
    Console.WriteLine("  " + warning);
}

return 0;

// The block's text as the site sees it: its lines, trailing blank lines dropped, joined by newlines.
static string Key(IReadOnlyList<string> body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', body))));

// The splash page's hero: the transcript as a component of spans with one class per style, and
// the stylesheet that colours each class on a dark ground and on a light one.
async Task WriteHeroAsync()
{
    var source = Path.Combine(root, "docs", "src", "hero.ilrepl");
    var body = File.ReadAllLines(source).ToList();
    while (body.Count > 0 && body[^1].Trim().Length == 0)
    {
        body.RemoveAt(body.Count - 1);
    }

    var scratch = Directory.CreateTempSubdirectory("ilrepl-docs-");
    Directory.SetCurrentDirectory(scratch.FullName);
    List<IReadOnlyList<TranscriptSpan>> lines;
    try
    {
        await using var engine = new InProcessEngine();
        lines = await TranscriptAsync(engine, body, "hero.ilrepl:1");
    }
    finally
    {
        Directory.SetCurrentDirectory(cwd);
        scratch.Delete(true);
    }

    var component = new StringBuilder();
    component.Append("---\n// Written by scripts/Highlight-Cil.cs from src/hero.ilrepl; edit that and run the script.\n---\n");
    component.Append("<pre class=\"hero-prompt\">");
    for (var i = 0; i < lines.Count; i++)
    {
        if (i > 0)
        {
            component.Append('\n');
        }

        foreach (var span in lines[i])
        {
            var text = System.Net.WebUtility.HtmlEncode(span.Text);
            component.Append(span.Style == SpanStyle.Default ? text : $"<span class=\"cil-{span.Style}\">{text}</span>");
        }
    }

    component.Append("</pre>\n");
    File.WriteAllText(Path.Combine(root, "docs", "src", "generated", "HeroPrompt.astro"), component.ToString());

    var css = new StringBuilder();
    css.Append("/* Written by scripts/Highlight-Cil.cs from the terminal's palette; edit SpanPalette and run the script. */\n");
    foreach (var (selector, colour) in new (string, Func<SpanStyle, Hex1bColor>)[] { ("", SpanPalette.Color), ("[data-theme='light'] ", SpanPalette.LightColor) })
    {
        foreach (var style in Enum.GetValues<SpanStyle>())
        {
            var color = colour(style);
            if (!color.IsDefault)
            {
                css.Append($"{selector}.cil-{style} {{ color: #{color.R:x2}{color.G:x2}{color.B:x2}; }}\n");
            }
        }
    }

    // The editor's error: a curly underline in the error colour under the text's own colour.
    css.Append($".cil-error-underline {{ text-decoration: underline wavy #{Hex(SpanPalette.Color(SpanStyle.Error))}; text-underline-offset: 0.15em; }}\n");
    css.Append($"[data-theme='light'] .cil-error-underline {{ text-decoration-color: #{Hex(SpanPalette.LightColor(SpanStyle.Error))}; }}\n");
    File.WriteAllText(Path.Combine(root, "docs", "src", "generated", "cil-palette.css"), css.ToString());
}

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
    // A prompt with nothing after it, other than the block's last line, is a blank line sent,
    // which runs the cell.
    var inputs = body.Take(body.Count - 1).Where(l => Patterns.InputLine().IsMatch(l) || Patterns.BarePrompt().IsMatch(l)).Concat(body.TakeLast(1).Where(l => Patterns.InputLine().IsMatch(l)))
        .Select(l => Patterns.InputLine().IsMatch(l) ? l[(l.IndexOf("> ", StringComparison.Ordinal) + 2)..] : "").ToList();
    if (inputs.Count == 0 || IsEditorView(body))
    {
        // The editor's own rows: a view of typing, not of the engine, so plain text is the
        // editor's own, as in a cil block.
        return StyledLines(body, SpanStyle.Default);
    }

    // A transcript may end on the bare prompt that came next; it is not the engine's to say.
    var trailingPrompt = body.Count > 0 && Patterns.BarePrompt().IsMatch(body[^1]);
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
        // A page's prompt numbers may count cells the page does not show, so they are not
        // compared; the page's own prompt is kept and the engine's spans follow it.
        var mismatch = -1;
        for (var i = 0; i < Math.Max(compared.Count, produced.Count); i++)
        {
            if (i >= compared.Count || i >= produced.Count || SamePrompt(produced[i].PlainText).TrimEnd() != SamePrompt(compared[i]).TrimEnd())
            {
                mismatch = i;
                break;
            }
        }

        if (mismatch < 0)
        {
            replayed++;
            var exact = produced.Select((l, i) => WithPagePrompt(l.Spans, compared[i])).ToList();
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

static string SamePrompt(string line) => Patterns.PromptNumber().Replace(line, "il[#]>");

// The engine's spans for a line, with the page's own prompt in place of the engine's.
static IReadOnlyList<TranscriptSpan> WithPagePrompt(IReadOnlyList<TranscriptSpan> spans, string pageLine)
{
    var prompt = Patterns.PagePrompt().Match(pageLine);
    if (!prompt.Success || spans.Count == 0 || spans[0].Style != SpanStyle.Prompt)
    {
        return spans;
    }

    return [new TranscriptSpan(prompt.Value, SpanStyle.Prompt), .. spans.Skip(1)];
}

// A block that shows the editor's own rows is a view of typing, not of the engine.
static bool IsEditorView(IReadOnlyList<string> body) => body.Any(l => l.StartsWith("  ...>", StringComparison.Ordinal));

// A transcript styled line by line, with a block comment carried from one input line to the next
// as the engine carries it, and the given style for the plain text of an input line.
List<IReadOnlyList<TranscriptSpan>> StyledLines(IReadOnlyList<string> body, SpanStyle plain)
{
    var comment = false;
    var listing = false;
    var result = new List<IReadOnlyList<TranscriptSpan>>();
    foreach (var line in body)
    {
        // After .types or .methods the lines up to the next prompt are the listing: a type's
        // header is a label and its members are plain, as the engine lists them.
        var input = Patterns.InputLine().Match(line);
        if (input.Success)
        {
            var text = line[input.Length..].TrimStart();
            listing = text.StartsWith(".types", StringComparison.Ordinal) || text.StartsWith(".methods", StringComparison.Ordinal);
        }
        else if (listing && line.StartsWith("  ", StringComparison.Ordinal) && line.Trim() is not ("no types" or "no methods"))
        {
            result.Add([new TranscriptSpan(line, Patterns.TypeHeader().IsMatch(line) ? SpanStyle.Label : SpanStyle.Default)]);
            continue;
        }

        result.Add(Styled(line, ref comment, plain));
    }

    return result;
}

// The rules the engine styles its own lines by, for a transcript the replay could not reproduce.
IReadOnlyList<TranscriptSpan> Styled(string line, ref bool comment, SpanStyle plain)
{
    if (Patterns.BarePrompt().IsMatch(line))
    {
        // The bare prompt that came next.
        return [new(line, SpanStyle.Prompt)];
    }

    var m = Patterns.Gutter().Match(line);
    if (m.Success)
    {
        var gutter = new TranscriptSpan(m.Groups[1].Value, m.Groups[1].Value.StartsWith("il", StringComparison.Ordinal) ? SpanStyle.Prompt : SpanStyle.Dim);
        return [gutter, .. tokenizer.Spans(m.Groups[2].Value, ref comment, plain)];
    }

    m = Patterns.StackLine().Match(line);
    if (m.Success)
    {
        var spans = new List<TranscriptSpan> { new(m.Groups[1].Value, SpanStyle.Dim) };
        var names = SplitStack(m.Groups[2].Value);
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

    m = Patterns.ResultWithType().Match(line);
    if (!m.Success)
    {
        m = Patterns.ResultLine().Match(line);
    }

    if (m.Success)
    {
        // A result: the value as the value formatter wrote it, read back by its shape.
        var spans = new List<TranscriptSpan> { new(m.Groups[1].Value, SpanStyle.Dim) };
        spans.AddRange(ValueSpans(m.Groups[2].Value));
        if (m.Groups.Count > 3 && m.Groups[3].Success)
        {
            spans.Add(new TranscriptSpan(m.Groups[3].Value, SpanStyle.Dim));
        }

        return spans;
    }

    m = Patterns.ErrorLine().Match(line);
    if (m.Success)
    {
        return [new(m.Groups[1].Value, SpanStyle.Error), new(m.Groups[2].Value)];
    }

    // An instruction row of a listing: the offset column, the instruction, the stack column.
    m = Patterns.ListingRow().Match(line);
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
    if (Patterns.ListingIl().IsMatch(line))
    {
        return tokenizer.Spans(line);
    }

    // The engine's own notes are indented; a line that is not is what the program wrote. In a
    // view of the editor an unindented line is its status bar, plain.
    if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
    {
        return plain == SpanStyle.Input ? [new(line, SpanStyle.Output)] : [new(line)];
    }

    return [new(line, SpanStyle.Dim)];
}

// The value formatter's spans for a value, read back from the text it wrote: literals, a
// sequence in brackets or braces, a session object as its type name and labelled fields, and
// the placeholders it writes when it cannot say more. Text of no known shape is plain.
static IReadOnlyList<TranscriptSpan> ValueSpans(string text)
{
    var spans = new List<TranscriptSpan>();
    var i = 0;
    return ParseValue(text, ref i, spans) && i == text.Length ? spans : [new TranscriptSpan(text)];
}

static bool ParseValue(string s, ref int i, List<TranscriptSpan> spans)
{
    if (i >= s.Length)
    {
        return false;
    }

    foreach (var word in new[] { "null", "true", "false" })
    {
        if (Word(s, i, word))
        {
            spans.Add(new TranscriptSpan(word, SpanStyle.Keyword));
            i += word.Length;
            return true;
        }
    }

    if (s[i] is '"' or '\'')
    {
        var end = i + 1;
        while (end < s.Length && s[end] != s[i])
        {
            end += s[end] == '\\' ? 2 : 1;
        }

        end = Math.Min(end + 1, s.Length);
        spans.Add(new TranscriptSpan(s[i..end], SpanStyle.String));
        i = end;
        return true;
    }

    var number = Patterns.NumberAt().Match(s, i);
    if (number.Success && number.Index == i)
    {
        spans.Add(new TranscriptSpan(number.Value, SpanStyle.Number));
        i += number.Length;
        return true;
    }

    if (s.AsSpan(i).StartsWith("typeof(", StringComparison.Ordinal) && s.IndexOf(')', i) is var paren && paren > i)
    {
        spans.Add(new TranscriptSpan(s[i..(paren + 1)], SpanStyle.Type));
        i = paren + 1;
        return true;
    }

    if (s.AsSpan(i).StartsWith("(void)", StringComparison.Ordinal))
    {
        spans.Add(new TranscriptSpan("(void)", SpanStyle.Dim));
        i += "(void)".Length;
        return true;
    }

    if (s[i] == '…')
    {
        spans.Add(new TranscriptSpan("…", SpanStyle.Dim));
        i++;
        return true;
    }

    if (s.AsSpan(i).StartsWith("↺ ", StringComparison.Ordinal))
    {
        var end = NameEnd(s, i + 2);
        spans.Add(new TranscriptSpan(s[i..end], SpanStyle.Dim));
        i = end;
        return true;
    }

    if (s[i] == '{' && Patterns.Placeholder().Match(s, i) is { Success: true } placeholder && placeholder.Index == i)
    {
        spans.Add(new TranscriptSpan(placeholder.Value, SpanStyle.Dim));
        i += placeholder.Length;
        return true;
    }

    if (s[i] is '[' or '{')
    {
        var close = s[i] == '[' ? ']' : '}';
        spans.Add(new TranscriptSpan(s[i].ToString()));
        i++;
        while (i < s.Length && s[i] != close)
        {
            if (!ParseValue(s, ref i, spans))
            {
                return false;
            }

            if (s.AsSpan(i).StartsWith(", ", StringComparison.Ordinal))
            {
                spans.Add(new TranscriptSpan(", "));
                i += 2;
            }
        }

        if (i >= s.Length)
        {
            return false;
        }

        spans.Add(new TranscriptSpan(close.ToString()));
        i++;
        return true;
    }

    if (char.IsLetter(s[i]) || s[i] == '_')
    {
        var end = NameEnd(s, i);
        var name = s[i..end];
        if (s.AsSpan(end).StartsWith(" {…}", StringComparison.Ordinal))
        {
            spans.Add(new TranscriptSpan(name + " {…}", SpanStyle.Dim));
            i = end + " {…}".Length;
            return true;
        }

        if (s.AsSpan(end).StartsWith(" { }", StringComparison.Ordinal))
        {
            spans.Add(new TranscriptSpan(name, SpanStyle.Type));
            spans.Add(new TranscriptSpan(" { }"));
            i = end + " { }".Length;
            return true;
        }

        if (s.AsSpan(end).StartsWith(" { ", StringComparison.Ordinal))
        {
            spans.Add(new TranscriptSpan(name, SpanStyle.Type));
            spans.Add(new TranscriptSpan(" { "));
            i = end + " { ".Length;
            while (true)
            {
                if (s[i] == '…')
                {
                    spans.Add(new TranscriptSpan("…", SpanStyle.Dim));
                    i++;
                }
                else
                {
                    var label = Patterns.FieldLabel().Match(s, i);
                    if (!label.Success || label.Index != i)
                    {
                        return false;
                    }

                    spans.Add(new TranscriptSpan(label.Groups[1].Value, SpanStyle.Label));
                    spans.Add(new TranscriptSpan(" = "));
                    i += label.Length;
                    if (Patterns.Placeholder().Match(s, i) is { Success: true } threw && threw.Index == i)
                    {
                        spans.Add(new TranscriptSpan(threw.Value, SpanStyle.Dim));
                        i += threw.Length;
                    }
                    else if (!ParseValue(s, ref i, spans))
                    {
                        return false;
                    }
                }

                if (s.AsSpan(i).StartsWith(", ", StringComparison.Ordinal))
                {
                    spans.Add(new TranscriptSpan(", "));
                    i += 2;
                    continue;
                }

                if (s.AsSpan(i).StartsWith(" }", StringComparison.Ordinal))
                {
                    spans.Add(new TranscriptSpan(" }"));
                    i += 2;
                    return true;
                }

                return false;
            }
        }

        // An enum value, or text a type wrote for itself.
        spans.Add(new TranscriptSpan(name));
        i = end;
        return true;
    }

    return false;
}

// The entries of a stack as the engine lists them, split at the commas between them and not at
// those inside a generic type's arguments.
static string[] SplitStack(string stack)
{
    if (stack.Length == 0)
    {
        return [];
    }

    var entries = new List<string>();
    var start = 0;
    var depth = 0;
    for (var i = 0; i < stack.Length; i++)
    {
        switch (stack[i])
        {
            case '<':
                depth++;
                break;
            case '>':
                depth--;
                break;
            case ',' when depth == 0 && i + 1 < stack.Length && stack[i + 1] == ' ':
                entries.Add(stack[start..i]);
                start = i + 2;
                i++;
                break;
            default:
                break;
        }
    }

    entries.Add(stack[start..]);
    return [.. entries];
}

static bool Word(string s, int i, string word) =>
    s.AsSpan(i).StartsWith(word, StringComparison.Ordinal) && (i + word.Length == s.Length || !char.IsLetterOrDigit(s[i + word.Length]));

// The end of a type or member name as the formatter prints one: dots, nesting, arity marks, and
// generic arguments in angle brackets included.
static int NameEnd(string s, int i)
{
    var depth = 0;
    while (i < s.Length)
    {
        var c = s[i];
        if (c == '<')
        {
            depth++;
        }
        else if (c == '>')
        {
            depth--;
        }
        else if (!(char.IsLetterOrDigit(c) || c is '_' or '.' or '`' or '/' or '[' or ']' || (depth > 0 && c is ',' or ' ')))
        {
            break;
        }

        i++;
    }

    return i;
}

static string Hex(Hex1bColor color) => $"{color.R:x2}{color.G:x2}{color.B:x2}";

static string FindRoot()
{
    var directory = Directory.GetCurrentDirectory();
    while (directory is not null && !Directory.Exists(Path.Combine(directory, "docs", "src", "content", "docs")))
    {
        directory = Path.GetDirectoryName(directory);
    }

    return directory ?? throw new InvalidOperationException("run from inside the repository");
}

// The shapes of a transcript's lines, compiled ahead of time.
static partial class Patterns
{
    [GeneratedRegex(@"^il\[\d+\]> ")]
    public static partial Regex InputLine();

    [GeneratedRegex(@"^il\[\d+\]>\s*$")]
    public static partial Regex BarePrompt();

    [GeneratedRegex(@"^il\[\d+\]>")]
    public static partial Regex PromptNumber();

    [GeneratedRegex(@"^il\[\d+\]> ?")]
    public static partial Regex PagePrompt();

    [GeneratedRegex(@"^(il\[\d+\]> |  \.\.\.>(?: |$))(.*)$")]
    public static partial Regex Gutter();

    [GeneratedRegex(@"^\s*(class|struct|interface|enum|delegate) \S")]
    public static partial Regex TypeHeader();

    [GeneratedRegex(@"^(  ┊ \[)(.*?)(\])( ◂ top)?$")]
    public static partial Regex StackLine();

    [GeneratedRegex(@"^(  = )(.*)( : .+)$")]
    public static partial Regex ResultWithType();

    [GeneratedRegex(@"^(  = )(.*)$")]
    public static partial Regex ResultLine();

    [GeneratedRegex(@"\G-?(\d+(\.\d+)?([eE][+-]?\d+)?|NaN|Infinity)(?![\w.])")]
    public static partial Regex NumberAt();

    [GeneratedRegex(@"\G\{(ToString threw [A-Za-z0-9_.]+|threw [A-Za-z0-9_.]+|[A-Za-z_][A-Za-z0-9_.`<>/\[\], ]*)\}")]
    public static partial Regex Placeholder();

    [GeneratedRegex(@"\G([A-Za-z_][A-Za-z0-9_]*) = ")]
    public static partial Regex FieldLabel();

    [GeneratedRegex(@"^(  error: )(.*)$")]
    public static partial Regex ErrorLine();

    [GeneratedRegex(@"^(\s*[0-9a-f]{3,4}\s+)(.*?)(\s+(\[[^\[\]]*\]|unreachable|\?))?$")]
    public static partial Regex ListingRow();

    [GeneratedRegex(@"^\s*([A-Za-z_][A-Za-z0-9_]*:\s*$|\.[a-z]|\{|\})")]
    public static partial Regex ListingIl();

}
