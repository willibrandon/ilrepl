using System.Globalization;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Verifies execution-thread identity, ambient state, isolation, and the requested desktop stack size through real IL.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class ExecutionThreadTests
{
    /// <summary>
    /// Supplies cancellation for independent engine operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Successive cells preserve thread-static fields, ThreadLocal values, both cultures, and the user-assigned thread name.
    /// </summary>
    [TestMethod]
    public async Task Cells_PreserveThreadAndAmbientState()
    {
        await using var engine = new InProcessEngine();
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        var name = "ilrepl-test-" + Guid.NewGuid().ToString("N");
        var initial = await SetAsync(engine, 73, name);
        await Task.Yield();
        var observed = await InvokeAsync(engine, "Observe()");
        Assert.AreEqual(initial, observed);
        Assert.Contains("|73|74|fr-FR|ja-JP|" + name, observed);
        Assert.AreSame(culture, CultureInfo.CurrentCulture);
        Assert.AreSame(uiCulture, CultureInfo.CurrentUICulture);
    }

    /// <summary>
    /// Concurrent sessions own different execution threads and cannot inherit each other's thread-local values.
    /// </summary>
    [TestMethod]
    public async Task Engines_IsolateExecutionThreadState()
    {
        await using var first = new InProcessEngine();
        await using var second = new InProcessEngine();
        var values = await Task.WhenAll(SetAsync(first, 11, "first"), SetAsync(second, 29, "second"));
        Assert.AreNotEqual(values[0].Split('|')[0], values[1].Split('|')[0]);
        Assert.AreEqual(values[0], await InvokeAsync(first, "Observe()"));
        Assert.AreEqual(values[1], await InvokeAsync(second, "Observe()"));
        Assert.Contains("|11|12|fr-FR|ja-JP|first", values[0]);
        Assert.Contains("|29|30|fr-FR|ja-JP|second", values[1]);
    }

    /// <summary>
    /// Real non-tail-recursive IL execution can retain more stack than the smaller platform secondary-thread defaults.
    /// </summary>
    [TestMethod]
    public async Task ExecutionStack_AllowsThreeMiBOfLiveFrames()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext)) return;
        await using var engine = new InProcessEngine();
        await LineAsync(engine, "ldc.i4 192");
        await LineAsync(engine, "call int32 IlRepl.Tests.Protocol.ExecutionThreadFixture::Recurse(int32)");
        var reply = await engine.HandleAsync(".run", TestContext.CancellationToken);
        Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(line => line.PlainText)));
        Assert.Contains(line => line.Kind == LineKind.Result && line.PlainText.Contains("18528", StringComparison.Ordinal), reply.Lines);
    }

    private async Task<string> SetAsync(InProcessEngine engine, int value, string name)
    {
        await LineAsync(engine, "ldc.i4 " + value.ToString(CultureInfo.InvariantCulture));
        await LineAsync(engine, "ldstr \"" + name + "\"");
        return await InvokeAsync(engine, "Set(int32, string)");
    }

    private async Task<string> InvokeAsync(InProcessEngine engine, string method)
    {
        await LineAsync(engine, "call string IlRepl.Tests.Protocol.ExecutionThreadFixture::" + method);
        var reply = await engine.HandleAsync(".run", TestContext.CancellationToken);
        Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(line => line.PlainText)));
        return reply.Lines.Single(line => line.Kind == LineKind.Result).PlainText;
    }

    private async Task LineAsync(InProcessEngine engine, string line)
    {
        var reply = await engine.HandleAsync(line, TestContext.CancellationToken);
        Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(item => item.PlainText)));
    }
}
