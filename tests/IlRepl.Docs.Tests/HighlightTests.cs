using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IlRepl.Docs.Tests;

/// <summary>
/// The CIL on the docs site is coloured by the terminal's own tokenizer: a generator writes the
/// tokens of every tagged block to a map the site reads at build. These tests keep the map and
/// the pages in step.
/// </summary>
[TestClass]
public sealed class HighlightTests
{
    private static readonly string s_docs = Path.Combine(SitePaths.Root, "docs", "src", "content", "docs");
    private static readonly string s_map = Path.Combine(SitePaths.Root, "docs", "src", "generated", "cil-tokens.json");

    /// <summary>
    /// Completion examples keep terminal errors colored and palette illustrations separate from submitted IL.
    /// </summary>
    [TestMethod]
    public void CompletionExamples_PreserveErrorAndEditorRendering()
    {
        using var map = JsonDocument.Parse(File.ReadAllText(s_map));
        var blocks = map.RootElement.GetProperty("blocks").EnumerateObject().Select(property => property.Value).ToArray();
        var suggestions = blocks.Where(block => block.GetProperty("where").GetString()!
            .StartsWith("usage/member-references.md:", StringComparison.Ordinal));
        Assert.Contains(block => block.GetProperty("lines").EnumerateArray()
            .Any(line => line.EnumerateArray().Any(token => token[2].GetString() == "Error")), suggestions);
        var editor = blocks.Where(block => block.GetProperty("where").GetString()!
            .StartsWith("usage/editing.md:", StringComparison.Ordinal) && block.GetProperty("editor").GetBoolean());
        Assert.IsNotEmpty(editor, "Unsubmitted editor examples keep their own rendering mode.");
        var page = File.ReadAllText(Path.Combine(SitePaths.Dist, "usage", "editing", "index.html"));
        Assert.Contains("Math::Max", page);
        Assert.Contains("Math.Max overloads in the running terminal", page, "The palette uses an actual terminal capture.");
        var keyboard = File.ReadAllText(Path.Combine(SitePaths.Dist, "reference", "keyboard", "index.html"));
        Assert.Contains("PageUp", keyboard);
        Assert.Contains("PageDown", keyboard);
        Assert.Contains("did you mean", File.ReadAllText(Path.Combine(SitePaths.Dist, "usage", "member-references", "index.html")));
    }

    /// <summary>
    /// Every block tagged cil or ilrepl in the docs has its tokens in the map, line for line,
    /// and the map holds nothing else, so an edited block fails here until the generator runs.
    /// </summary>
    [TestMethod]
    public void TaggedBlocks_AreAllInTheTokenMap()
    {
        using var map = JsonDocument.Parse(File.ReadAllText(s_map));
        var blocks = map.RootElement.GetProperty("blocks");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(s_docs, "*.md*", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i] is not ("```cil" or "```ilrepl"))
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

