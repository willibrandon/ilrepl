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
        var css = Directory.EnumerateFiles(Path.Combine(SitePaths.Dist, "_astro"), "*.css").Select(File.ReadAllText).ToList();
        Assert.Contains(c => c.Contains(".cil-Opcode{color:" + dark, StringComparison.Ordinal) && c.Contains(".cil-Opcode{color:" + light, StringComparison.Ordinal), css, "the stylesheet should colour the class on both grounds");
    }
}
