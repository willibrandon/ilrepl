using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Jump eligibility compares calling conventions and exact parameter shapes in the editing context.
/// </summary>
[TestClass]
public sealed class JumpEligibilityTests
{
    /// <summary>
    /// Supplies cancellation for speculative replay.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Equal CLI signatures qualify while differing conventions, modifiers, array kinds and pointer shapes do not.
    /// </summary>
    /// <param name="source">The enclosing method header.</param>
    /// <param name="target">The target header.</param>
    /// <param name="eligible">Whether the signature matches.</param>
    [TestMethod]
    [DataRow("static int32 Bridge(int32 value)", "static int32 Target(int32 other)", true)]
    [DataRow("static int32 Bridge(int32 value)", "static void Target(int32 value)", false)]
    [DataRow("static int32 Bridge(int32 value)", "static int32 Target()", false)]
    [DataRow("static int32 Bridge(int32 value)", "static int32 Target(float32 value)", false)]
    [DataRow("static int32 Bridge(int32 x, string y)", "static int32 Target(string x, int32 y)", false)]
    [DataRow("static int32 Bridge(int32 value)", "instance int32 Target(int32 value)", false)]
    [DataRow("instance int32 Bridge(int32 value)", "static int32 Target(int32 value)", false)]
    [DataRow("instance int32 Bridge(int32 value)", "instance int32 Target(int32 other)", true)]
    [DataRow("static int32 Bridge(int32 value)", "static vararg int32 Target(int32 value)", false)]
    [DataRow("static int32 Bridge(int32[] value)", "static int32 Target(int32[0...] value)", false)]
    [DataRow("static int32 Bridge(int32[1...3] value)", "static int32 Target(int32[2...4] value)", false)]
    [DataRow("static int32 Bridge(int32& value)", "static int32 Target(int32* value)", false)]
    [DataRow("static int32 Bridge(method int32 *(int32) value)", "static int32 Target(method void *(int32) value)", false)]
    [DataRow("static int32 Bridge(int32 value)",
        "static int32 Target(int32 modopt(System.Runtime.CompilerServices.IsReadOnlyAttribute) value)", false)]
    [DataRow("static int32 Bridge(int32 value)",
        "static int32 modreq(System.Runtime.CompilerServices.IsReadOnlyAttribute) Target(int32 value)", false)]
    [DataRow("static int32 Bridge(int32 value)", "static int32 Target<T>(int32 value)", false)]
    [DataRow("static !!0 Bridge<T>(!!0 value)", "static !!0 Target<U>(!!0 value)", true)]
    [DataRow("static int32 Bridge<T>(int32 value)", "static !!0 Target<U>(!!0 value)", true)]
    [DataRow("static int32[] Bridge<T>(int32[] value)", "static !!0[] Target<U>(!!0[] value)", true)]
    [DataRow("static int32 Bridge<T>(string value)", "static !!0 Target<U>(!!0 value)", false)]
    [DataRow("static !!0 Bridge<T>(!!0 value)", "static !!1 Target<T, U>(!!1 value)", false)]
    public void Jump_RequiresEnclosingSignature(string source, string target, bool eligible)
    {
        using var editing = new EditingSession(new Session());
        var lines = new[] { ".class public JumpHost {", ".method public " + target + " {", "ldnull", "throw", "}",
            ".method public " + source + " {", "" };
        var view = editing.Speculate(lines, lines.Length - 1, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines);
        var method = view.Scope.Methods(view.Owner!, "Target").Single();
        Assert.AreEqual(eligible, MemberEligibility.Admits(method, CompletionSite.None with { Owner = "jmp" }, view));
    }

    /// <summary>
    /// Instance jumps can transfer this to an inherited method but cannot reinterpret it as an unrelated receiver.
    /// </summary>
    /// <param name="baseType">The enclosing class's base.</param>
    /// <param name="eligible">Whether this is a Random instance.</param>
    [TestMethod]
    [DataRow("Random", true)]
    [DataRow("Object", false)]
    public void Jump_ChecksImplicitReceiver(string baseType, bool eligible)
    {
        using var editing = new EditingSession(new Session());
        var lines = new[] { ".class public JumpHost extends " + baseType + " {",
            ".method public instance int32 Bridge(int32 value) {", "" };
        var view = editing.Speculate(lines, 2, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines);
        var method = RuntimeSymbolImporter.Import(typeof(Random).GetMethod(nameof(Random.Next), [typeof(int)])!);
        Assert.AreEqual(eligible, MemberEligibility.Admits(method, CompletionSite.None with { Owner = "jmp" }, view));
    }
}
