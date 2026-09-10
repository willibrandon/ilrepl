using IlRepl.Engine;
using IlRepl.Engine.Binding;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Attribute declarations bind consistently during runtime input and previews without invoking user code.
/// </summary>
[TestClass]
public sealed class AttributeBindingTests
{
    /// <summary>
    /// The test runner's cancellation and reporting context.
    /// </summary>
    public required TestContext TestContext { get; set; }

    /// <summary>
    /// Previewed attribute properties remain available after their accessor and declaring class have closed.
    /// </summary>
    [TestMethod]
    public void AttributeProperty_BindsBeforeSubmission()
    {
        var session = new Session();
        using var editing = new EditingSession(session);
        string[] lines = [".class public Mark extends System.Attribute {",
            ".method public instance void .ctor() {", "ldarg.0", "call instance void System.Attribute::.ctor()", "ret", "}",
            ".method public instance void set_Label(string label) {", "ret", "}",
            ".property instance string Label() {", ".set instance void Mark::set_Label(string)", "}", "}",
            ".class public Box {", ".custom instance void Mark::.ctor() = { property string Label = string('ready') }", "}", ""];
        var view = editing.Speculate(lines, lines.Length - 1, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines, string.Join("; ", view.SkippedLines.Select(line => line.Message)));
        var attribute = view.Scope.LookupType("Mark", null, 0, false).Type;
        Assert.AreEqual("Label", view.Scope.Properties(attribute).Single(property => property.DeclaringType == attribute).Name);
        Assert.IsTrue(view.Snapshot.Types.TryResolve("Box", false, false, out _));
        Assert.IsEmpty(session.Types);
    }

    /// <summary>
    /// An attribute argument inside a nested type participates in the containing family's replacement closure.
    /// </summary>
    [TestMethod]
    public void AttributeTypeArgument_RebuildsTheNestedDependent()
    {
        using var editing = new EditingSession(new Session());
        string[] lines = [".class public Point { }", ".class public Outer {", ".class nested public Inner {",
            ".custom instance void System.Diagnostics.DebuggerTypeProxyAttribute::.ctor(class System.Type) = { type(Point) }",
            "}", "}", ""];
        var original = editing.Speculate(lines, lines.Length - 1, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(original.SkippedLines, string.Join("; ", original.SkippedLines.Select(line => line.Message)));
        var identity = original.Scope.LookupType("Outer", null, 0, false).Type;
        string[] replaced = [.. lines[..^1], ".class public Point { }", ""];
        var current = editing.Speculate(replaced, replaced.Length - 1, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(current.SkippedLines, string.Join("; ", current.SkippedLines.Select(line => line.Message)));
        Assert.AreNotEqual(identity, current.Scope.LookupType("Outer", null, 0, false).Type);
    }

    /// <summary>
    /// Binding arrays and named values inspects metadata without invoking the attribute constructor or setter.
    /// </summary>
    [TestMethod]
    public void AttributeValues_DoNotInvokeUserCode()
    {
        AttributeBindingProbeAttribute.Calls = 0;
        var resolver = new TypeResolver();
        var context = new ParseContext([], [], GenericContext.Empty, resolver, []);
        const string text = "instance void IlRepl.Tests.Engine.Binding.AttributeBindingProbeAttribute::.ctor(int32[]) "
            + "= { { int32(1) int32(2) } property string Label = string('ready') }";
        var runtime = CustomAttributeParser.Parse(text, context, ".custom " + text);
        using var editing = new EditingSession(new Session());
        string[] lines = [".class public Annotated {", ".custom " + text, "}", ""];
        var view = editing.Speculate(lines, lines.Length - 1, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines, string.Join("; ", view.SkippedLines.Select(line => line.Message)));
        Assert.AreEqual(0, AttributeBindingProbeAttribute.Calls);
        Assert.AreSequenceEqual([1, 2], (int[])runtime.FixedArguments.Single()!);
        Assert.AreEqual("ready", runtime.NamedProperties.Single().Value);
    }
}
