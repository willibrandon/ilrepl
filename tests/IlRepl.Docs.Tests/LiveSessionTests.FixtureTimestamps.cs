using System.Globalization;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser comparison workers reproduce fixture timestamps through the real virtual filesystem.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Both workers retain creation, write, and access times for roots, directories, files, and links.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonRestoresFixtureTimestamps(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var setup = """
            ldstr "/tmp/stamp-input/nested"
            call class DirectoryInfo Directory::CreateDirectory(string)
            pop
            ldstr "/tmp/stamp-input/empty"
            call class DirectoryInfo Directory::CreateDirectory(string)
            pop
            ldstr "/tmp/stamp-input/nested/data.txt"
            ldstr "seed"
            call void File::WriteAllText(string, string)
            ldstr "/tmp/stamp-input/alias.txt"
            ldstr "nested/data.txt"
            call class FileSystemInfo File::CreateSymbolicLink(string, string)
            pop
            ldstr "/tmp/stamp-input/alias-dir"
            ldstr "empty"
            call class FileSystemInfo Directory::CreateSymbolicLink(string, string)
            pop
            ldc.i4.1
            ret
            """;
        var ticks = ComparisonFixtureTimeExamples.Timestamp.Ticks.ToString(CultureInfo.InvariantCulture);
        await RunCorpusCellAsync(page, setup, 1);
        var parent = await ObserveComparisonResultsAsync(page);
        await parent.EvaluateAsync<object?>("""
            ({ paths, timestamp }) => {
              const filesystem = globalThis.getDotnetRuntime(0).Module.FS;
              for (const path of paths) {
                const node = filesystem.lookupPath('/tmp/stamp-input/' + path, { follow: false }).node;
                node.node_ops.setattr(node, { timestamp });
              }
            }
            """, new { paths = ComparisonFixtureTimeExamples.Paths,
                timestamp = new DateTimeOffset(ComparisonFixtureTimeExamples.Timestamp).ToUnixTimeMilliseconds() });
        var source = ComparisonFixtureTimeExamples.Source();
        await SubmitEditSourceAsync(page, source + "\n.edit Read as Copy {\n" + source + "\n}", "edit Copy committed as revision 1");
        await TypeLineAsync(page, ".compare Copy () --files /tmp/stamp-input");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            var values = side.GetProperty("result").GetProperty("members");
            Assert.AreEqual(ComparisonFixtureTimeExamples.Paths.Count * 3, values.GetArrayLength());
            foreach (var value in values.EnumerateArray())
            {
                Assert.AreEqual(ticks, value.GetProperty("value").GetProperty("value").GetString(), value.GetRawText());
            }
        }

        await InputIdleAsync(page);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
