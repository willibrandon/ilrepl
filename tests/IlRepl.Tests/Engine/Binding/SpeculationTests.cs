using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Repl;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Unsent input follows the live session's structural rules without compiling or running user code.
/// </summary>
[TestClass]
public sealed class SpeculationTests
{
    /// <summary>
    /// The test runner's cancellation and reporting context.
    /// </summary>
    public required TestContext TestContext { get; set; }

    /// <summary>
    /// Repeated replacement remaps a committed dependent's signature to the newest type identity.
    /// </summary>
    [TestMethod]
    public void Replacement_RebindsCommittedDependentsTwice()
    {
        var session = new Session();
        foreach (var line in new[] { ".class public A { }", ".class public B {",
            ".method public static A Id(A value) {", "ldarg value", "ret", "}", "}" })
        {
            session.AddLine(line);
        }

        using var editing = new EditingSession(session);
        string[] lines = [".class public A {", ".field public int32 X", "}",
            ".class public A {", ".field public string X", "}", ""];
        var view = editing.Speculate(lines, 6, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines, string.Join("; ", view.SkippedLines.Select(line => line.Message)));
        var current = view.Scope.LookupType("A", null, 0, false).Type;
        var dependent = view.Scope.LookupType("B", null, 0, false).Type;
        var method = view.Scope.Methods(dependent, "Id").Single();
        Assert.AreEqual(current, method.Parameters[0].Type);
        Assert.AreEqual(current, method.ReturnType);
        Assert.AreEqual(TypeSymbol.Primitive("string"), view.Scope.Field(current, "X")?.FieldType);
    }

    /// <summary>
    /// A pending cell is rebound only after every dependent session method has its replacement signature.
    /// </summary>
    [TestMethod]
    public void Replacement_RebuildsTheCellAfterDependentSignatures()
    {
        var session = new Session();
        foreach (var line in new[] { ".class public A { }", ".method A Id(A value) {",
            "ldarg value", "ret", "}", "ldnull", "call A Id(A)" })
        {
            session.AddLine(line);
        }

        using var editing = new EditingSession(session);
        var view = editing.Speculate([".class public A { }", ""], 1,
            cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines, string.Join("; ", view.SkippedLines.Select(line => line.Message)));
        var current = view.Scope.LookupType("A", null, 0, false).Type;
        Assert.AreEqual(current, view.Scope.SessionMethods.Single().ReturnType);
        Assert.AreEqual(current, view.Stack.Single());
    }

    /// <summary>
    /// Removing a referenced nested type refuses the replacement while keeping the accepted family inspectable.
    /// </summary>
    [TestMethod]
    public void Replacement_RefusesRemovalOfARequiredNestedType()
    {
        var session = new Session();
        foreach (var line in new[] { ".class public Outer {", ".class nested public Inner { }", "}",
            ".class public Holder {", ".field public class Outer/Inner Value", "}" })
        {
            session.AddLine(line);
        }

        using var editing = new EditingSession(session);
        var view = editing.Speculate([".class public Outer {", "}", ""], 2,
            inspecting: true, cancellationToken: TestContext.CancellationToken);
        Assert.HasCount(1, view.SkippedLines);
        Assert.IsTrue(view.Snapshot.Types.TryResolve("Outer/Inner", false, false, out _));
        Assert.AreEqual("Outer", view.Owner?.Name);
    }

    /// <summary>
    /// A property block preserves the enclosing class and validates its accessor against the shared signature parser.
    /// </summary>
    [TestMethod]
    public void Property_UsesTheDeclaredAccessor()
    {
        using var editing = new EditingSession(new Session());
        string[] lines = [".class public Box {", ".method public int32 get_Value() {", "ldc.i4.1", "ret", "}",
            ".property instance int32 Value() {", ".get instance int32 Box::get_Value()", "}", "}", ""];
        var view = editing.Speculate(lines, 9, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines, string.Join("; ", view.SkippedLines.Select(line => line.Message)));
        Assert.IsNull(view.Owner);
        Assert.IsTrue(view.Snapshot.Types.TryResolve("Box", false, false, out _));
    }

    /// <summary>
    /// An unsent method exposes its named parameters, locals and stack to the next operand.
    /// </summary>
    [TestMethod]
    public void Method_ExposesArgumentsLocalsAndStack()
    {
        var session = new Session();
        using var editing = new EditingSession(session);
        string[] lines = [".method int32 Add(int32 left, int32 right) {", ".locals init (int32 sum)", "ldarg left", ""];
        var view = editing.Speculate(lines, 3, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines);
        Assert.HasCount(2, view.Scope.Arguments);
        Assert.AreEqual("left", view.Scope.Arguments[0].Name);
        Assert.AreEqual("sum", view.Scope.Locals[0].Name);
        Assert.AreEqual(TypeSymbol.Primitive("int32"), view.Stack.Single());
        Assert.IsFalse(session.State.IsMethod);
    }

    /// <summary>
    /// A top-level run clears the preview body while preserving declarations and leaving the real session untouched.
    /// </summary>
    [TestMethod]
    [DataRow("")]
    [DataRow("ret")]
    [DataRow(".run")]
    public void Run_PreservesDeclarations(string boundary)
    {
        var session = new Session();
        using var editing = new EditingSession(session);
        string[] lines = [".locals init (int32 value)", "ldc.i4 42", boundary, ""];
        var view = editing.Speculate(lines, 3, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines);
        Assert.IsEmpty(view.Stack);
        Assert.HasCount(1, view.Scope.Locals);
        Assert.AreEqual(0, session.Submissions);
        Assert.IsEmpty(session.State.Locals);
    }

