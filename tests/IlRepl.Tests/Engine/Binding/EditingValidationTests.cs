using IlRepl.Engine;
using IlRepl.Engine.Binding;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Previewed declarations obey the accepting engine's close rules and keep earlier views independent.
/// </summary>
[TestClass]
public sealed class EditingValidationTests
{
    /// <summary>
    /// Assigned cell types survive clear and undo while additional type parameters invalidate the assignment.
    /// </summary>
    [TestMethod]
    public void TypeArguments_FollowPersistentCellDeclarations()
    {
        var session = new Session();
        session.AddLine(".typeparams (T)");
        session.AddLine(".typeargs (string)");
        using var editing = new EditingSession(session);
        string[] lines = ["ldc.i4.1", ".clear", "ldc.i4.2", ".undo", ""];
        var view = editing.Speculate(lines, lines.Length - 1, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines);
        Assert.AreEqual(TypeSymbol.Primitive("string"), view.TypeArguments?.Single());
        var changed = editing.Speculate([.. lines[..^1], ".typeparams (U)", ""], lines.Length,
            cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(changed.SkippedLines);
        Assert.IsNull(changed.TypeArguments);
        Assert.HasCount(2, changed.Scope.Generics.MethodArguments);
    }

    /// <summary>
    /// A refused assignment preserves the previous arguments and uses the accepting engine's exact diagnostic.
    /// </summary>
    [TestMethod]
    public void TypeArguments_RefusalPreservesThePreviousAssignment()
    {
        var session = new Session();
        session.AddLine(".typeparams (T)");
        session.AddLine(".typeargs (string)");
        var expected = Assert.ThrowsExactly<ReplException>(() => session.AddLine(".typeargs (int32, string)")).Message;
        using var editing = new EditingSession(session);
        var view = editing.Speculate([".typeargs (int32, string)", ""], 1,
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(expected, view.SkippedLines.Single().Message);
        Assert.AreEqual(TypeSymbol.Primitive("string"), view.TypeArguments?.Single());
    }

    /// <summary>
    /// Clearing a nested class abandons its whole family just as the live session does.
    /// </summary>
    [TestMethod]
    public void ClearNestedType_AbandonsTheFamily()
    {
        using var editing = new EditingSession(new Session());
        var view = editing.Speculate([".class Outer {", ".class nested public Inner {", ".clear", ""], 3,
            cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines);
        Assert.IsNull(view.Owner);
        Assert.IsEmpty(view.Snapshot.Types.Entries);
    }

    /// <summary>
    /// Missing command arguments are refused before checking whether a method or class remains open.
    /// </summary>
    [TestMethod]
    public void SaveWithoutPath_UsesTheSharedUsageGuard()
    {
        using var editing = new EditingSession(new Session());
        var view = editing.Speculate([".method void F() {", ".save", ""], 2,
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual("usage: .save <path.dll>", view.SkippedLines.Single().Message);
        Assert.AreEqual("F", view.OpenMethod?.Name);
    }

    /// <summary>
    /// The test runner's cancellation and reporting context.
    /// </summary>
    public required TestContext TestContext { get; set; }

    /// <summary>
    /// An omitted interface implementation keeps the edited class open with the accepting engine's diagnostic.
    /// </summary>
    [TestMethod]
    public void MissingInterfaceMember_MatchesTheAcceptingEngine()
    {
        const string header = ".class public Resource implements System.IDisposable {";
        var session = new Session();
        session.AddLine(header);
        var expected = Assert.ThrowsExactly<ReplException>(() => session.AddLine("}")).Message;
        using var editing = new EditingSession(new Session());
        var view = editing.Speculate([header, "}", ""], 2, cancellationToken: TestContext.CancellationToken);
        Assert.HasCount(1, view.SkippedLines);
        Assert.AreEqual(expected, view.SkippedLines.Single().Message);
        Assert.AreEqual("Resource", view.Owner?.Name);
    }

    /// <summary>
    /// Nested types are checked against abstract members declared later on their enclosing base type.
    /// </summary>
    [TestMethod]
    public void NestedValidation_WaitsForTheWholeFamily()
    {
        string[] lines = [".class public abstract Outer {", ".class nested public Child extends Outer { }",
            ".method public virtual abstract void Required() { }", "}", ""];
        var session = new Session();
        foreach (var line in lines[..^2])
        {
            session.AddLine(line);
        }

        var expected = Assert.ThrowsExactly<ReplException>(() => session.AddLine("}")).Message;
        using var editing = new EditingSession(new Session());
        var view = editing.Speculate(lines, lines.Length - 1, cancellationToken: TestContext.CancellationToken);
        Assert.HasCount(1, view.SkippedLines);
        Assert.AreEqual(expected, view.SkippedLines[0].Message);
        Assert.AreEqual("Outer", view.Owner?.Name);
    }

    /// <summary>
    /// A published editing view keeps its locals when the same replay session consumes another declaration.
    /// </summary>
    [TestMethod]
    public void EarlierView_DoesNotObserveLaterEdits()
    {
        using var editing = new EditingSession(new Session());
        var first = editing.Speculate([".locals init (int32 first)", ""], 1,
            cancellationToken: TestContext.CancellationToken);
        var second = editing.Speculate([".locals init (int32 first)", ".locals init (string second)", ""], 2,
            cancellationToken: TestContext.CancellationToken);
        Assert.HasCount(1, first.Scope.Locals);
        Assert.HasCount(2, second.Scope.Locals);
    }

    /// <summary>
    /// Preview checks a private type retained only as a custom modifier on a generic method argument.
    /// </summary>
    [TestMethod]
    public void GenericArgumentModifier_UsesTheAcceptingAccessibilityRule()
    {
        var session = IlLines.Load(".class public Outer {", ".class nested private Inner { }", "}");
        var line = "call !!0[] [System.Runtime]System.Array::Empty<int32 modopt(Outer/Inner)>()";
        var expected = Assert.ThrowsExactly<ReplException>(() => session.AddLine(line)).Message;
        using var editing = new EditingSession(session);
        var view = editing.Speculate([line, ""], 1, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(expected, view.SkippedLines.Single().Message);
    }

    /// <summary>
    /// Undo removes a cell instruction while preserving declarations that survived an earlier clear.
    /// </summary>
    [TestMethod]
    public void UndoAfterClear_PreservesPersistentDeclarations()
    {
        using var editing = new EditingSession(new Session());
        string[] lines = [".locals init (int32 value)", "ldc.i4.1", ".clear", "ldc.i4.2", ".undo", ""];
        var view = editing.Speculate(lines, lines.Length - 1, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines);
        Assert.HasCount(1, view.Scope.Locals);
        Assert.AreEqual("value", view.Scope.Locals[0].Name);
        Assert.IsEmpty(view.Stack);
    }
}
