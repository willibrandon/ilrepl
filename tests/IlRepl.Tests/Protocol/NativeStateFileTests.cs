using System.Text.Json;
using IlRepl.Host;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Verifies worker snapshots remain atomic while a supervisor retains a real open reader.
/// </summary>
[TestClass]
public sealed class NativeStateFileTests
{
    /// <summary>
    /// Supplies cancellation to concurrent snapshot publication and reading.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// An open snapshot permits replacement on Windows and continues reading its original complete generation.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Write_OpenReaderAllowsAtomicReplacementAndRetainsOldSnapshot()
    {
        using var files = new SessionWorkspaceFixture();
        var path = Path.Combine(files.DirectoryPath, "state.json");
        await NativeStateFile.WriteAsync(files.DirectoryPath, new NativeWorkerState
        {
            MethodId = 11, Report = new NativeReport { Name = "before", Invocations = 1 },
        });
        await using var original = NativeStateFile.OpenRead(path);

        await Task.Run(() => NativeStateFile.WriteAsync(files.DirectoryPath, new NativeWorkerState
        {
            MethodId = 22, Report = new NativeReport { Name = "after", Invocations = 2 },
        }), TestContext.CancellationToken);

        var retained = await JsonSerializer.DeserializeAsync(original, ProtocolJsonContext.Default.NativeWorkerState,
            TestContext.CancellationToken);
        var published = await NativeStateFile.ReadAsync(path, TestContext.CancellationToken);
        Assert.IsNotNull(retained);
        Assert.IsNotNull(published);
        Assert.AreEqual(11UL, retained.MethodId);
        Assert.AreEqual("before", retained.Report.Name);
        Assert.AreEqual(1, retained.Report.Invocations);
        Assert.AreEqual(22UL, published.MethodId);
        Assert.AreEqual("after", published.Report.Name);
        Assert.AreEqual(2, published.Report.Invocations);
        Assert.IsFalse(File.Exists(path + ".tmp"));
    }
}
