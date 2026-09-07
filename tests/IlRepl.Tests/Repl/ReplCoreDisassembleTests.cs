using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Tests for the <c>.dis</c> command.
/// </summary>
[TestClass]
public sealed class ReplCoreDisassembleTests
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

    private static ReplCore Load(params string[] lines)
    {
        var core = new ReplCore();
        foreach (var line in lines)
        {
            core.Handle(line);
        }

        return core;
    }

    private static List<string> Listing(ReplCore core, string command)
    {
        var before = core.Transcript.Lines.Count;
        var result = core.Handle(command);
        Assert.IsTrue(result.Succeeded, string.Join("\n", core.Transcript.Lines.Skip(before).Select(l => l.PlainText)));
        return core.Transcript.Lines.Skip(before).Where(l => l.Kind == LineKind.Listing).Select(l => l.PlainText).ToList();
    }

    private static string Errors(ReplCore core, string command)
    {
        var before = core.Transcript.Lines.Count;
        var result = core.Handle(command);
        Assert.IsFalse(result.Succeeded, command);
        return string.Join("\n", core.Transcript.Lines.Skip(before).Where(l => l.Kind == LineKind.Error).Select(l => l.PlainText));
    }

    /// <summary>
    /// A framework method lists with its header, maxstack, hex offsets, a stack column, and a closing brace.
    /// </summary>
    [TestMethod]
    public void Handle_Dis_FrameworkMethod_ListsBody()
    {
        var core = new ReplCore();
        var lines = Listing(core, ".dis instance string [System.Runtime]System.String::Trim()");
        Assert.AreEqual("  .method public hidebysig instance string Trim() cil managed {", lines[0]);
        Assert.StartsWith("  .maxstack ", lines[1]);
        Assert.Contains(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^  [0-9a-f]{4} \S.* \[.*\]$|^  [0-9a-f]{4} \S.* \?$"), lines);
        Assert.AreEqual("  }", lines[^1]);
        Assert.Contains(l => l.Kind == LineKind.Info && l.PlainText.StartsWith("  code size ", StringComparison.Ordinal), core.Transcript.Lines);

        // The short spelling resolves the same method.
        var shortLines = Listing(core, ".dis String::Trim()");
        Assert.AreEqual(lines[0], shortLines[0]);
    }

    /// <summary>
    /// A session method lists by bare name and by signature, with the same text.
    /// </summary>
    [TestMethod]
    public void Handle_Dis_SessionMethod_ByNameAndSignature()
    {
        var core = Load(Fib);
        var byName = Listing(core, ".dis Fib");
        var bySignature = Listing(core, ".dis int32 Fib(int32)");
        Assert.AreSequenceEqual(byName, bySignature);
        var text = string.Join("\n", byName);
        Assert.Contains("  .method public hidebysig static int32 Fib(int32 n) cil managed {", text);
        Assert.Contains("call int32 Fib(int32)", text);
        Assert.Contains("ldarg 0", text);
        Assert.Contains("\nIL_", text);
        Assert.Contains(l => l.StartsWith("  0000 ", StringComparison.Ordinal), byName);
        Assert.DoesNotContain("IlRepl.Cell", text);
    }

    /// <summary>
    /// .dis works while another method is open and leaves the block open.
    /// </summary>
    [TestMethod]
    public void Handle_Dis_WhileMethodOpen_LeavesBlockOpen()
    {
        var core = Load(Fib);
        core.Handle(".method int32 Other() {");
        core.Handle("ldc.i4 1");
        var lines = Listing(core, ".dis Fib");
        Assert.IsNotEmpty(lines);
        Assert.AreEqual("Other", core.Status.OpenMethod);
        Assert.IsTrue(core.Handle("ret").Succeeded);
        Assert.IsTrue(core.Handle("}").Succeeded);
        Assert.AreEqual(2, core.Status.Methods);
    }

    /// <summary>
    /// A member of the class being written is refused with the hint to close it, and the class still closes.
    /// </summary>
    [TestMethod]
    public void Handle_Dis_OpenClassMember_IsRefused_AndClassCloses()
    {
        var core = Load(".class public C {", ".method public static int32 M() {", "ldc.i4 1", "ret", "}");
        var error = Errors(core, ".dis int32 C::M()");
        Assert.Contains("C", error);
        Assert.IsTrue(core.Handle("}").Succeeded);
        var lines = Listing(core, ".dis int32 C::M()");
        Assert.Contains("  .method public static int32 M() cil managed {", lines);

        // A missing member of a class being written is not declared by the lookup either.
        core.Handle(".class public D {");
        core.Handle(".method public static void N() {");
        _ = Errors(core, ".dis void D::Missing()");
        Assert.IsTrue(core.Handle("ret").Succeeded);
        Assert.IsTrue(core.Handle("}").Succeeded);
        Assert.IsTrue(core.Handle("}").Succeeded);
        Assert.AreEqual(2, core.Status.Types);
    }

    /// <summary>
    /// A member of a closed class lists with unqualified session references.
    /// </summary>
    [TestMethod]
    public void Handle_Dis_ClosedClassMember_Lists()
    {
        var core = Load(".class public Point {", ".field public int32 X", ".method public instance int32 Get() {", "ldarg.0", "ldfld int32 Point::X", "ret", "}", "}");
        var text = string.Join("\n", Listing(core, ".dis instance int32 Point::Get()"));
        Assert.Contains("ldfld int32 Point::X", text);
        Assert.Contains("[Point]", text);
    }

    /// <summary>
    /// A generic instantiation lists its definition and says so.
    /// </summary>
    [TestMethod]
    public void Handle_Dis_GenericInstantiation_NotesDefinition()
    {
        var core = new ReplCore();
        var before = core.Transcript.Lines.Count;
        Assert.IsTrue(core.Handle(".dis instance void class List`1<int32>::Add(!0)").Succeeded);
        var notes = core.Transcript.Lines.Skip(before).Where(l => l.Kind == LineKind.Info).Select(l => l.PlainText).ToList();
        Assert.Contains(n => n.Contains("showing the definition", StringComparison.Ordinal), notes);
        var listing = core.Transcript.Lines.Skip(before).Where(l => l.Kind == LineKind.Listing).Select(l => l.PlainText).ToList();
        Assert.Contains("!0", string.Join("\n", listing));
    }

    /// <summary>
    /// A method with try, catch, and finally lists the same block lines .show printed while it was open.
    /// </summary>
    [TestMethod]
    public void Handle_Dis_TryCatchFinally_MatchesShowBlocks()
    {
        var core = Load(".method string Guarded() {", ".locals init (string message)", ".try {", "ldstr \"boom\"", "newobj instance void InvalidOperationException::.ctor(string)", "throw",
            "} catch InvalidOperationException {", "callvirt instance string Exception::get_Message()", "stloc message", "leave DONE", "} finally {", "ldstr \"finally ran\"", "call void Console::WriteLine(string)", "}", "DONE: ldloc message", "ret");
        var shown = Listing(core, ".show").Where(l => l.TrimStart().StartsWith('.') || l.TrimStart().StartsWith('}')).Select(l => l.Trim()).Where(l => l != ".locals init (string message)" && !l.StartsWith(".method", StringComparison.Ordinal)).ToList();
        core.Handle("}");
        var disassembled = Listing(core, ".dis Guarded").Where(l => l.TrimStart().StartsWith(".try") || l.TrimStart().StartsWith('}')).Select(l => l.Trim()).ToList();
        Assert.AreSequenceEqual(shown, disassembled.Take(shown.Count));
        Assert.AreEqual("}", disassembled[^1]);
    }

    /// <summary>
    /// Errors name what went wrong: no argument, an unknown method, an ambiguous one, an abstract one.
    /// </summary>
    [TestMethod]
    public void Handle_Dis_Errors()
    {
        var core = new ReplCore();
        Assert.Contains("usage: .dis", Errors(core, ".dis"));
        Assert.Contains("Nope", Errors(core, ".dis Nope"));
        Assert.Contains("is abstract", Errors(core, ".dis instance void [System.Runtime]System.IO.Stream::Flush()"));
        Assert.Contains("ambiguous", Errors(core, ".dis System.Object::Equals").ToLowerInvariant());
        Assert.IsTrue(core.Handle("ldc.i4 1").Succeeded);
        Assert.IsTrue(core.Handle(".undo").Succeeded);
    }

    /// <summary>
    /// After .load a method of the loaded assembly lists, its vararg call site included.
    /// </summary>
    [TestMethod]
    public void Handle_Dis_LoadedAssembly_Lists()
    {
        var core = new ReplCore();
        Assert.IsTrue(core.Handle(".load " + SampleHost.Samples.GreeterDll).Succeeded);
        var text = string.Join("\n", Listing(core, ".dis int32 Greeter.Hello::CallCountArgs()"));
        Assert.Contains("call vararg int32 [Greeter]Greeter.Hello::CountArgs(..., int32)", text);
    }

    /// <summary>
    /// Help and completion know the command.
    /// </summary>
    [TestMethod]
    public void Handle_Help_ListsDis()
    {
        var core = new ReplCore();
        core.Handle(".help");
        Assert.Contains(l => l.PlainText.Contains(".dis <method>", StringComparison.Ordinal), core.Transcript.Lines);
        Assert.Contains(i => i.Name == ".dis" && i.TakesOperand, Completer.Catalog);
    }

    /// <summary>
    /// The listing of a session method pastes back into a fresh method: the emitter forces zeroed
    /// locals and writes its own transitions, and the pasted method runs to the same result.
    /// </summary>
    [TestMethod]
    public void Handle_Dis_Listing_PastesBackIntoAMethod()
    {
        var core = Load(Fib);
        foreach (var line in new[] { ".method int32 Safe(int32 d) {", ".locals init (int32 n)", ".try {", "ldc.i4 1", "ldarg d", "div", "stloc n", "leave END", "} filter {", "isinst DivideByZeroException", "ldnull", "cgt.un", "endfilter", "} handler {", "pop", "ldc.i4 42", "stloc n", "leave END", "}", "END: ldloc n", "ret", "}" })
        {
            core.Handle(line);
        }

        foreach (var (name, header, argument, expected) in new[] { ("Fib", ".method int32 Fib2(int32 n) {", "10", "55"), ("Safe", ".method int32 Safe2(int32 d) {", "0", "42") })
        {
            var before = core.Transcript.Lines.Count;
            Assert.IsTrue(core.Handle(".dis " + name).Succeeded);
            var listing = core.Transcript.Lines.Skip(before).Where(l => l.Kind == LineKind.Listing).ToList();
            var pasted = new List<string>();
            foreach (var line in listing.Skip(1).Take(listing.Count - 2))
            {
                var text = line.PlainText.Trim();
                if (text.StartsWith(".maxstack", StringComparison.Ordinal))
                {
                    continue;
                }

                // An instruction line is three spans: the offset, the text, and the stack column.
                var offsetColumn = line.Spans.Count == 3 && System.Text.RegularExpressions.Regex.IsMatch(line.Spans[0].Text, "^  [0-9a-f]{4} $");
                pasted.Add(offsetColumn ? line.Spans[1].Text.Trim() : text);
            }

            Assert.Contains(l => l.StartsWith(".locals init", StringComparison.Ordinal) || name == "Fib", pasted, "the emitter forces zeroed locals, which the listing shows");
            Assert.IsTrue(core.Handle(header).Succeeded, header);
            foreach (var line in pasted)
            {
                var result = core.Handle(line);
                Assert.IsTrue(result.Succeeded, line + "\n" + string.Join("\n", core.Transcript.Lines.TakeLast(3).Select(l => l.PlainText)));
            }

            Assert.IsTrue(core.Handle("}").Succeeded, "closing the pasted method\n" + string.Join("\n", core.Transcript.Lines.TakeLast(3).Select(l => l.PlainText)));
            var run = core.Transcript.Lines.Count;
            core.Handle("ldc.i4 " + argument);
            core.Handle("call int32 " + header.Split(' ')[2][..header.Split(' ')[2].IndexOf('(')] + "(int32)");
            core.Handle("ret");
            var results = core.Transcript.Lines.Skip(run).Where(l => l.Kind == LineKind.Result).Select(l => l.PlainText).ToList();
            Assert.HasCount(1, results, string.Join("\n", core.Transcript.Lines.Skip(run).Select(l => l.PlainText)));
            Assert.Contains(expected, results[0]);
        }
    }
}
