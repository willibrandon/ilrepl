using IlRepl.Engine;
using IlRepl.Repl;
using IlRepl.Tests.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Initial method publication preserves delegate identity, active callers, and later transactional replacements.
/// </summary>
[TestClass]
public sealed class InitialMethodBindingTests
{
    /// <summary>
    /// Supplies cancellation to real user-code synchronization.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A wrong delegate cannot overwrite the first implementation even when its callable signature is identical.
    /// </summary>
    [TestMethod]
    public void InitialBinding_RejectsWrongDelegateWithoutChangingThePublishedBody()
    {
        using var core = new ReplCore();
        var session = core.Session;
        session.DeferActivation = true;
        Submit(session, ".method int32 First() { ldc.i4.s 42; ret }", ".method int32 Second() { ldc.i4.s 43; ret }");
        var first = session.Methods.Single(method => method.Signature.Name == "First");
        var second = session.Methods.Single(method => method.Signature.Name == "Second");
        first.Trampoline.BindInitial(first.Version.Implementation);
        var retained = first.Trampoline.Method.CreateDelegate<Func<int>>();
        Assert.AreEqual(42, retained());

        Assert.ThrowsExactly<InvalidCastException>(() => first.Trampoline.BindInitial(second.Version.Implementation));
        Assert.ThrowsExactly<ArgumentNullException>(() => first.Trampoline.BindInitial(null!));
        Assert.AreEqual(42, retained());
        Assert.IsTrue(session.DeferActivation);

        session.Activate();
        Assert.IsFalse(session.DeferActivation);
        Assert.AreEqual(42, retained());
        Assert.AreEqual(43, second.Trampoline.Method.CreateDelegate<Func<int>>()());
    }

    /// <summary>
    /// A real call finishes its initially published body while retained callers observe a validated replacement and reset.
    /// </summary>
    [TestMethod]
    public async Task InitialBinding_RetainedCallSurvivesRejectedAndCommittedReplacements()
    {
        using var core = new ReplCore();
        using var permit = new ExecutionPermit();
        var name = ExecutionThreadFixture.Register(permit);
        Task<int>? running = null;
        try
        {
            var session = core.Session;
            session.DeferActivation = true;
            Submit(session, ".method int32 Read() {", "ldstr \"" + name + "\"",
                "call int32 IlRepl.Tests.Protocol.ExecutionThreadFixture::Wait(string)", "ret", "}");
            session.Activate();
            var original = Assert.ContainsSingle(session.Methods);
            var retained = original.Trampoline.Method.CreateDelegate<Func<int>>();
            var originalBody = original.Version.Implementation;
            running = Task.Run(retained, TestContext.CancellationToken);
            await permit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

            Submit(session, ".method int32 Read() {", "ldstr \"wrong return type\"");
            var failure = Assert.ThrowsExactly<ReplException>(() => session.AddLine("ret"));
            Assert.Contains("int32", failure.Message);
            Assert.AreSame(original, Assert.ContainsSingle(session.Methods));
            Assert.IsTrue(session.AbandonMethod());
            Assert.IsFalse(running.IsCompleted);

            Submit(session, ".method int32 Read() {", "ldnull", "no. 1", "castclass object", "pop", "ldc.i4.s 99", "ret");
            var runtimeFailure = Assert.ThrowsExactly<ReplException>(() => session.AddLine("}"));
            Assert.Contains("the JIT rejected method Read", runtimeFailure.Message);
            Assert.AreSame(original, Assert.ContainsSingle(session.Methods));
            Assert.IsTrue(session.AbandonMethod());
            Assert.IsFalse(running.IsCompleted);

            Submit(session, ".method int32 Read() { ldc.i4.s 43; ret }");
            Assert.AreSame(original.Trampoline, Assert.ContainsSingle(session.Methods).Trampoline);
            Assert.AreEqual(43, retained());
            Assert.IsFalse(running.IsCompleted);
            permit.Release();
            Assert.AreEqual(42, await running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken));
            Assert.AreEqual(42, originalBody.DynamicInvoke());

            session.Reset();
            Assert.IsEmpty(session.Methods);
            Assert.AreEqual(43, retained());
            Assert.AreEqual(42, originalBody.DynamicInvoke());
        }
        finally
        {
            permit.Release();
            if (running is not null)
            {
                await running.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            }
            ExecutionThreadFixture.Unregister(name);
        }
    }

    private static void Submit(Session session, params string[] source)
    {
        foreach (var line in IlLines.Expand(source))
        {
            session.AddLine(line);
        }
    }
}
