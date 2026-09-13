using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using Mono.Cecil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Event handler types retain their annotations through binding, replacement, and metadata emission.
/// </summary>
[TestClass]
public sealed class EventModifierTests
{
    private const string ActionType = "class [System.Runtime]System.Action";
    private const string Volatile = "[System.Runtime]System.Runtime.CompilerServices.IsVolatile";
    private const string Cdecl = "[System.Runtime]System.Runtime.CompilerServices.CallConvCdecl";

    /// <summary>
    /// Supplies cancellation for speculative analysis.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The live family, saved assembly, and assembled listing preserve joint event modifier order.
    /// </summary>
    [TestMethod]
    public async Task HandlerType_RetainsAnnotationsEverywhere()
    {
        var handler = $"{ActionType} modreq({Volatile}) modopt({Cdecl}) modreq({Volatile})";
        var lines = EventSource(handler);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, preview.Diagnostics);
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        Assert.AreEqual(typeof(Action), session.Types.Single().RuntimeType!.GetEvent("Changed")!.EventHandlerType);
        AssertModifiers(session.Types.Single().Definition!.Image!);
        AssertModifiers(AssemblyExporter.Write(session, "event-modifiers"));
        var il = session.ToIlAsm();
        Assert.Contains($".event {handler} Changed", il);
        AssertModifiers(IlasmLocator.Assemble(il));
    }

    /// <summary>
    /// A type mentioned only by an event annotation participates in family replacement and export.
    /// </summary>
    [TestMethod]
    public async Task HandlerModifier_RebuildsItsDependentFamily()
    {
        var session = IlLines.Load(".class public Marker { }");
        foreach (var line in EventSource($"{ActionType} modopt(Marker)"))
        {
            session.AddLine(line);
        }

        var previous = session.Types.Single(type => type.FullName == "Events").RuntimeType;
        var replacement = new[] { ".class public Marker {", ".field public int32 Value", "}" };
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(replacement, 1, 0, 1), TestContext.CancellationToken);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, preview.Diagnostics);
        foreach (var line in replacement)
        {
            session.AddLine(line);
        }

        Assert.AreNotSame(previous, session.Types.Single(type => type.FullName == "Events").RuntimeType);
        using var exported = AssemblyDefinition.ReadAssembly(new MemoryStream(AssemblyExporter.Write(session, "event-replaced")));
        var handler = (OptionalModifierType)exported.MainModule.GetType("Events").Events.Single().EventType;
        Assert.AreSame(exported.MainModule.GetType("Marker"), handler.ModifierType.Resolve());
        _ = IlasmLocator.Assemble(session.ToIlAsm());
    }

    /// <summary>
    /// An event annotation cannot bypass the visibility of a nested private session type.
    /// </summary>
    [TestMethod]
    public async Task HandlerModifier_RespectsTypeAccess()
    {
        var session = IlLines.Load(".class public Outer {", ".class nested private Hidden { }", "}");
        var lines = EventSource($"{ActionType} modopt(Outer/Hidden)");
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.Contains(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error
            && diagnostic.Message.Contains("nested private", StringComparison.Ordinal), preview.Diagnostics);
        var error = Assert.ThrowsExactly<ReplException>(() =>
        {
            foreach (var line in lines)
            {
                session.AddLine(line);
            }
        });
        Assert.Contains("nested private", error.Message);
    }

    private static string[] EventSource(string handler) =>
    [
        ".class public Events {",
        $".method public static void Add({ActionType} handler) {{",
        "ret",
        "}",
        $".method public static void Remove({ActionType} handler) {{",
        "ret",
        "}",
        $".event {handler} Changed {{",
        $".addon void Events::Add({ActionType})",
        $".removeon void Events::Remove({ActionType})",
        "}",
        "}",
    ];

    private static void AssertModifiers(byte[] image)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(image));
        var outer = (RequiredModifierType)assembly.MainModule.GetType("Events").Events.Single().EventType;
        Assert.AreEqual("IsVolatile", outer.ModifierType.Name);
        var middle = (OptionalModifierType)outer.ElementType;
        Assert.AreEqual("CallConvCdecl", middle.ModifierType.Name);
        var inner = (RequiredModifierType)middle.ElementType;
        Assert.AreEqual("IsVolatile", inner.ModifierType.Name);
        Assert.AreEqual("System.Action", inner.ElementType.FullName);
    }
}
