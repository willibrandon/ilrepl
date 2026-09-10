using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Completion eligibility respects opcode, accessibility and receiver rules without requiring a ready-to-run stack.
/// </summary>
[TestClass]
public sealed class CompletionEligibilityTests
{
    /// <summary>
    /// The runner's cancellation and reporting context.
    /// </summary>
    public required TestContext TestContext { get; set; }

    /// <summary>
    /// Call and pointer instructions expose the method kinds permitted by their CLI contracts.
    /// </summary>
    [TestMethod]
    [DataRow("call", true, true, false, true)]
    [DataRow("callvirt", false, true, true, true)]
    [DataRow("ldftn", true, true, false, true)]
    [DataRow("jmp", false, false, false, false)]
    [DataRow("ldvirtftn", false, false, true, true)]
    [DataRow("newobj", false, false, false, false)]
    [DataRow("ldtoken method", true, true, true, true)]
    public void Methods_FollowTheOpcode(string opcode, bool staticMethod, bool instanceMethod, bool abstractMethod, bool virtualMethod)
    {
        using var editing = new EditingSession(new Session());
        var view = editing.Speculate([], 0, cancellationToken: TestContext.CancellationToken);
        var site = CompletionSite.None with { Owner = opcode };
        var methods = new[]
        {
            typeof(Console).GetMethod(nameof(Console.WriteLine), [typeof(string)])!,
            typeof(string).GetMethod(nameof(string.Substring), [typeof(int)])!,
            typeof(Stream).GetMethod(nameof(Stream.Read), [typeof(byte[]), typeof(int), typeof(int)])!,
            typeof(object).GetMethod(nameof(ToString))!,
        };
        bool[] expected = [staticMethod, instanceMethod, abstractMethod, virtualMethod];
        for (var i = 0; i < methods.Length; i++)
        {
            Assert.AreEqual(expected[i], MemberEligibility.Admits(RuntimeSymbolImporter.Import(methods[i]), site, view),
                opcode + " " + methods[i]);
        }
    }

    /// <summary>
    /// Readonly storage can be read while constants are offered only as field tokens.
    /// </summary>
    [TestMethod]
    [DataRow("ldsfld", true, false)]
    [DataRow("ldsflda", true, false)]
    [DataRow("stsfld", false, false)]
    [DataRow("ldfld", false, false)]
    [DataRow("ldtoken field", true, true)]
    public void Fields_FollowStorageRules(string opcode, bool readOnly, bool literal)
    {
        using var editing = new EditingSession(new Session());
        var view = editing.Speculate([], 0, cancellationToken: TestContext.CancellationToken);
        var site = CompletionSite.None with { Owner = opcode };
        Assert.AreEqual(readOnly, MemberEligibility.Admits(
            RuntimeSymbolImporter.Import(typeof(string).GetField(nameof(string.Empty))!), site, view));
        Assert.AreEqual(literal, MemberEligibility.Admits(
            RuntimeSymbolImporter.Import(typeof(int).GetField(nameof(int.MaxValue))!), site, view));
    }

    /// <summary>
    /// An initonly instance field is offered only when the constructor's original receiver is beneath its value.
    /// </summary>
    [TestMethod]
    public void InitOnly_UsesOriginalReceiverProvenance()
    {
        using var editing = new EditingSession(new Session());
        string[] lines = [".class public Box {", ".field public initonly int32 Value",
            ".method public instance void .ctor() {", "ldarg.0", "call instance void Object::.ctor()",
            "ldarg.0", "ldc.i4.1", ""];
        var view = editing.Speculate(lines, lines.Length - 1, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines);
        var field = view.Scope.Fields(view.Owner!).Single();
        var site = CompletionSite.None with { Owner = "stfld" };
        Assert.IsTrue(MemberEligibility.Admits(field, site, view));
        string[] changed = [.. lines[..^1], "pop", "pop", "ldnull", "ldc.i4.1", ""];
        var other = editing.Speculate(changed, changed.Length - 1, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(other.SkippedLines);
        Assert.IsFalse(MemberEligibility.Admits(field, site, other));
    }

    /// <summary>
    /// Void remains an eligible token and pointer element while a bare void local is refused.
    /// </summary>
    [TestMethod]
    public void TypeSites_FilterOnlyTheirRestrictions()
    {
        using var editing = new EditingSession(new Session());
        var view = editing.Speculate([], 0, cancellationToken: TestContext.CancellationToken);
        Assert.IsTrue(MemberEligibility.Admits(TypeSymbol.Void, CompletionSite.None with { Owner = "ldtoken" }, view));
        var locals = CompletionSite.None with { Owner = ".locals" };
        Assert.IsFalse(MemberEligibility.Admits(TypeSymbol.Void, locals, view));
        Assert.IsTrue(MemberEligibility.Admits(TypeSymbol.Void, locals, view, hasElementSuffix: true));
        Assert.IsTrue(MemberEligibility.Admits(RuntimeSymbolImporter.Import(typeof(Console)), locals, view));
    }
}
