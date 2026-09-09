using System.Diagnostics;
using System.Runtime.CompilerServices;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Verifies bounded preview ownership and allocation while repeatedly replacing unsent generic declarations.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CompletionLifetimeTests
{
    /// <summary>
    /// Supplies cancellation and measurement output.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A thousand generic edits preserve completion without creating runtime assemblies or retaining past documents.
    /// </summary>
    [TestMethod]
    public async Task Completion_ThousandGenericEdits_StaySymbolicAndBounded()
    {
        var session = new Session();
        using var completer = new OperandCompleter(session);
        string[] lines = [".class public Box<T> {", ".field public !0 value0", "}", "ldtoken Box"];
        var request = new CompletionRequest(lines, 3, lines[3].Length, null, []);
        await completer.CompleteAsync(request, TestContext.CancellationToken);
        var loaded = new List<string>();
        var events = new Lock();
        var completing = new AsyncLocal<bool> { Value = true };
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var watch = Stopwatch.StartNew();
        AppDomain.CurrentDomain.AssemblyLoad += Record;
        try
        {
            for (var edit = 1; edit <= 1000; edit++)
            {
                lines[1] = ".field public !0 value" + edit;
                var reply = await completer.CompleteAsync(request, TestContext.CancellationToken);
                Assert.Contains(item => item.InsertText == "Box<", reply.Items, $"construction after edit {edit}");
                Assert.Contains(item => item.InsertText == "Box`1", reply.Items, $"open definition after edit {edit}");
            }
        }
        finally
        {
            completing.Value = false;
            AppDomain.CurrentDomain.AssemblyLoad -= Record;
        }

        var retained = GC.GetTotalMemory(forceFullCollection: true) - baseline;
        Assert.IsEmpty(loaded, "Preview must not load or create an assembly: " + string.Join(", ", loaded));
        Assert.IsLessThan(20_000_000L, retained);
        Assert.IsEmpty(session.Types);
        Assert.AreEqual(0, session.Submissions);
        TestContext.WriteLine($"1000 generic edits: {watch.Elapsed.TotalMilliseconds:F0} ms, {retained:N0} retained bytes");
        foreach (var line in lines.Take(3))
        {
            session.AddLine(line);
        }

        var defined = session.Types.Single().RuntimeType!;
        Assert.IsNotNull(defined.GetField("value1000"));
        Assert.AreEqual(typeof(string), defined.MakeGenericType(typeof(string)).GetField("value1000")!.FieldType);

        void Record(object? sender, AssemblyLoadEventArgs args)
        {
            if (!completing.Value)
            {
                TestContext.WriteLine("Background load outside completion: " + args.LoadedAssembly.FullName
                    + "\n" + Environment.StackTrace);
                return;
            }

            lock (events)
            {
                loaded.Add(args.LoadedAssembly.FullName ?? "unknown");
            }
        }
    }

    /// <summary>
    /// Reset drops an idle preview's collectible references without needing another completion request.
    /// </summary>
    [TestMethod]
    public async Task Completion_Reset_ReleasesIdleSnapshotImmediately()
    {
        var session = new Session();
        using var completer = new OperandCompleter(session);
        var weak = await CompleteAndResetAsync(session, completer, TestContext.CancellationToken);
        for (var attempt = 0; attempt < 15 && weak.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(10, TestContext.CancellationToken);
        }

        Assert.IsFalse(weak.IsAlive, "A reset must release the last completed preview even if no later query is made.");
        GC.KeepAlive(session);
        GC.KeepAlive(completer);
    }

    /// <summary>
    /// Two hundred unsent lines replay within the interactive allocation and latency budgets.
    /// </summary>
    [TestMethod]
    public void Speculation_TwoHundredLines_StaysWithinBudget()
    {
        using var editing = new EditingSession(new Session());
        string[] lines = [".method void Long() {", .. Enumerable.Repeat("nop", 198), "}"];
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        var view = editing.Speculate(lines, lines.Length, cancellationToken: TestContext.CancellationToken);
        var elapsed = watch.Elapsed;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.IsEmpty(view.SkippedLines);
        Assert.IsLessThan(TimeSpan.FromMilliseconds(150), elapsed);
        Assert.IsLessThan(20_000_000L, allocated);
        TestContext.WriteLine($"200 lines: {elapsed.TotalMilliseconds:F1} ms, {allocated:N0} allocated bytes");
    }

    /// <summary>
    /// Discovering a type and its constructors never initializes the type or executes a constructor body.
    /// </summary>
    [TestMethod]
    public async Task Completion_InitializerAndConstructor_AreNeverExecuted()
    {
        var key = "ILREPL_COMPLETION_" + Guid.NewGuid().ToString("N");
        var session = new Session();
        using var completer = new OperandCompleter(session);
        string[] lines = [".class public CompletionSideEffect {",
            ".method private static void .cctor() {", $"ldstr \"{key}\"", "ldstr \"initialized\"",
            "call Environment::SetEnvironmentVariable(string, string)", "ret", "}",
            ".method public instance void .ctor() {", "ldarg.0", "call instance void Object::.ctor()",
            $"ldstr \"{key}\"", "ldstr \"constructed\"", "call Environment::SetEnvironmentVariable(string, string)",
            "ret", "}", "}", "newobj CompletionSideEffect::"];
        try
        {
            var reply = await completer.CompleteAsync(new CompletionRequest(lines, lines.Length - 1, lines[^1].Length, null, []),
                TestContext.CancellationToken);
            Assert.HasCount(1, reply.Items);
            Assert.IsNull(Environment.GetEnvironmentVariable(key));
            foreach (var line in lines.Take(lines.Length - 1))
            {
                session.AddLine(line);
            }

            var accepted = await completer.CompleteAsync(
                new CompletionRequest([lines[^1]], 0, lines[^1].Length, null, []), TestContext.CancellationToken);
            Assert.HasCount(1, accepted.Items);
            Assert.IsNull(Environment.GetEnvironmentVariable(key));
            session.AddLine("newobj " + accepted.Items[0].InsertText);
            Assert.IsNull(Environment.GetEnvironmentVariable(key));
            session.Run();
            Assert.AreEqual("constructed", Environment.GetEnvironmentVariable(key));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
            session.Reset();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> CompleteAndResetAsync(
        Session session, OperandCompleter completer, CancellationToken cancellationToken)
    {
        session.AddLine(".class public Kept { }");
        var weak = new WeakReference(session.Types.Single().Definition!.Assembly);
        const string line = "ldtoken Kep";
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, line.Length, null, []), cancellationToken);
        Assert.AreEqual("Kept", reply.Items[0].InsertText);
        session.Reset();
        return weak;
    }
}
