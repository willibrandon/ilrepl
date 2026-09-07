using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Tests for <see cref="ReplCore"/> with <c>.class</c> blocks: the notes, the cell numbering,
/// the status, and the commands that inspect, undo, clear, and reset types.
/// </summary>
[TestClass]
public sealed class ReplCoreTypeTests
{
    private static readonly string[] Point =
    [
        ".class public sequential ansi sealed Point extends [System.Runtime]System.ValueType {",
        ".field public int32 X",
        ".field public int32 Y",
        ".method public instance int32 Sum() {",
        "ldarg.0",
        "ldfld int32 Point::X",
        "ldarg.0",
        "ldfld int32 Point::Y",
        "add",
        "ret",
        "}",
        "}",
    ];

    private static string Plain(ReplCore core) => string.Join("\n", core.Transcript.Lines.Select(l => l.PlainText));

    private static ReplCore Load(params string[] lines)
    {
        var core = new ReplCore();
        foreach (var line in lines)
        {
            core.Handle(line);
        }

        return core;
    }

    /// <summary>
    /// The header notes the kind and name and the status says which type is open.
    /// </summary>
    [TestMethod]
    public void Handle_ClassHeader_NotesAndOpensType()
    {
        var core = new ReplCore();
        Assert.IsTrue(core.Handle(Point[0]).Succeeded);
        Assert.Contains("  struct Point", Plain(core));
        Assert.AreEqual(LineKind.Info, core.Transcript.Lines[^1].Kind);
        var status = core.Status;
        Assert.AreEqual("Point", status.OpenType);
        Assert.IsNull(status.OpenMethod);
        Assert.AreEqual(0, status.Types);
        Assert.AreEqual(1, status.CellNumber);
    }

    /// <summary>
    /// Members are noted as they arrive, a member inside a class shows both in the status, and
    /// closing the class advances the cell number once.
    /// </summary>
    [TestMethod]
    public void Handle_ClassClose_NotesEndAndAdvancesCellNumberOnce()
    {
        var core = new ReplCore();
        foreach (var line in Point.Take(4))
        {
            core.Handle(line);
        }

        Assert.Contains("  field public int32 X", Plain(core));
        Assert.Contains("  method instance int32 Sum()", Plain(core));
        Assert.AreEqual("Sum", core.Status.OpenMethod);
        Assert.AreEqual("Point", core.Status.OpenType);
        Assert.AreEqual(1, core.Status.CellNumber);
        foreach (var line in Point.Skip(4))
        {
            Assert.IsTrue(core.Handle(line).Succeeded, line);
        }

        Assert.Contains("  end of method Sum", Plain(core));
        Assert.Contains("  end of struct Point", Plain(core));
        Assert.IsNull(core.Status.OpenType);
        Assert.AreEqual(1, core.Status.Types);
        Assert.AreEqual(2, core.Status.CellNumber);
        Assert.AreEqual("il[2]> ", core.Prompt);
    }

    /// <summary>
    /// A nested class shows its path, and the outer one again after it closes.
    /// </summary>
    [TestMethod]
    public void Handle_NestedClass_StatusShowsPath()
    {
        var core = Load(".class public Outer {", ".class nested public Inner {");
        Assert.AreEqual("Outer/Inner", core.Status.OpenType);
        core.Handle("}");
        Assert.AreEqual("Outer", core.Status.OpenType);
        Assert.Contains("  end of class Outer/Inner", Plain(core));
        core.Handle("}");
        Assert.AreEqual(2, core.Status.Types);
    }

    /// <summary>
    /// An empty line, <c>.run</c>, and <c>.save</c> are refused while a class is open.
    /// </summary>
    [TestMethod]
    [DataRow("")]
    [DataRow(".run")]
    [DataRow(".save x.dll")]
    public void Handle_RunOrSaveInsideClass_Errors(string command)
    {
        var core = Load("ldc.i4 1", Point[0]);
        Assert.IsFalse(core.Handle(command).Succeeded);
        Assert.Contains("error: class Point is still open; close it with }", Plain(core));
    }

