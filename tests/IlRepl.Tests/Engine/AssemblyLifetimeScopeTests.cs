using System.Runtime.Loader;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Verifies worker assembly lifetime overrides remain local to their asynchronous compilation context.
/// </summary>
[TestClass]
public sealed class AssemblyLifetimeScopeTests
{
    /// <summary>
    /// Supplies cancellation to condition-based concurrent scope coordination.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Nested compilation scopes restore ordinary session loading after both normal and exceptional exits.
    /// </summary>
    [TestMethod]
    public void Scope_NestedAndExceptionalDisposalRestoreSessionLifetime()
    {
        AssertGeneratedLifetime(collectible: true);
        using (new AssemblyLifetimeScope(collectible: false))
        {
            AssertGeneratedLifetime(collectible: false);
            using (new AssemblyLifetimeScope(collectible: true))
            {
                AssertGeneratedLifetime(collectible: true);
            }
            AssertGeneratedLifetime(collectible: false);
            Assert.ThrowsExactly<InvalidOperationException>(LeaveNestedScope);
            AssertGeneratedLifetime(collectible: false);
        }
        AssertGeneratedLifetime(collectible: true);
    }

    /// <summary>
    /// Concurrent asynchronous workers compile different lifetimes without changing the parent or each other.
    /// </summary>
    [TestMethod]
    public async Task Scope_ConcurrentContextsKeepIndependentLifetimes()
    {
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var cancellationToken = TestContext.CancellationToken;
        using (new AssemblyLifetimeScope(collectible: false))
        {
            await Task.WhenAll(CompileAsync(true), CompileAsync(false));
            AssertGeneratedLifetime(collectible: false);
        }
        AssertGeneratedLifetime(collectible: true);

        async Task CompileAsync(bool collectible)
        {
            using var scope = new AssemblyLifetimeScope(collectible);
            if (Interlocked.Increment(ref entered) == 2) bothEntered.SetResult();
            await bothEntered.Task.WaitAsync(cancellationToken);
            await Task.Yield();
            AssertGeneratedLifetime(collectible);
        }
    }

    private static void AssertGeneratedLifetime(bool collectible)
    {
        var session = new Session { DeferActivation = true };
        CompiledCell? cell = null;
        try
        {
            foreach (var line in IlLines.Expand(
                ".class public Owner {", ".method public static int32 Read() { ldc.i4.7; ret }", "}",
                ".method int32 Value() { call int32 Owner::Read(); ret }", "ldc.i4.s 42"))
            {
                session.AddLine(line);
            }
            cell = CellCompiler.CompileForInspection(session);
            var method = Assert.ContainsSingle(session.Methods);
            var type = Assert.ContainsSingle(session.Types).RuntimeType!;
            Assert.AreEqual(collectible, cell.Assembly.IsCollectible);
            Assert.AreEqual(collectible, method.Version.Body.Module.Assembly.IsCollectible);
            Assert.AreEqual(collectible, method.Trampoline.Method.Module.Assembly.IsCollectible);
            Assert.AreEqual(collectible, type.Assembly.IsCollectible);
            Assert.AreEqual(collectible, AssemblyLoadContext.GetLoadContext(cell.Assembly)!.IsCollectible);
            Assert.AreEqual(collectible, AssemblyLoadContext.GetLoadContext(method.Version.Body.Module.Assembly)!.IsCollectible);
            Assert.IsTrue(session.DeferActivation);
        }
        finally
        {
            cell?.Release();
            session.Reset();
            session.Resolver.Dispose();
        }
    }

    private static void LeaveNestedScope()
    {
        using var nested = new AssemblyLifetimeScope(collectible: true);
        AssertGeneratedLifetime(collectible: true);
        throw new InvalidOperationException("leave compilation scope");
    }
}
