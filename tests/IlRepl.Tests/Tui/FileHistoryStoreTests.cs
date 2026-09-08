using System.Diagnostics;
using System.Runtime.Versioning;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Tests for <see cref="FileHistoryStore"/>: the file's shape, what a damaged file yields, the
/// lock every read and append takes, and two real processes sharing one file.
/// </summary>
[TestClass]
public sealed class FileHistoryStoreTests
{
    private static readonly object EnvironmentGate = new();

    /// <summary>
    /// The test context, for cancellation.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "ilrepl-tests", Guid.NewGuid().ToString("N"), "history");

    /// <summary>
    /// A multi-line entry comes back as it went in.
    /// </summary>
    [TestMethod]
    public void Format_Parse_RoundTripsMultiLineEntry()
    {
        var record = FileHistoryStore.Format(".method int32 F() {\n  ldc.i4 1\n  ret\n}", new DateTimeOffset(2026, 9, 7, 10, 30, 15, TimeSpan.Zero));
        Assert.AreEqual("\n# 2026-09-07 10:30:15.000000\n+.method int32 F() {\n+  ldc.i4 1\n+  ret\n+}\n", record);
        Assert.AreSequenceEqual([".method int32 F() {\n  ldc.i4 1\n  ret\n}"], FileHistoryStore.Parse(record));
    }

    /// <summary>
    /// A file pgcli wrote parses: timestamps, blank lines between records, plus-prefixed lines.
    /// </summary>
    [TestMethod]
    public void Parse_PgcliFile()
    {
        const string content = "\n# 2025-01-01 09:00:00.000000\n+select 1;\n\n# 2025-01-01 09:00:05.123456\n+select *\n+from t\n+where x = 1;\n\n# 2025-01-01 09:01:00.000000\n+\\d t\n";
        Assert.AreSequenceEqual(["select 1;", "select *\nfrom t\nwhere x = 1;", "\\d t"], FileHistoryStore.Parse(content));
        Assert.IsEmpty(FileHistoryStore.Parse(""));
    }

    /// <summary>
    /// A record cut off before its final newline is dropped; the ones before it are kept.
    /// </summary>
    [TestMethod]
    public void Parse_TruncatedLastRecord_KeepsTheRest()
    {
        var content = FileHistoryStore.Format("one", DateTimeOffset.Now) + FileHistoryStore.Format("two\nlines", DateTimeOffset.Now) + "\n# 2026-09-07 10:30:15.000000\n+thr";
        Assert.AreSequenceEqual(["one", "two\nlines"], FileHistoryStore.Parse(content));
    }

    /// <summary>
    /// A line that is neither a header nor an entry line ends an entry and is otherwise ignored.
    /// </summary>
    [TestMethod]
    public void Parse_DamagedLine_Skipped()
    {
        var content = FileHistoryStore.Format("one", DateTimeOffset.Now) + "garbage\x00here\n" + FileHistoryStore.Format("two", DateTimeOffset.Now);
        Assert.AreSequenceEqual(["one", "two"], FileHistoryStore.Parse(content));
    }

    /// <summary>
    /// A missing file is an empty history, not a problem.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task Load_MissingFile_Empty()
    {
        var store = new FileHistoryStore(TempPath());
        Assert.IsEmpty(await store.LoadAsync(TestContext.CancellationToken));
        Assert.IsNull(store.Problem);
    }

    /// <summary>
    /// An append creates the directory, and a load reads the appends back in order.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task Append_CreatesDirectory_AndLoadReadsBack()
    {
        var path = TempPath();
        var store = new FileHistoryStore(path);
        await store.AppendAsync("ldc.i4 1", TestContext.CancellationToken);
        await store.AppendAsync(".method int32 F() {\n  ret\n}", TestContext.CancellationToken);
        Assert.IsTrue(File.Exists(path));
        Assert.IsNull(store.Problem);
        var entries = await new FileHistoryStore(path).LoadAsync(TestContext.CancellationToken);
        Assert.AreSequenceEqual(["ldc.i4 1", ".method int32 F() {\n  ret\n}"], entries);
    }

