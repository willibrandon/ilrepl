using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    /// The built page carries the palette's colours on the tokens: the directive, the opcode,
    /// the prompt, and the top of the stack on the methods page wear the terminal's colours.
    /// </summary>
    [TestMethod]
    public void MethodsPage_IsColouredByThePalette()
    {
        var page = Path.Combine(SitePaths.Dist, "usage", "methods", "index.html");
        Assert.IsTrue(File.Exists(page), "build the docs first");
        var html = File.ReadAllText(page);
        using var map = JsonDocument.Parse(File.ReadAllText(s_map));
        var palette = map.RootElement.GetProperty("palette");
        string Colour(string style) => palette.GetProperty(style).GetString()!;

        Assert.IsTrue(Regex.IsMatch(html, $"""<span style="--0:{Colour("Directive")};--1:{Colour("Directive")}">\.method</span>"""), "the directive should wear the directive colour");
        Assert.IsTrue(Regex.IsMatch(html, $"""<span style="--0:{Colour("Opcode")};--1:{Colour("Opcode")}">ldarg</span>"""), "the opcode should wear the opcode colour");
        Assert.IsTrue(Regex.IsMatch(html, $"""<span style="--0:{Colour("Prompt")};--1:{Colour("Prompt")}">il\[1\]&gt; </span>"""), "the prompt should wear the prompt colour");
        Assert.IsTrue(Regex.IsMatch(html, $"""<span style="--0:{Colour("TopType")};--1:{Colour("TopType")}">int32</span>"""), "the top of the stack should wear the top-type colour");
    }
}
