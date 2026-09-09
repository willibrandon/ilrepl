using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Tui;

/// <summary>
/// An idle prompt refreshes ambiguous type spellings after background loads through either engine transport.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AssemblyCompletionTests
{
    /// <summary>
    /// Supplies cancellation for engine and background-load waits.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A newly colliding type invalidates cached rows without an edit or session mutation, and fresh rows execute.
    /// </summary>
    /// <param name="remote">Whether the host process owns the assemblies.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackgroundLoad_RequalifiesIdleCandidates(bool remote)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var directory = Directory.CreateTempSubdirectory("ilrepl-background-assemblies-").FullName;
        var name = "IdleType" + Guid.NewGuid().ToString("N");
        var first = WriteFixture(directory, "First", name, 1);
        var second = WriteFixture(directory, "Second", name, 2);
        var release = Path.Combine(directory, "release");
        await using var engine = remote ? await HostPaths.StartEngineAsync(ct) : (IReplEngine)new InProcessEngine();
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary))
        {
            Requester = new CompletionRequester(engine),
        };
        try
        {
            foreach (var line in new[] { ".load " + first, ".load " + SampleHost.Samples.GreeterDll,
                "ldstr " + Quote(second), "ldstr " + Quote(release),
                "call Greeter.BackgroundAssemblyLoader::LoadAsync(string, string)", "pop", "ret" })
            {
                Assert.IsTrue((await engine.HandleAsync(line, ct)).Succeeded, line);
            }

            state.SetText("call " + name + "::Val", name.Length + 10);
            await UntilAsync(() => Rows(state, engine).Count == 1, ct);
            var original = state.Completions!;
            var oldItem = original.Reply.Items.Single();
            Assert.AreEqual(name + "::Value()", oldItem.InsertText);
            Assert.IsNotNull(CompletionEdit.For(state, oldItem));
            var status = engine.Status;
            var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Invalidate = () => invalidated.TrySetResult();
            await File.WriteAllTextAsync(release, "load", ct);
            await UntilAsync(() => File.Exists(release + ".loaded"), ct);
            await invalidated.Task.WaitAsync(ct);
            await UntilAsync(() => engine.AssemblyVersion != original.Reply.AssemblyVersion, ct);
            Assert.AreEqual(status, engine.Status);
            Assert.IsFalse(state.Requester.Matches(state, original));
            Assert.IsNull(CompletionEdit.For(state, oldItem));
            await UntilAsync(() => Rows(state, engine).Count == 2, ct);
            var current = state.Completions!;
            Assert.AreEqual(original.Key.Document, current.Key.Document);
            Assert.DoesNotContain(item => item.InsertText == oldItem.InsertText, current.Reply.Items);
            foreach (var (ns, value) in new[] { ("First", 1), ("Second", 2) })
            {
                var chosen = current.Reply.Items.Single(item => item.InsertText.Contains(ns + ".", StringComparison.Ordinal));
                Assert.IsTrue((await engine.HandleAsync(".clear", ct)).Succeeded);
                Assert.IsTrue((await engine.HandleAsync("call " + chosen.InsertText, ct)).Succeeded);
                var run = await engine.HandleAsync("ret", ct);
                Assert.IsTrue(run.Succeeded);
                Assert.Contains(line => line.PlainText.Contains($"= {value} : int32", StringComparison.Ordinal), run.Lines);
            }
        }
        finally
        {
            File.WriteAllText(release, "release on failure");
            await state.Requester.SettleAsync(TimeSpan.FromSeconds(5));
            // Default-context assemblies remain mapped on Windows until the test process exits.
            if (!OperatingSystem.IsWindows())
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// A pending assembly notification neither blocks normal input nor survives cancellation.
    /// </summary>
    /// <param name="remote">Whether the host process serves the notification.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Watch_Cancellation_LeavesInputUsable(bool remote)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = remote ? await HostPaths.StartEngineAsync(ct) : (IReplEngine)new InProcessEngine();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watching = engine.WaitForAssembliesAsync(engine.AssemblyVersion, cancellation.Token);
        Assert.IsTrue((await engine.HandleAsync("nop", ct)).Succeeded);
        while (watching.IsCompletedSuccessfully)
        {
            watching = engine.WaitForAssembliesAsync(await watching, cancellation.Token);
        }

        Assert.IsTrue((await engine.HandleAsync("nop", ct)).Succeeded);
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => watching.WaitAsync(TimeSpan.FromSeconds(3), ct));
        Assert.IsTrue((await engine.HandleAsync(".clear", ct)).Succeeded);
    }

    private static IReadOnlyList<CompletionItem> Rows(PromptState state, IReplEngine engine)
    {
        while (state.Events.TryDequeue(out var message))
        {
            if (message.CompletionResult is { } result)
            {
                state.Requester!.Apply(state, result);
            }
        }

        state.Requester!.Refresh(state);
        return PromptWidget.Candidates(state, engine.Catalog);
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(1, cancellationToken);
        }
    }

    private static string WriteFixture(string directory, string ns, string name, int value)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(ns + name, new Version(1, 0)),
            ns + name, ModuleKind.Dll);
        var module = assembly.MainModule;
        var type = new TypeDefinition(ns, name, TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(type);
        var method = new MethodDefinition("Value", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, value));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        var path = Path.Combine(directory, ns + name + ".dll");
        assembly.Write(path);
        return path;
    }

    private static string Quote(string path) => "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"";
}
