using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Tests that only run as a child process of <see cref="FileHistoryStoreTests"/>, driven by
/// environment variables, so two real processes can be made to overlap on the history file.
/// Without the variables they are inconclusive.
/// </summary>
[TestClass]
public sealed class HistoryProbes
{
    /// <summary>
    /// The variable that selects a probe.
    /// </summary>
    public const string Probe = "ILREPL_HISTORY_PROBE";

    /// <summary>
    /// The history file the probe works on.
    /// </summary>
    public const string PathVariable = "ILREPL_HISTORY_PATH";

    /// <summary>
    /// The file the holder creates once it has the lock.
    /// </summary>
    public const string SentinelVariable = "ILREPL_HISTORY_SENTINEL";

    /// <summary>
    /// How long the holder keeps the lock, in milliseconds.
    /// </summary>
    public const string HoldVariable = "ILREPL_HISTORY_HOLD_MS";

    /// <summary>
    /// How many entries the appender writes, and the prefix it gives them.
    /// </summary>
    public const string CountVariable = "ILREPL_HISTORY_COUNT";

    /// <summary>
    /// The prefix the appender gives its entries.
    /// </summary>
    public const string PrefixVariable = "ILREPL_HISTORY_PREFIX";

    /// <summary>
    /// Takes the lock, writes a record straight into the file as a writer in the middle of an
    /// append would, signals the parent, keeps the lock for a while, then lets go.
    /// </summary>
    [TestMethod]
    public void HoldLock()
    {
        TestSkip.Unless(Environment.GetEnvironmentVariable(Probe) == "hold", "runs as a child of FileHistoryStoreTests");
        var path = Environment.GetEnvironmentVariable(PathVariable)!;
        var sentinel = Environment.GetEnvironmentVariable(SentinelVariable)!;
        var hold = int.Parse(Environment.GetEnvironmentVariable(HoldVariable)!, System.Globalization.CultureInfo.InvariantCulture);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1))
        {
            File.AppendAllText(path, FileHistoryStore.Format("held by the child", DateTimeOffset.Now));
            File.WriteAllText(sentinel, "held");
            Thread.Sleep(hold);
        }
    }

    /// <summary>
    /// Appends a run of entries through the store, as a session would.
    /// </summary>
    /// <returns>A task that completes when the entries are written.</returns>
    [TestMethod]
    public async Task AppendMany()
    {
        TestSkip.Unless(Environment.GetEnvironmentVariable(Probe) == "append", "runs as a child of FileHistoryStoreTests");
        var path = Environment.GetEnvironmentVariable(PathVariable)!;
        var count = int.Parse(Environment.GetEnvironmentVariable(CountVariable)!, System.Globalization.CultureInfo.InvariantCulture);
        var prefix = Environment.GetEnvironmentVariable(PrefixVariable)!;
        var store = new FileHistoryStore(path, TimeSpan.FromSeconds(30));
        for (var i = 0; i < count; i++)
        {
            await store.AppendAsync($"{prefix} {i}\nsecond line of {prefix} {i}", CancellationToken.None);
        }

        Assert.IsNull(store.Problem, store.Problem);
    }
}