    /// <summary>
    /// A refused line restores comment and declaration state before replay continues.
    /// </summary>
    [TestMethod]
    public void RefusedLine_RestoresCommentState()
    {
        using var editing = new EditingSession(new Session());
        string[] lines = ["lcd.i4 2 /*", "ldc.i4 3", ""];
        var view = editing.Speculate(lines, 2, cancellationToken: TestContext.CancellationToken);
        Assert.HasCount(1, view.SkippedLines);
        Assert.IsFalse(view.InBlockComment);
        Assert.AreEqual(TypeSymbol.Primitive("int32"), view.Stack.Single());
    }

    /// <summary>
    /// A run refused inside an open method preserves its parameter scope and accepted instructions.
    /// </summary>
    [TestMethod]
    public void RefusedRun_PreservesOpenMethod()
    {
        using var editing = new EditingSession(new Session());
        string[] lines = [".method int32 Id(int32 value) {", ".run", "ldarg value", ""];
        var view = editing.Speculate(lines, 3, cancellationToken: TestContext.CancellationToken);
        Assert.HasCount(1, view.SkippedLines);
        Assert.AreEqual("Id", view.OpenMethod?.Name);
        Assert.AreEqual(TypeSymbol.Primitive("int32"), view.Stack.Single());
    }

    /// <summary>
    /// Clearing a member removes its declaration so a corrected header can reuse the name.
    /// </summary>
    [TestMethod]
    public void Clear_RemovesOnlyTheOpenMember()
    {
        using var editing = new EditingSession(new Session());
        string[] lines = [".class public Box {", ".method public int32 Get() {", ".clear",
            ".method public string Get() {", "ldstr \"ready\"", ""];
        var view = editing.Speculate(lines, 5, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines);
        Assert.AreEqual("Box", view.Owner?.Name);
        Assert.AreEqual(TypeSymbol.Primitive("string"), view.OpenMethod?.ReturnType);
        Assert.HasCount(1, view.Snapshot.Types.DeclarationOf(view.Owner!)!.Methods);
    }

    /// <summary>
    /// A completed method declaration suspends and resumes the cell's pending label space.
    /// </summary>
    [TestMethod]
    public void ClosedMethod_PreservesCellLabels()
    {
        using var editing = new EditingSession(new Session());
        string[] lines = ["br END", ".method void F() {", "ret", "}", "END: ldc.i4.1", ""];
        var view = editing.Speculate(lines, 5, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines);
        Assert.Contains("END", view.DefinedLabels);
        Assert.IsEmpty(view.PendingLabels);
        Assert.AreEqual(TypeSymbol.Primitive("int32"), view.Stack.Single());
        Assert.IsNull(view.OpenMethod);
    }

    /// <summary>
    /// Edited generic and nested type headers allocate no runtime assemblies across repeated previews.
    /// </summary>
    [TestMethod]
    public void GenericHeaders_AllocateNoRuntimeAssemblies()
    {
        var session = new Session();
        using var editing = new EditingSession(session);
        var thread = Environment.CurrentManagedThreadId;
        var created = new List<string>();
        void Record(object? sender, AssemblyLoadEventArgs args)
        {
            if (Environment.CurrentManagedThreadId == thread && args.LoadedAssembly.IsDynamic)
            {
                created.Add(args.LoadedAssembly.FullName ?? "dynamic assembly");
            }
        }

        AppDomain.CurrentDomain.AssemblyLoad += Record;
        try
        {
            for (var i = 0; i < 100; i++)
            {
                string[] lines = [$".class public Box{i}<T> {{", ".class nested public Inner<T, U> {",
                    ".method public !!V Id<V>(!!V value) {", "ldarg value", ""];
                var view = editing.Speculate(lines, 4, cancellationToken: TestContext.CancellationToken);
                Assert.IsEmpty(view.SkippedLines, string.Join("; ", view.SkippedLines.Select(line => line.Message)));
                Assert.HasCount(2, view.Scope.Generics.TypeArguments);
                Assert.HasCount(1, view.Scope.Generics.MethodArguments);
            }
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyLoad -= Record;
        }

        Assert.IsEmpty(created);
    }

    /// <summary>
    /// Replacing a failed nested header leaves the name available just as a corrected live header does.
    /// </summary>
    [TestMethod]
    public void RefusedNestedHeader_DoesNotReserveTheName()
    {
        using var editing = new EditingSession(new Session());
        string[] lines = [".class public Outer {", ".class nested public Inner extends string {",
            ".class nested public Inner { }", "}", ""];
        var view = editing.Speculate(lines, 4, cancellationToken: TestContext.CancellationToken);
        Assert.HasCount(1, view.SkippedLines);
        Assert.IsTrue(view.Snapshot.Types.TryResolve("Outer/Inner", false, false, out _));
        Assert.IsNull(view.Owner);
    }

    /// <summary>
    /// Capturing accepted input preserves existing local and method bindings without mutating the live revision.
    /// </summary>
    [TestMethod]
    public void Capture_PreservesExistingState()
    {
        var repl = new ReplCore();
        foreach (var line in new[] { ".locals init (int32 value)", ".method int32 Id(int32 input) {", "ldarg input" })
        {
            repl.Handle(line);
        }

        var revision = repl.Session.CompletionRevision;
        using var editing = new EditingSession(repl.Session);
        var view = editing.Speculate([""], 0, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines);
        Assert.AreEqual("Id", view.OpenMethod?.Name);
        Assert.AreEqual("input", view.Scope.Arguments[0].Name);
        Assert.AreEqual(TypeSymbol.Primitive("int32"), view.Stack.Single());
        Assert.AreEqual(revision, repl.Session.CompletionRevision);
    }
}
