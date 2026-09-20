using System.Globalization;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Runs history probes requested by a parent test process.
/// </summary>
internal static class HistoryProbes
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
    /// Runs the selected history probe when one was requested.
    /// </summary>
    /// <returns>Whether a probe was requested.</returns>
    public static async Task<bool> TryRunAsync()
    {
        switch (Environment.GetEnvironmentVariable(Probe))
        {
            case "hold":
                HoldLock();
                return true;
            case "append":
                await AppendMany();
                return true;
            case "path":
                Console.Out.WriteLine(FileHistoryStore.DefaultPath());
                return true;
            default:
                return false;
        }
    }

    private static void HoldLock()
    {
        var path = Environment.GetEnvironmentVariable(PathVariable)!;
        var sentinel = Environment.GetEnvironmentVariable(SentinelVariable)!;
        var hold = int.Parse(Environment.GetEnvironmentVariable(HoldVariable)!, CultureInfo.InvariantCulture);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1))
        {
            File.AppendAllText(path, FileHistoryStore.Format("held by the child", DateTimeOffset.Now));
            File.WriteAllText(sentinel, "held");
            Thread.Sleep(hold);
        }
    }

    private static async Task AppendMany()
    {
        var path = Environment.GetEnvironmentVariable(PathVariable)!;
        var count = int.Parse(Environment.GetEnvironmentVariable(CountVariable)!, CultureInfo.InvariantCulture);
        var prefix = Environment.GetEnvironmentVariable(PrefixVariable)!;
        var store = new FileHistoryStore(path, TimeSpan.FromSeconds(30));
        for (var i = 0; i < count; i++)
        {
            await store.AppendAsync($"{prefix} {i}\nsecond line of {prefix} {i}", CancellationToken.None);
        }

        Assert.IsNull(store.Problem, store.Problem);
    }
}
