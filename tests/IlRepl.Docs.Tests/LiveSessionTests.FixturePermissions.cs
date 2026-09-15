using System.Globalization;
using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser workers preserve actual virtual-filesystem permissions independently of later source changes.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Frozen read-only and Unix modes survive repeated workers, and a permission-dependent edit reports its actual difference.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonRestoresFixturePermissions(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await RunCorpusCellAsync(page, """
            ldstr "/tmp/permission-input/nested"
            call class DirectoryInfo Directory::CreateDirectory(string)
            pop
            ldstr "/tmp/permission-input/empty"
            call class DirectoryInfo Directory::CreateDirectory(string)
            pop
            ldstr "/tmp/permission-input/nested/data.txt"
            ldstr "seed"
            call void File::WriteAllText(string, string)
            ldstr "/tmp/permission-input/readonly.txt"
            ldstr "unchanged"
            call void File::WriteAllText(string, string)
            ldstr "/tmp/permission-input/tool.sh"
            ldstr "#!/bin/sh\nexit 0\n"
            call void File::WriteAllText(string, string)
            ldstr "/tmp/permission-input/readonly.txt"
            ldc.i4.1
            call void File::SetAttributes(string, valuetype FileAttributes)
            ldc.i4.1
            ret
            """, 1);
        var parent = await ObserveComparisonResultsAsync(page);
        var entries = ComparisonFixturePermissionExamples.Paths.Select(path => new
        {
            path,
            mode = (int)ComparisonFixturePermissionExamples.Mode(path),
            directory = path is "." or "nested" or "empty",
        }).ToArray();
        var expected = entries.SelectMany(entry => new[]
        {
            (entry.directory ? (int)FileAttributes.Directory : 0) | ((entry.mode & (int)UnixFileMode.UserWrite) == 0 ? 1 : 0),
            entry.mode,
        }).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray();
        await parent.EvaluateAsync<object?>("""
            entries => {
              self.permissionEntries = entries;
              self.permissionPackages = [];
              self.permissionMutations = 0;
              const send = self.postMessage.bind(self);
              self.postMessage = (message, ...options) => {
                if (message.type === 'comparison-run' && message.original) {
                  self.permissionPackages.push(JSON.parse(message.package));
                  const filesystem = globalThis.getDotnetRuntime(0).Module.FS;
                  for (const entry of entries) {
                    filesystem.chmod('/tmp/permission-input/' + entry.path, entry.directory ? 0o700 : 0o600);
                  }
                  self.permissionMutations++;
                }
                return send(message, ...options);
              };
            }
            """, entries);
        var source = ComparisonFixturePermissionExamples.Source(unix: true, restrictAfterRead: true);
        await SubmitEditSourceAsync(page, source + "\n.edit Read as Copy {\n" + source + "\n}", "edit Copy committed as revision 1");
        for (var run = 0; run < 2; run++)
        {
            await ResetPermissionsAsync();
            await TypeLineAsync(page, ".compare Copy () --files /tmp/permission-input");
            foreach (var index in new[] { run * 2, (run * 2) + 1 })
            {
                var side = await WaitForComparisonResultAsync(parent, index);
                Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
                var values = side.GetProperty("result").GetProperty("members");
                Assert.AreSequenceEqual(expected,
                    values.EnumerateArray().Select(value => value.GetProperty("value").GetProperty("value").GetString()));
                Assert.AreEqual(1, side.GetProperty("invocations").GetArrayLength());
            }

            await ExpectComparisonTextAsync(page, "stdout:");
            await InputIdleAsync(page);
            Assert.Contains("Copy: match", await ReadComparisonTranscriptAsync(page));
            await page.Mouse.WheelAsync(0, 10_000);
            await ExpectComparisonTextAsync(page, "stdout:");
            await InputIdleAsync(page);
            await AssertSourceAsync(run + 1);
        }

        await SubmitEditSourceAsync(page, ComparisonFixturePermissionExamples.Branch(false) + "\n.edit Check as PermissionBranch {\n"
            + ComparisonFixturePermissionExamples.Branch(true) + "\n}", "edit PermissionBranch committed as revision 1");
        await ResetPermissionsAsync();
        await TypeLineAsync(page, ".compare PermissionBranch () --files /tmp/permission-input");
        var original = await WaitForComparisonResultAsync(parent, 4);
        var edited = await WaitForComparisonResultAsync(parent, 5);
        Assert.AreEqual("completed", original.GetProperty("outcome").GetString(), original.GetRawText());
        Assert.AreEqual("completed", edited.GetProperty("outcome").GetString(), edited.GetRawText());
        Assert.AreEqual("42", original.GetProperty("result").GetProperty("value").GetString());
        Assert.AreEqual("43", edited.GetProperty("result").GetProperty("value").GetString());
        await ExpectComparisonTextAsync(page, "PermissionBranch: different");
        await AssertSourceAsync(3);
        var packages = await parent.EvaluateAsync<JsonElement>("() => self.permissionPackages");
        Assert.AreEqual(3, packages.GetArrayLength());
        foreach (var package in packages.EnumerateArray())
        {
            var files = package.GetProperty("files").EnumerateArray().ToArray();
            Assert.HasCount(entries.Length, files);
            foreach (var entry in entries)
            {
                var captured = files.Single(file => file.GetProperty("path").GetString() == entry.path);
                var attributes = captured.GetProperty("attributes").GetInt32();
                Assert.AreEqual((entry.mode & (int)UnixFileMode.UserWrite) == 0, (attributes & (int)FileAttributes.ReadOnly) != 0);
                Assert.AreEqual(entry.directory, (attributes & (int)FileAttributes.Directory) != 0);
                Assert.AreEqual(entry.mode, captured.GetProperty("unixMode").GetInt32());
            }
        }

        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));

        Task ResetPermissionsAsync() => parent.EvaluateAsync<object?>("""
            timestamp => {
              const filesystem = globalThis.getDotnetRuntime(0).Module.FS;
              for (const entry of self.permissionEntries) {
                const path = '/tmp/permission-input/' + entry.path;
                filesystem.chmod(path, entry.mode);
                const node = filesystem.lookupPath(path, { follow: false }).node;
                node.node_ops.setattr(node, { timestamp });
              }
            }
            """, new DateTimeOffset(ComparisonFixtureTimeExamples.Timestamp).ToUnixTimeMilliseconds());

        async Task AssertSourceAsync(int mutations)
        {
            var snapshot = await parent.EvaluateAsync<JsonElement>("""
                () => {
                  const filesystem = globalThis.getDotnetRuntime(0).Module.FS;
                  return {
                    mutations: self.permissionMutations,
                    modes: self.permissionEntries.map(entry => filesystem.stat('/tmp/permission-input/' + entry.path).mode & 0o7777),
                    data: filesystem.readFile('/tmp/permission-input/nested/data.txt', { encoding: 'utf8' }),
                    readonly: filesystem.readFile('/tmp/permission-input/readonly.txt', { encoding: 'utf8' }),
                    executable: filesystem.readFile('/tmp/permission-input/tool.sh', { encoding: 'utf8' })
                  };
                }
                """);
            Assert.AreEqual(mutations, snapshot.GetProperty("mutations").GetInt32());
            Assert.AreSequenceEqual(entries.Select(entry => entry.directory ? 448 : 384),
                snapshot.GetProperty("modes").EnumerateArray().Select(value => value.GetInt32()));
            Assert.AreEqual("seed", snapshot.GetProperty("data").GetString());
            Assert.AreEqual("unchanged", snapshot.GetProperty("readonly").GetString());
            Assert.AreEqual("#!/bin/sh\nexit 0\n", snapshot.GetProperty("executable").GetString());
        }
    }
}
