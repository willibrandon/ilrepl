using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for how session methods keep their identity: every caller binds to a trampoline, a
/// compatible redefinition rebinds it in one store, and a version lives as long as something
/// can still reach it.
/// </summary>
[TestClass]
public sealed class MethodVersionTests
{
    /// <summary>
    /// The test context, set by the runner.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    private static Session Load(params string[] lines)
    {
        var session = new Session();
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        return session;
    }

    /// <summary>
    /// A delegate taken over a session method keeps working, and sees the new body, after a
    /// compatible redefinition.
    /// </summary>
    [TestMethod]
    public void Run_RetainedDelegate_SeesCompatibleRedefinition()
    {
        var session = Load(".method int32 F(int32 n) {", "ldarg n", "ldc.i4 2", "mul", "ret", "}");
        session.AddLine("ldnull");
        session.AddLine("ldftn int32 F(int32)");
        session.AddLine("newobj instance void class Func`2<int32, int32>::.ctor(object, native int)");
        var retained = (Func<int, int>)session.Run().Value!;
        Assert.AreEqual(10, retained(5));

        foreach (var line in new[] { ".method int32 F(int32 n) {", "ldarg n", "ldc.i4 3", "mul", "ret", "}" })
        {
            session.AddLine(line);
        }

        Assert.AreEqual(15, retained(5), "the delegate binds the trampoline, which now forwards to the new version");
        Assert.HasCount(1, session.Methods);
    }

    /// <summary>
    /// A method that calls another sees the callee's new body without being recompiled.
    /// </summary>
    [TestMethod]
    public void Run_Caller_SeesCalleeRedefinition()
    {
        var session = Load(
            ".method int32 Two() {", "ldc.i4 2", "ret", "}",
            ".method int32 Twice() {", "call int32 Two()", "ldc.i4 2", "mul", "ret", "}");
        var callerVersion = session.Methods[1].Version;
        session.AddLine("call int32 Twice()");
        Assert.AreEqual(4, session.Run().Value);

        foreach (var line in new[] { ".method int32 Two() {", "ldc.i4 5", "ret", "}" })
        {
            session.AddLine(line);
        }

        session.AddLine("call int32 Twice()");
        Assert.AreEqual(10, session.Run().Value);
        Assert.AreSame(callerVersion, session.Methods[1].Version, "the caller was not recompiled");
    }

    /// <summary>
    /// A signature change with no callers gives the method a new identity; the old trampoline
    /// is no longer the one the session binds.
    /// </summary>
    [TestMethod]
    public void AddLine_SignatureChange_IsANewIdentity()
    {
        var session = Load(".method int32 F() {", "ldc.i4 1", "ret", "}");
        var first = session.Methods[0].Trampoline;
        foreach (var line in new[] { ".method int64 F() {", "ldc.i8 2", "ret", "}" })
        {
            session.AddLine(line);
        }

        Assert.AreNotSame(first, session.Methods[0].Trampoline);
        Assert.AreEqual(typeof(long), session.Methods[0].Trampoline.Method.ReturnType);
        session.AddLine("call int64 F()");
        Assert.AreEqual(2L, session.Run().Value);
    }

    /// <summary>
    /// A call already inside the old version finishes there while a rebind happens, and the
    /// next call takes the new version.
    /// </summary>
    [TestMethod]
    public void Run_CallInFlight_FinishesOnItsVersion()
    {
        var ct = TestContext.CancellationToken;
        var session = Load(
            ".method int32 Slow(int32 n) {",
            "call void [IlRepl.Tests]IlRepl.Tests.Engine.MethodVersionTests::Pause()",
            "ldarg n", "ldc.i4 1", "mul", "ret", "}");
        var method = session.Methods[0].Trampoline.Method;
        var call = method.CreateDelegate<Func<int, int>>();
        s_entered.Reset();
        s_resume.Reset();
        var inFlight = Task.Run(() => call(6), ct);
        Assert.IsTrue(s_entered.Wait(TimeSpan.FromSeconds(30), ct), "the call should have entered the old version");

        foreach (var line in new[] { ".method int32 Slow(int32 n) {", "ldarg n", "ldc.i4 100", "mul", "ret", "}" })
        {
            session.AddLine(line);
        }

        s_resume.Set();
        Assert.AreEqual(6, inFlight.GetAwaiter().GetResult(), "the call in flight finishes on the version it entered");
        Assert.AreEqual(600, call(6), "a new call takes the new version");
    }

    /// <summary>
    /// After reset, a delegate a user kept still runs its last version.
    /// </summary>
    [TestMethod]
    public void Reset_RetainedDelegate_KeepsWorking()
    {
        var session = Load(".method int32 Seven() {", "ldc.i4 7", "ret", "}");
        var retained = session.Methods[0].Trampoline.Method.CreateDelegate<Func<int>>();
        session.Reset();
        Assert.IsEmpty(session.Methods);
        Assert.AreEqual(7, retained());
    }

    /// <summary>
    /// The version's assembly and the trampoline's are session assemblies, and a version depends
    /// on the trampolines its body calls.
    /// </summary>
    [TestMethod]
    public void Methods_LiveInSessionAssemblies()
    {
        var session = Load(".method int32 One() {", "ldc.i4 1", "ret", "}", ".method int32 Two() {", "call int32 One()", "dup", "add", "ret", "}");
        var one = session.Methods[0];
        var two = session.Methods[1];
        Assert.IsTrue(SessionAssemblies.IsSessionAssembly(one.Version.Body.DeclaringType!.Assembly));
        Assert.IsTrue(SessionAssemblies.IsSessionAssembly(one.Trampoline.Method.DeclaringType!.Assembly));
        Assert.AreEqual("IlRepl.Cell", one.Trampoline.Method.DeclaringType!.FullName);
        Assert.IsEmpty(one.Version.Definition.Dependencies);
        Assert.Contains(one.Trampoline.Definition, two.Version.Definition.Dependencies, "a version depends on the trampolines it calls");
    }

    private static readonly ManualResetEventSlim s_entered = new(false);
    private static readonly ManualResetEventSlim s_resume = new(false);

    /// <summary>
    /// Called from a session method body: signals entry and waits to be resumed.
    /// </summary>
    public static void Pause()
    {
        s_entered.Set();
        s_resume.Wait(TimeSpan.FromSeconds(30));
    }
}
