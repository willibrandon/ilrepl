using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Tests for <see cref="ReplCore"/> with <c>.method</c> blocks: the notes, the commands, the
/// status, and the cell number.
/// </summary>
[TestClass]
public sealed class ReplCoreMethodTests
{
    private static readonly string[] Fib =
    [
        ".method int32 Fib(int32 n) {",
        "ldarg n",
        "ldc.i4 2",
        "blt BASE",
        "ldarg n",
        "ldc.i4 1",
        "sub",
        "call int32 Fib(int32)",
        "ldarg n",
        "ldc.i4 2",
        "sub",
        "call int32 Fib(int32)",
        "add",
        "ret",
        "BASE: ldarg n",
        "ret",
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
    /// The header prints its signature and the status says which method is open.
    /// </summary>
    [TestMethod]
    public void Handle_MethodHeader_NotesSignatureAndOpensMethod()
    {
        var core = new ReplCore();
        Assert.IsTrue(core.Handle(".method int32 Fib(int32 n) {").Succeeded);
        Assert.Contains("  method int32 Fib(int32 n)", Plain(core));
        Assert.AreEqual(LineKind.Info, core.Transcript.Lines[^1].Kind);
        var status = core.Status;
        Assert.AreEqual("Fib", status.OpenMethod);
        Assert.AreEqual(0, status.Methods);
        Assert.IsTrue(status.CellIsEmpty);
        Assert.AreEqual(1, status.CellNumber);
    }

    /// <summary>
    /// The closing brace notes the end, counts the method, and advances the cell number once.
    /// </summary>
    [TestMethod]
    public void Handle_MethodClose_NotesEndAndAdvancesCellNumberOnce()
    {
        var core = new ReplCore();
        foreach (var line in Fib[..^1])
        {
            core.Handle(line);
            Assert.AreEqual(1, core.CellNumber, line);
        }

        Assert.IsTrue(core.Handle("}").Succeeded);
        Assert.Contains("  end of method Fib", Plain(core));
        Assert.IsNull(core.Status.OpenMethod);
        Assert.AreEqual(1, core.Status.Methods);
        Assert.AreEqual(2, core.CellNumber);
        Assert.AreEqual("il[2]> ", core.Prompt);
    }

    /// <summary>
    /// A refused close and an abandoned block keep the cell number.
    /// </summary>
    [TestMethod]
    public void Handle_RejectedOrAbandonedClose_KeepsCellNumber()
    {
        var core = Load(".method int32 F() {", "ldc.i4 1", "ldc.i4 2");
        Assert.IsFalse(core.Handle("}").Succeeded);
        Assert.Contains("error: method F needs a ret before }", Plain(core));
        Assert.AreEqual(1, core.CellNumber);
        Assert.AreEqual("F", core.Status.OpenMethod);

        core.Handle(".clear");
        Assert.Contains("  method F abandoned", Plain(core));
        Assert.AreEqual(1, core.CellNumber);
        Assert.IsNull(core.Status.OpenMethod);
    }

    /// <summary>
    /// Closing a block leaves the cell's own instructions in place under the new number.
    /// </summary>
    [TestMethod]
    public void Handle_MethodClose_KeepsPendingCellInstructions()
    {
        var core = Load("ldc.i4 1", ".method int32 Two() {", "ldc.i4 2", "ret", "}");
        Assert.AreEqual(2, core.CellNumber);
        Assert.AreEqual(1, core.Status.Instructions);
        Assert.AreEqual("[int32]", core.Status.Stack);
        core.Handle("ret");
        Assert.Contains("= 1 : int32", Plain(core));
        Assert.AreEqual(3, core.CellNumber);
    }

    /// <summary>
    /// The transcript from the docs: define, then call from the next cell.
    /// </summary>
    [TestMethod]
    public void Handle_CallToSessionMethod_ReturnsValue()
    {
        var core = Load(Fib);
        core.Handle("ldc.i4 10");
        core.Handle("call int32 Fib(int32)");
        Assert.Contains("┊ [int32]", Plain(core));
        core.Handle("ret");
        Assert.Contains("= 55 : int32", Plain(core));
        Assert.AreEqual("il[3]> ", core.Prompt);
    }

    /// <summary>
    /// ret inside a block echoes the stack and never runs the cell.
    /// </summary>
    [TestMethod]
    public void Handle_RetInsideMethod_EchoesStackWithoutRunning()
    {
        var core = Load(".method int32 One() {", "ldc.i4 1");
        Assert.IsTrue(core.Handle("ret").Succeeded);
        Assert.AreEqual(LineKind.Stack, core.Transcript.Lines[^1].Kind);
        Assert.AreEqual("  ┊ []", core.Transcript.Lines[^1].PlainText);
        Assert.DoesNotContain(l => l.Kind == LineKind.Result, core.Transcript.Lines);
        Assert.DoesNotContain("ret inside the cell", Plain(core));
        Assert.AreEqual("One", core.Status.OpenMethod);
    }

    /// <summary>
    /// Running or saving while a block is open is refused with the way out.
    /// </summary>
    /// <param name="line">The line that would run or save the cell.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow(".run")]
    [DataRow(".save x.dll")]
    public void Handle_RunOrSaveInsideMethod_Errors(string line)
    {
        var core = Load("ldc.i4 1", ".method int32 Fib(int32 n) {");
        Assert.IsFalse(core.Handle(line).Succeeded);
        Assert.Contains("error: method Fib is still open; close it with }", Plain(core));
        Assert.AreEqual("Fib", core.Status.OpenMethod);
        Assert.AreEqual(1, core.CellNumber);
    }

    /// <summary>
    /// .methods lists the signatures in definition order, or says there are none.
    /// </summary>
    [TestMethod]
    public void Handle_Methods_ListsSignaturesOrSaysNone()
    {
        var core = new ReplCore();
        core.Handle(".methods");
        Assert.Contains("  no methods", Plain(core));

        foreach (var line in new[] { ".method int32 Two() {", "ldc.i4 2", "ret", "}", ".method void Greet(string name) {", "ret", "}" })
        {
            core.Handle(line);
        }

        core.Transcript.Clear();
        core.Handle(".methods");
        var listing = core.Transcript.Lines.Where(l => l.Kind == LineKind.Listing).Select(l => l.PlainText).ToList();
        Assert.AreSequenceEqual(["  int32 Two()", "  void Greet(string name)"], listing);
    }

    /// <summary>
    /// .show inside a block lists the method with the stack after each instruction.
    /// </summary>
    [TestMethod]
    public void Handle_ShowInsideMethod_ListsMethodBody()
    {
        var core = Load("ldc.i4 9", ".method int32 Fib(int32 n) {", "ldarg n", "ldc.i4 2");
        core.Transcript.Clear();
        core.Handle(".show");
        var text = Plain(core);
        Assert.Contains("  .method int32 Fib(int32 n) {", text);
        Assert.Contains("000  ldarg n", text);
        Assert.Contains("[int32]", text);
        Assert.Contains("001  ldc.i4 2", text);
        Assert.Contains("[int32, int32]", text);
        Assert.DoesNotContain("ldc.i4 9", text, "the cell is not listed while a method is open");
        Assert.DoesNotContain("(empty cell)", text);

        core.Transcript.Clear();
        core.Handle(".undo");
        core.Handle(".undo");
        core.Handle(".show");
        Assert.Contains("(empty method)", Plain(core));
    }

    /// <summary>
    /// .undo inside a block takes back lines, and taking back the header abandons the block.
    /// </summary>
    [TestMethod]
    public void Handle_UndoInsideMethod_PopsLineThenAbandons()
    {
        var core = Load(".method int32 Fib(int32 n) {", "ldarg n");
        core.Handle(".undo");
        Assert.AreEqual("Fib", core.Status.OpenMethod);
        Assert.AreEqual("[]", core.Status.Stack);
        Assert.DoesNotContain("abandoned", Plain(core));

        core.Handle(".undo");
        Assert.Contains("  method Fib abandoned", Plain(core));
        Assert.IsNull(core.Status.OpenMethod);
        Assert.AreEqual(1, core.CellNumber);
    }

    /// <summary>
    /// .clear inside a block abandons the method and leaves the cell alone.
    /// </summary>
    [TestMethod]
    public void Handle_ClearInsideMethod_AbandonsAndKeepsCell()
    {
        var core = Load("ldc.i4 1", ".method int32 Fib(int32 n) {", "ldarg n");
        core.Handle(".clear");
        Assert.Contains("  method Fib abandoned", Plain(core));
        Assert.IsNull(core.Status.OpenMethod);
        Assert.AreEqual("[int32]", core.Status.Stack);
        Assert.AreEqual(0, core.Status.Methods);
    }

    /// <summary>
    /// .clear outside a block keeps the methods, like the declarations.
    /// </summary>
    [TestMethod]
    public void Handle_ClearOutsideMethod_KeepsMethods()
    {
        var core = Load(Fib);
        core.Handle("ldc.i4 1");
        core.Handle(".clear");
        Assert.Contains("cell cleared (declarations kept)", Plain(core));
        Assert.AreEqual(1, core.Status.Methods);
        Assert.IsTrue(core.Status.CellIsEmpty);
    }

    /// <summary>
    /// .reset drops the methods too.
    /// </summary>
    [TestMethod]
    public void Handle_Reset_DropsMethods()
    {
        var core = Load(Fib);
        core.Handle(".reset");
        Assert.Contains("  cell, declarations, methods, and types cleared", Plain(core));
        Assert.AreEqual(0, core.Status.Methods);
        Assert.IsFalse(core.Handle("call int32 Fib(int32)").Succeeded);
        Assert.Contains("no method 'Fib' in the session", Plain(core));
    }

    /// <summary>
    /// Defining a name again notes the replacement.
    /// </summary>
    [TestMethod]
    public void Handle_RedefineMethod_NotesReplacement()
    {
        var core = Load(".method int32 Two() {", "ldc.i4 2", "ret", "}", ".method int32 Two() {", "ldc.i4 20", "ret", "}");
        Assert.Contains("  replaced method Two", Plain(core));
        Assert.AreEqual(1, core.Status.Methods);
        Assert.AreEqual(3, core.CellNumber);
    }

    /// <summary>
    /// An invalid join names both incoming paths and keeps the method open.
    /// </summary>
    [TestMethod]
    public void Handle_InvalidJoin_ReportsPathsAndKeepsBlockOpen()
    {
        var core = Load(".method void Bad() {", "ldc.i4 0", "brfalse SKIP", "ldc.i4 1", "ldc.i4 2", "pop", "SKIP: pop");
        Assert.IsFalse(core.Handle("}").Succeeded);
        Assert.Contains("error: SKIP receives incompatible stacks", Plain(core));
        Assert.Contains("[int32]", Plain(core));
        Assert.AreEqual("Bad", core.Status.OpenMethod);
        Assert.AreEqual(1, core.CellNumber);
    }

    /// <summary>
    /// .save counts the methods it wrote.
    /// </summary>
    [TestMethod]
    public void Handle_SaveWithMethods_NotesCount()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ilrepl-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var core = Load("ldc.i4 1");
            core.Handle(".save " + Path.Combine(directory, "plain.dll"));
            Assert.Contains("with IlRepl.Cell.Run\n", Plain(core) + "\n");
            Assert.DoesNotContain(" and ", core.Transcript.Lines[^1].PlainText);

            foreach (var line in Fib)
            {
                core.Handle(line);
            }

            core.Handle(".save " + Path.Combine(directory, "fib.dll"));
            Assert.Contains("with IlRepl.Cell.Run and 1 method", core.Transcript.Lines[^1].PlainText);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// .il lists the methods as ILAsm blocks beside Run.
    /// </summary>
    [TestMethod]
    public void Handle_IlWithMethods_ListsMethodBlocks()
    {
        var core = Load(Fib);
        core.Handle("ldc.i4 10");
        core.Handle("call int32 Fib(int32)");
        core.Handle(".il");
        var text = Plain(core);
        Assert.Contains(".method public static int32 Fib(int32 n) cil managed", text);
        Assert.Contains("call int32 IlRepl.Cell::Fib(int32)", text);
        Assert.Contains(".method public static object Run() cil managed", text);
    }

    /// <summary>
    /// Help lists the directive and the command.
    /// </summary>
    [TestMethod]
    public void Handle_Help_ListsMethodDirectiveAndCommand()
    {
        var core = Load(".help");
        var text = Plain(core);
        Assert.Contains(".method T Name(T a, ...) {", text);
        Assert.Contains(".methods", text);
        Assert.Contains("call int32 Fib(int32)", text);
    }

    /// <summary>
    /// While a block is open the status describes the method.
    /// </summary>
    [TestMethod]
    public void Status_ReflectsOpenMethod()
    {
        var core = Load("ldc.i4 9", ".method int32 Fib(int32 n) {", ".locals init (int32 t)", ".try {", "ldc.i4 1");
        var status = core.Status;
        Assert.AreEqual("Fib", status.OpenMethod);
        Assert.AreEqual(1, status.Locals);
        Assert.AreEqual(1, status.OpenBlocks);
        Assert.AreEqual(1, status.Instructions);
        Assert.AreEqual("[int32]", status.Stack);
        Assert.IsFalse(status.CellIsEmpty);
        Assert.AreEqual(0, status.Methods);
        Assert.AreEqual("il[1]> ", status.Prompt);
    }

    /// <summary>
    /// A close the runtime refuses is an error line, never an exception out of Handle.
    /// </summary>
    [TestMethod]
    public void Handle_RuntimeRejectedClose_ReportsError()
    {
        var core = Load(".method void Bad() {", "callvirt void Console::WriteLine()");
        Assert.IsFalse(core.Handle("}").Succeeded);
        Assert.Contains("error: the runtime rejected method Bad", Plain(core));
        Assert.AreEqual("Bad", core.Status.OpenMethod);
        Assert.AreEqual(1, core.CellNumber);
    }

    /// <summary>
    /// A close the runtime cannot prepare on this platform is an error line, never an exception
    /// out of Handle, and the block stays open.
    /// </summary>
    [TestMethod]
    public void Handle_PlatformLimitedClose_IsAnErrorLine()
    {
        var core = new ReplCore();
        core.Session.Resolver.Load(SampleHost.Samples.GreeterDll);
        foreach (var line in new[] { ".method native int Pointer() {", "ldftn vararg int32 Greeter.Hello::CountArgs()", "ret" })
        {
            core.Handle(line);
        }

        var close = core.Handle("}");
        if (OperatingSystem.IsWindows())
        {
            Assert.IsTrue(close.Succeeded);
            Assert.AreEqual(1, core.Status.Methods);
            return;
        }

        Assert.IsFalse(close.Succeeded);
        Assert.Contains("error: the runtime only supports the vararg calling convention on Windows; method Pointer cannot be prepared here", Plain(core));
        Assert.AreEqual("Pointer", core.Status.OpenMethod);
    }
}