    /// <summary>
    /// A load keeps the last thousand entries in memory and leaves the file whole.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task Load_KeepsLastThousandInMemory_FileUntouched()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = new System.Text.StringBuilder();
        for (var i = 0; i < 1005; i++)
        {
            content.Append(FileHistoryStore.Format("entry " + i, DateTimeOffset.Now));
        }

        await File.WriteAllTextAsync(path, content.ToString(), TestContext.CancellationToken);
        var length = new FileInfo(path).Length;
        var entries = await new FileHistoryStore(path).LoadAsync(TestContext.CancellationToken);
        Assert.HasCount(FileHistoryStore.MaxEntries, entries);
        Assert.AreEqual("entry 5", entries[0]);
        Assert.AreEqual("entry 1004", entries[^1]);
        Assert.AreEqual(length, new FileInfo(path).Length);
    }

    /// <summary>
    /// A path that cannot be written sets the problem and throws nothing.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task Append_UnwritablePath_SetsProblem()
    {
        var file = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "a file, not a directory", TestContext.CancellationToken);
        var store = new FileHistoryStore(Path.Combine(file, "history"));
        await store.AppendAsync("ldc.i4 1", TestContext.CancellationToken);
        Assert.IsNotNull(store.Problem);
        Assert.IsEmpty(await store.LoadAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// While another holder has the lock, an append waits; once the lock goes, the record is written whole.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task Append_WhileLockHeld_WaitsThenAppends()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new FileHistoryStore(path, TimeSpan.FromSeconds(10));
        Task append;
        using (new FileStream(store.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1))
        {
            append = store.AppendAsync("waited", TestContext.CancellationToken);
            await Task.Delay(300, TestContext.CancellationToken);
            Assert.IsFalse(append.IsCompleted, "the append must wait for the lock");
            Assert.IsFalse(File.Exists(path));
        }

        await append;
        Assert.IsNull(store.Problem, store.Problem);
        Assert.AreSequenceEqual(["waited"], FileHistoryStore.Parse(await File.ReadAllTextAsync(path, TestContext.CancellationToken)));
    }

    /// <summary>
    /// A lock that never goes away is given up on after the timeout; the entry stays in memory
    /// and the problem says who holds the lock.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task Append_LockNeverReleased_SetsProblemAfterTimeout()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new FileHistoryStore(path, TimeSpan.FromMilliseconds(200));
        using (new FileStream(store.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1))
        {
            var watch = Stopwatch.StartNew();
            await store.AppendAsync("lost", TestContext.CancellationToken);
            Assert.IsGreaterThanOrEqualTo(150, watch.ElapsedMilliseconds);
        }

        Assert.IsNotNull(store.Problem);
        Assert.Contains(store.LockPath, store.Problem);
        Assert.IsFalse(File.Exists(path));
    }

    /// <summary>
    /// A load waits for the lock too, so it never reads a record another session is still writing.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task Load_WhileLockHeld_WaitsForWholeRecord()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new FileHistoryStore(path, TimeSpan.FromSeconds(10));
        await File.WriteAllTextAsync(path, FileHistoryStore.Format("first", DateTimeOffset.Now), TestContext.CancellationToken);
        Task<IReadOnlyList<string>> load;
        using (new FileStream(store.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1))
        {
            load = store.LoadAsync(TestContext.CancellationToken);
            await Task.Delay(200, TestContext.CancellationToken);
            Assert.IsFalse(load.IsCompleted, "the load must wait for the lock");
            await File.AppendAllTextAsync(path, "\n# 2026-09-07 10:30:15.000000\n+second\n", TestContext.CancellationToken);
        }

        Assert.AreSequenceEqual(["first", "second"], await load);
    }

    /// <summary>
    /// Two processes forced to overlap: the child takes the lock and writes a record while
    /// holding it; the parent's append waits for the release, and both records survive whole.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task TwoProcesses_ForcedOverlap_NothingLost()
    {
        var path = TempPath();
        var sentinel = path + ".sentinel";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var child = StartProbe("HoldLock", new Dictionary<string, string>
        {
            [HistoryProbes.Probe] = "hold",
            [HistoryProbes.PathVariable] = path,
            [HistoryProbes.SentinelVariable] = sentinel,
            [HistoryProbes.HoldVariable] = "1500",
        });
        var waited = Stopwatch.StartNew();
        while (!File.Exists(sentinel))
        {
            // The output is read only on failure: reading it earlier would wait for the child to exit.
            if (child.HasExited)
            {
                Assert.Fail("the child exited before taking the lock: " + await child.StandardOutput.ReadToEndAsync(TestContext.CancellationToken));
            }

            Assert.IsLessThan(60_000, waited.ElapsedMilliseconds, "the child never took the lock");
            await Task.Delay(20, TestContext.CancellationToken);
        }

        var store = new FileHistoryStore(path, TimeSpan.FromSeconds(30));
        var append = Stopwatch.StartNew();
        await store.AppendAsync("appended by the parent", TestContext.CancellationToken);
        Assert.IsNull(store.Problem, store.Problem);
        Assert.IsGreaterThanOrEqualTo(500, append.ElapsedMilliseconds, "the parent's append must wait for the child's lock");
        await child.WaitForExitAsync(TestContext.CancellationToken);
        Assert.AreEqual(0, child.ExitCode, await child.StandardOutput.ReadToEndAsync(TestContext.CancellationToken));
        Assert.AreSequenceEqual(["held by the child", "appended by the parent"], FileHistoryStore.Parse(await File.ReadAllTextAsync(path, TestContext.CancellationToken)));
    }

    /// <summary>
    /// Two processes appending at once lose nothing and tear nothing.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task TwoProcesses_ParallelAppends_AllRecordsWhole()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var first = StartProbe("AppendMany", new Dictionary<string, string> { [HistoryProbes.Probe] = "append", [HistoryProbes.PathVariable] = path, [HistoryProbes.CountVariable] = "100", [HistoryProbes.PrefixVariable] = "a" });
        using var second = StartProbe("AppendMany", new Dictionary<string, string> { [HistoryProbes.Probe] = "append", [HistoryProbes.PathVariable] = path, [HistoryProbes.CountVariable] = "100", [HistoryProbes.PrefixVariable] = "b" });
        await first.WaitForExitAsync(TestContext.CancellationToken);
        await second.WaitForExitAsync(TestContext.CancellationToken);
        Assert.AreEqual(0, first.ExitCode, await first.StandardOutput.ReadToEndAsync(TestContext.CancellationToken));
        Assert.AreEqual(0, second.ExitCode, await second.StandardOutput.ReadToEndAsync(TestContext.CancellationToken));
        var entries = FileHistoryStore.Parse(await File.ReadAllTextAsync(path, TestContext.CancellationToken));
        Assert.HasCount(200, entries);
        foreach (var prefix in new[] { "a", "b" })
        {
            var own = entries.Where(e => e.StartsWith(prefix + " ", StringComparison.Ordinal)).ToList();
            Assert.HasCount(100, own);
            for (var i = 0; i < 100; i++)
            {
                Assert.AreEqual($"{prefix} {i}\nsecond line of {prefix} {i}", own[i]);
            }
        }
    }

    /// <summary>
    /// The default path honours a set XDG_CONFIG_HOME and ignores an empty one.
    /// </summary>
    [TestMethod]
    public void DefaultPath_HonoursXdgConfigHome()
    {
        lock (EnvironmentGate)
        {
            var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(Path.GetTempPath(), "xdg"));
                Assert.AreEqual(Path.Combine(Path.GetTempPath(), "xdg", "ilrepl", "history"), FileHistoryStore.DefaultPath());
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "");
                Assert.DoesNotContain("xdg", FileHistoryStore.DefaultPath());
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
                Assert.DoesNotContain("xdg", FileHistoryStore.DefaultPath());
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            }
        }
    }

    /// <summary>
    /// On Windows the default path is under the local application data folder.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public void DefaultPath_Windows_UsesLocalApplicationData()
    {
        lock (EnvironmentGate)
        {
            var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
                Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ilrepl", "history"), FileHistoryStore.DefaultPath());
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            }
        }
    }

    /// <summary>
    /// On Unix the default path is under ~/.config.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public void DefaultPath_Unix_UsesConfigDirectory()
    {
        lock (EnvironmentGate)
        {
            var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
                Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "ilrepl", "history"), FileHistoryStore.DefaultPath());
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            }
        }
    }

    private static Process StartProbe(string probe, Dictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add("FullyQualifiedName~HistoryProbes." + probe);
        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("the probe did not start");
    }

    /// <summary>
    /// The file and its directory are created for the owner alone: every typed line ends up there.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task Append_CreatesFileForTheOwnerOnly()
    {
        var path = TempPath();
        var store = new FileHistoryStore(path);
        await store.AppendAsync("ldstr \"secret\"", CancellationToken.None);
        Assert.IsNull(store.Problem);
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
    }

    /// <summary>
    /// An older file that others could read is tightened before it grows.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task Append_TightensAnExistingFile()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, FileHistoryStore.Format("nop", DateTimeOffset.Now), TestContext.CancellationToken);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var store = new FileHistoryStore(path);
        await store.AppendAsync("ldc.i4 1", CancellationToken.None);
        Assert.IsNull(store.Problem);
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.AreSequenceEqual(["nop", "ldc.i4 1"], await store.LoadAsync(CancellationToken.None));
    }

    /// <summary>
    /// A trailing blank line is written as an empty plus line and read back as part of the entry.
    /// </summary>
    [TestMethod]
    public void Format_Parse_RoundTripsTrailingBlankLine()
    {
        var record = FileHistoryStore.Format("ldc.i4.1\n", new DateTimeOffset(2026, 9, 7, 10, 30, 15, TimeSpan.Zero));
        Assert.AreEqual("\n# 2026-09-07 10:30:15.000000\n+ldc.i4.1\n+\n", record);
        Assert.AreSequenceEqual(["ldc.i4.1\n"], FileHistoryStore.Parse(record));
    }

    /// <summary>
    /// A record a crash cut short is cut away before the file grows, so a later append cannot
    /// finish it and bring it back; the whole records before it stay.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task Append_CutsAnIncompleteTailFirst()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var whole = FileHistoryStore.Format("nop", new DateTimeOffset(2026, 9, 7, 10, 30, 15, TimeSpan.Zero));
        await File.WriteAllTextAsync(path, whole + "\n# 2026-09-07 10:31:00.000000\n+thr", TestContext.CancellationToken);
        var store = new FileHistoryStore(path);
        Assert.AreSequenceEqual(["nop"], await store.LoadAsync(TestContext.CancellationToken), "reading drops the cut record");
        await store.AppendAsync("ldc.i4 1", TestContext.CancellationToken);
        Assert.IsNull(store.Problem);
        Assert.AreSequenceEqual(["nop", "ldc.i4 1"], await store.LoadAsync(TestContext.CancellationToken), "and writing does not bring it back");
        Assert.DoesNotContain("thr", await File.ReadAllTextAsync(path, TestContext.CancellationToken));

        // A file whose only record is cut is emptied before the new one goes in.
        var lone = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(lone)!);
        await File.WriteAllTextAsync(lone, "\n# 2026-09-07 10:31:00.000000\n+thr", TestContext.CancellationToken);
        var loneStore = new FileHistoryStore(lone);
        await loneStore.AppendAsync("ret", TestContext.CancellationToken);
        Assert.AreSequenceEqual(["ret"], await loneStore.LoadAsync(TestContext.CancellationToken));
    }
}