    /// <summary>
    /// <c>.types</c> lists each type with its members, nested ones under their outer type.
    /// </summary>
    [TestMethod]
    public void Handle_Types_ListsOrSaysNone()
    {
        var core = new ReplCore();
        core.Handle(".types");
        Assert.Contains("  no types", Plain(core));
        foreach (var line in Point)
        {
            core.Handle(line);
        }

        core.Handle(".class public Outer {");
        core.Handle(".class nested public Inner { }");
        core.Handle("}");
        core.Handle(".types");
        var listing = core.Transcript.Lines.Where(l => l.Kind == LineKind.Listing).Select(l => l.PlainText).ToList();
        Assert.AreSequenceEqual(["  struct Point", "      public int32 X", "      public int32 Y", "      instance int32 Sum()", "  class Outer", "      class Outer/Inner"], listing);
    }

    /// <summary>
    /// <c>.show</c> inside a class lists the header, the members so far, and the open method's body.
    /// </summary>
    [TestMethod]
    public void Handle_ShowInsideClass_ListsHeaderFieldsAndMethods()
    {
        var core = Load(Point[0], Point[1], Point[2], Point[3], "ldarg.0");
        core.Handle(".show");
        var listing = core.Transcript.Lines.Where(l => l.Kind == LineKind.Listing).Select(l => l.PlainText).ToList();
        Assert.AreEqual("  .class public sequential Point extends ValueType {", listing[0]);
        Assert.AreEqual("      .field public int32 X", listing[1]);
        Assert.AreEqual("      .field public int32 Y", listing[2]);
        Assert.AreEqual("    .method instance int32 Sum() {", listing[3]);
        Assert.Contains(l => l.Contains("ldarg.0", StringComparison.Ordinal) && l.Contains("[Point&]", StringComparison.Ordinal), listing);
    }

    /// <summary>
    /// Undo on a member line pops it, undo on the header abandons the class with a note.
    /// </summary>
    [TestMethod]
    public void Handle_UndoInsideClass_PopsThenAbandons()
    {
        var core = Load(Point[0], Point[1]);
        core.Handle(".undo");
        Assert.AreEqual("Point", core.Status.OpenType);
        core.Handle(".undo");
        Assert.Contains("  class Point abandoned", Plain(core));
        Assert.IsNull(core.Status.OpenType);
        Assert.AreEqual(1, core.Status.CellNumber);
    }

    /// <summary>
    /// <c>.clear</c> abandons an open class and keeps the cell; outside a class it keeps the types.
    /// </summary>
    [TestMethod]
    public void Handle_Clear_AbandonsOrKeepsTypes()
    {
        var core = Load("ldc.i4 5", Point[0], Point[1]);
        core.Handle(".clear");
        Assert.Contains("  class Point abandoned", Plain(core));
        Assert.IsNull(core.Status.OpenType);
        Assert.AreEqual("[int32]", core.Status.Stack);
        foreach (var line in Point)
        {
            core.Handle(line);
        }

        core.Handle(".clear");
        Assert.Contains("  cell cleared (declarations kept)", Plain(core));
        Assert.AreEqual(1, core.Status.Types);
    }

    /// <summary>
    /// <c>.reset</c> drops the types too.
    /// </summary>
    [TestMethod]
    public void Handle_Reset_DropsTypes()
    {
        var core = Load([.. Point, ".reset"]);
        Assert.Contains("  cell, declarations, methods, and types cleared", Plain(core));
        Assert.AreEqual(0, core.Status.Types);
    }

    /// <summary>
    /// A placement mistake is an error line, not an exception, and the class stays open.
    /// </summary>
    [TestMethod]
    public void Handle_PlacementError_IsAnErrorLine()
    {
        var core = Load(Point[0]);
        Assert.IsFalse(core.Handle("ldc.i4 1").Succeeded);
        Assert.Contains("error: instructions belong in a method body; struct Point is open", Plain(core));
        Assert.AreEqual("Point", core.Status.OpenType);
    }

    /// <summary>
    /// Help lists the class directives and the types command.
    /// </summary>
    [TestMethod]
    public void Handle_Help_ListsClassDirectivesAndTypesCommand()
    {
        var core = Load(".help");
        var text = Plain(core);
        Assert.Contains(".class public Name extends T {", text);
        Assert.Contains(".field public [static] T Name", text);
        Assert.Contains(".types", text);
        Assert.Contains("newobj instance void Point::.ctor(int32, int32)", text);
    }
}