                var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', body))));
                var where = $"{Path.GetRelativePath(s_docs, file)}:{start + 1}";
                Assert.IsTrue(blocks.TryGetProperty(key, out var block), $"{where} is not in the token map; run scripts/Highlight-Cil.cs");
                Assert.AreEqual(body.Count, block.GetProperty("lines").GetArrayLength(), $"{where} has a different line count in the token map");
                seen.Add(key);
                i = end;
            }
        }

        var stale = blocks.EnumerateObject().Where(b => !seen.Contains(b.Name)).Select(b => b.Value.GetProperty("where").GetString()).ToList();
        Assert.IsEmpty(stale, "the token map holds blocks the docs no longer have: " + string.Join(", ", stale));
        Assert.IsNotEmpty(seen, "the docs should have tagged blocks");
    }

    /// <summary>
    /// The built page carries the palette's colours on the tokens, the terminal's own for the
    /// dark theme and the light ground's for the light one: the directive, the opcode, the
    /// prompt, and the top of the stack on the methods page.
    /// </summary>
    [TestMethod]
    public void MethodsPage_IsColouredByThePalette()
    {
        var page = Path.Combine(SitePaths.Dist, "usage", "methods", "index.html");
        Assert.IsTrue(File.Exists(page), "build the docs first");
        var html = File.ReadAllText(page);
        using var map = JsonDocument.Parse(File.ReadAllText(s_map));
        var palette = map.RootElement.GetProperty("palette");
        string Span(string style, string text) => $"<span style=\"--0:{palette.GetProperty("dark").GetProperty(style).GetString()};--1:{palette.GetProperty("light").GetProperty(style).GetString()}\">{text}</span>";

        Assert.Contains(Span("Directive", ".method"), html, "the directive should wear the directive colours");
        Assert.Contains(Span("Opcode", "ldarg"), html, "the opcode should wear the opcode colours");
        Assert.Contains(Span("Prompt", "il[1]&gt; "), html, "the prompt should wear the prompt colours");
        Assert.Contains(Span("TopType", "int32"), html, "the top of the stack should wear the top-type colours");
    }

    /// <summary>
    /// The splash page's hero transcript is coloured the same way, through a generated component
    /// with one class per style and a stylesheet holding both palettes.
    /// </summary>
    [TestMethod]
    public void SplashPage_HeroIsColouredByThePalette()
    {
        var page = Path.Combine(SitePaths.Dist, "index.html");
        Assert.IsTrue(File.Exists(page), "build the docs first");
        var html = File.ReadAllText(page);
        Assert.Contains("<span class=\"cil-Prompt\">il[1]&gt; </span>", html, "the prompt should wear the prompt class");
        Assert.Contains("<span class=\"cil-Opcode\">ldc.i4</span>", html, "the opcode should wear the opcode class");
        Assert.Contains("<span class=\"cil-TopType\">int32</span>", html, "the top of the stack should wear the top-type class");
        Assert.Contains("<span class=\"cil-Number\">42</span>", html, "the result should wear the number class");

        using var map = JsonDocument.Parse(File.ReadAllText(s_map));
        var dark = map.RootElement.GetProperty("palette").GetProperty("dark").GetProperty("Opcode").GetString()!;
        var light = map.RootElement.GetProperty("palette").GetProperty("light").GetProperty("Opcode").GetString()!;
        // The bundler merges the classes that share a colour into one rule, so the class and the
        // two colours are looked for on their own.
        var css = Directory.EnumerateFiles(Path.Combine(SitePaths.Dist, "_astro"), "*.css").Select(File.ReadAllText).ToList();
        Assert.Contains(c => c.Contains(".cil-Opcode", StringComparison.Ordinal) && c.Contains("{color:" + dark + "}", StringComparison.Ordinal) && c.Contains("{color:" + light + "}", StringComparison.Ordinal), css, "the stylesheet should colour the class on both grounds");
    }

    /// <summary>
    /// An error token is drawn as its host draws it: in a view of the editor a curly underline
    /// under the text's own colour, in a transcript's echo the error colour.
    /// </summary>
    [TestMethod]
    public void ErrorTokens_AreDrawnAsTheirHostDrawsThem()
    {
        var editing = File.ReadAllText(Path.Combine(SitePaths.Dist, "usage", "editing", "index.html"));
        var quickStart = File.ReadAllText(Path.Combine(SitePaths.Dist, "getting-started", "quick-start", "index.html"));
        using var map = JsonDocument.Parse(File.ReadAllText(s_map));
        var palette = map.RootElement.GetProperty("palette");
        var error = $"--0:{palette.GetProperty("dark").GetProperty("Error").GetString()};--1:{palette.GetProperty("light").GetProperty("Error").GetString()}";

        // The underline wraps the token, which keeps the page's own text colour inside it.
        var underlined = editing.IndexOf("<span class=\"cil-error-underline\">", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, underlined, "the editor's view should underline the typo");
        var wrapped = editing.Substring(underlined, Math.Min(240, editing.Length - underlined));
        Assert.Contains(">lcd.i4</span></span>", wrapped, "the underline should wrap the typo");
        Assert.DoesNotContain(error, wrapped[..wrapped.IndexOf("lcd.i4", StringComparison.Ordinal)], "the underlined typo should keep its own colour");
        Assert.Contains($"<span style=\"{error}\">lcd.i4</span>", quickStart, "the transcript's echo should show the typo in the error colour");
    }

    /// <summary>
    /// A .types listing shows each type's header in the label colour and its members plain, as
    /// the engine lists them.
    /// </summary>
    [TestMethod]
    public void TypesListing_HeadersWearTheLabelColour()
    {
        var types = File.ReadAllText(Path.Combine(SitePaths.Dist, "usage", "types", "index.html"));
        using var map = JsonDocument.Parse(File.ReadAllText(s_map));
        var palette = map.RootElement.GetProperty("palette");
        var label = $"--0:{palette.GetProperty("dark").GetProperty("Label").GetString()};--1:{palette.GetProperty("light").GetProperty("Label").GetString()}";
        // The page names the struct in a note first, dim; the listing's header is the one that
        // wears the label colour, so every occurrence is looked at.
        var headers = new List<string>();
        for (var at = types.IndexOf("struct Point</span>", StringComparison.Ordinal); at >= 0; at = types.IndexOf("struct Point</span>", at + 1, StringComparison.Ordinal))
        {
            headers.Add(types[Math.Max(0, at - 120)..at]);
        }

        Assert.IsNotEmpty(headers, "the page should name the struct");
        Assert.Contains(h => h.Contains(label, StringComparison.Ordinal), headers, "the listing's header should wear the label colour");
    }
}
