using System.Reflection;
using System.Runtime.Serialization.Json;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Operand pages preserve the identities, syntax and editing context accepted by real input.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OperandCompleterTests
{
    /// <summary>
    /// Supplies cancellation for the asynchronous completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Case correction and every matching tier offer the same executable public overload.
    /// </summary>
    [TestMethod]
    [DataRow("call Console::Wr")]
    [DataRow("call console::wr")]
    [DataRow("call Console::WL")]
    [DataRow("call Console::iteLi")]
    public async Task MethodMatches_SelectTheIntendedOverload(string line)
    {
        var session = new Session();
        using var completer = new OperandCompleter(session);
        var reply = await Complete(completer, [line]);
        var item = reply.Items.Single(item => item.InsertText == "Console::WriteLine(string)");
        var text = Apply(line, reply, item);
        var member = MemberResolver.ResolveMethod(text[5..], session.State.Context, false);
        Assert.AreEqual(typeof(Console).GetMethod(nameof(Console.WriteLine), [typeof(string)]), member.Method);
        Assert.AreEqual("[string] → []", item.Detail);
        Assert.AreEqual(0, session.Submissions);
        Assert.IsTrue(session.State.IsEmpty);
    }

    /// <summary>
    /// Replacement ranges preserve a reference's following comment and replace its existing suffix once.
    /// </summary>
    [TestMethod]
    public async Task MidReference_ReplacesTheWholeReference()
    {
        const string line = "call console::WrXXX(int32) // keep";
        var session = new Session();
        using var completer = new OperandCompleter(session);
        var reply = await Complete(completer, [line], line.IndexOf("XXX", StringComparison.Ordinal));
        var item = reply.Items.Single(item => item.InsertText == "Console::WriteLine(string)");
        Assert.AreEqual("call Console::WriteLine(string) // keep", Apply(line, reply, item));
    }

    /// <summary>
    /// A type completed before an existing member separator preserves that separator and the method text.
    /// </summary>
    [TestMethod]
    public async Task MidQualifier_PreservesTheExistingSeparator()
    {
        const string line = "call conso::WriteLine(string)";
        using var completer = new OperandCompleter(new Session());
        var reply = await Complete(completer, [line], line.IndexOf("::", StringComparison.Ordinal));
        var item = reply.Items.Single(item => item.InsertText == "Console");
        Assert.AreEqual("call Console::WriteLine(string)", Apply(line, reply, item));
        Assert.IsFalse(item.Continues);
    }

    /// <summary>
    /// A constructor candidate binds in its short form and describes the newly constructed instance.
    /// </summary>
    [TestMethod]
    public async Task Constructor_OffersOnlyConstructors()
    {
        const string line = "newobj Exception::";
        var session = new Session();
        using var completer = new OperandCompleter(session);
        var reply = await Complete(completer, [line]);
        Assert.IsNotEmpty(reply.Items);
        Assert.IsTrue(reply.Items.All(item => item.Name.StartsWith(".ctor(", StringComparison.Ordinal)));
        var item = reply.Items.Single(item => item.Name == ".ctor(string)");
        Assert.AreEqual("[string] → Exception", item.Detail);
        var instruction = InstructionParser.Parse(Apply(line, reply, item), session.State.Context);
        Assert.AreEqual("newobj", instruction.Op.Name);
    }

    /// <summary>
    /// Method and field tokens describe their handles while function pointers describe native integers.
    /// </summary>
    [TestMethod]
    [DataRow("ldtoken method Console::WriteLine", "WriteLine(string)", "[] → RuntimeMethodHandle")]
    [DataRow("ldftn Console::WriteLine", "WriteLine(string)", "[] → native int")]
    [DataRow("ldtoken field string::Empty", "Empty", "[] → RuntimeFieldHandle")]
    [DataRow("ldsfld string::Empty", "Empty", "[] → string")]
    public async Task OpcodeDetails_FollowTheBoundOperand(string line, string name, string expected)
    {
        using var completer = new OperandCompleter(new Session());
        var reply = await Complete(completer, [line]);
        Assert.AreEqual(expected, reply.Items.Single(item => item.Name == name).Detail);
    }

    /// <summary>
    /// An instruction with an incomplete stack still discovers eligible instance members and rejects static callvirt targets.
    /// </summary>
    [TestMethod]
    public async Task Callvirt_UsesMemberEligibilityWithoutStackUnderflowFiltering()
    {
        using var completer = new OperandCompleter(new Session());
        var instance = await Complete(completer, ["callvirt string::Substr"]);
        Assert.Contains(item => item.Name == "Substring(int32)", instance.Items);
        var invalid = await Complete(completer, ["callvirt Console::WriteLine"]);
        Assert.IsEmpty(invalid.Items);
    }

    /// <summary>
    /// Unsent variable declarations provide named slots while leaving the real cell unchanged.
    /// </summary>
    [TestMethod]
    public async Task UnsentVariables_CompleteAndBindAfterSubmission()
    {
        var session = new Session();
        using var completer = new OperandCompleter(session);
        string[] lines = [".locals init (int32 value)", "ldloc val"];
        var reply = await Complete(completer, lines);
        var item = reply.Items.Single();
        Assert.AreEqual("'value'", item.InsertText);
        Assert.AreEqual("[] → int32", item.Detail);
        Assert.IsEmpty(session.State.Locals);
        session.AddLine(lines[0]);
        session.AddLine(Apply(lines[1], reply, item));
        Assert.AreEqual("[int32]", session.State.Stack.Render());
    }

    /// <summary>
    /// A private field is available inside its unsent owner and excluded from the cell.
    /// </summary>
    [TestMethod]
    public async Task OpenType_UsesTheBodyAccessContext()
    {
        using var completer = new OperandCompleter(new Session());
        string[] lines = [".class public Box {", ".field private int32 hidden", ".method public int32 Read() {", "ldfld Box::hi"];
        var reply = await Complete(completer, lines);
        Assert.AreEqual("hidden", reply.Items.Single().Name);
        Assert.AreEqual("[Box] → int32", reply.Items.Single().Detail);
    }

    /// <summary>
    /// A forward label in the same body remains discoverable without accepting the unfinished branch.
    /// </summary>
    [TestMethod]
    public async Task Labels_IncludeFutureLinesOfThisBody()
    {
        using var completer = new OperandCompleter(new Session());
        string[] lines = ["br DO", "DONE: nop"];
        var request = new CompletionRequest(lines, 0, lines[0].Length, null, []);
        var reply = await completer.CompleteAsync(request, TestContext.CancellationToken);
        Assert.AreEqual("DONE", reply.Items.Single().InsertText);
    }

    /// <summary>
    /// Type arguments continue a selected generic method and its completed signature binds through real input.
    /// </summary>
    [TestMethod]
    public async Task GenericMethod_CompletesArgumentsThenSignature()
    {
        var session = new Session();
        using var completer = new OperandCompleter(session);
        const string original = "call Array::Em";
        var first = await Complete(completer, [original]);
        var starter = first.Items.Single(item => item.Continues);
        Assert.AreEqual("Array::Empty<", starter.InsertText);
        Assert.IsNotNull(starter.Continuation);
        var text = Apply(original, first, starter) + "str";
        var anchor = new ContinuationAnchor(0, first.ReplaceStart, first.ReplaceStart + starter.InsertText.Length, starter.Continuation);
        var second = await completer.CompleteAsync(new CompletionRequest([text], 0, text.Length, null, [anchor]),
            TestContext.CancellationToken);
        var argument = second.Items.Single(item => item.InsertText == "string");
        text = Apply(text, second, argument) + ">";
        var third = await completer.CompleteAsync(new CompletionRequest([text], 0, text.Length, null, [anchor]),
            TestContext.CancellationToken);
        var signature = third.Items.Single();
        text = Apply(text, third, signature);
        Assert.AreEqual("call Array::Empty<string>()", text);
        session.AddLine(text);
        Assert.AreEqual("[string[]]", session.State.Stack.Render());
    }

    /// <summary>
    /// A type definition offers an anchored construction without needing to bind an unfinished instruction.
    /// </summary>
    [TestMethod]
    public async Task GenericType_OffersConstructionAndOpenToken()
    {
        using var completer = new OperandCompleter(new Session());
        var reply = await Complete(completer, ["ldtoken List"]);
        Assert.Contains(item => item.InsertText == "List<" && item.Continuation is not null, reply.Items);
        Assert.Contains(item => item.InsertText == "List`1" && !item.Continues, reply.Items);
    }

    /// <summary>
    /// A selected unsent generic overload survives replay after edits to unrelated cell instructions.
    /// </summary>
    [TestMethod]
    public async Task GenericAnchor_ReplayedDeclarations_KeepTheChosenArity()
    {
        // Warm serialization dependencies before asserting that replay alone preserves the binding epoch.
        new DataContractJsonSerializer(typeof(string)).WriteObject(Stream.Null, "");
        Assembly.Load("System.Runtime.Serialization.Primitives");
        var session = new Session();
        using var completer = new OperandCompleter(session);
        string[] lines = ["nop", ".class public Host {",
            ".method public static int32 Make<T>() {", "ldc.i4.1", "ret", "}",
            ".method public static int32 Make<T, U>() {", "ldc.i4.2", "ret", "}", "}", "call Host::Mak"];
        using var editing = new EditingSession(new Session());
        var view = editing.Speculate(lines, lines.Length - 1, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines, string.Join("; ", view.SkippedLines.Select(line => line.Message)));
        var scope = (SnapshotBindingScope)view.Scope;
        var owner = scope.LookupType("Host", null, 0, false).Type;
        foreach (var method in scope.Methods(owner, "Make"))
        {
            var bound = SymbolBinder.BindMethodReference(
                CilSyntaxParser.ParseMethodReference($"Host::Make<[{method.Arity}]>()"), scope, false);
            Assert.IsTrue(MemberSpeller.SameMethod(bound.Method, method), $"{bound.Method.Definition} / {method.Definition}");
            Assert.IsNotNull(new MemberSpeller(scope).TrySpell(method, CompletionSite.None), scope.Describe(method));
        }

        var first = await Complete(completer, lines);
        var assemblies = session.Resolver.Assemblies.ToArray();
        Assert.HasCount(2, first.Items);
        var starter = first.Items.Single(item => item.FullDetail!.Contains("<[2]>", StringComparison.Ordinal));
        Assert.IsNotNull(starter.Continuation);
        lines[^1] = Apply(lines[^1], first, starter) + "str";
        var anchor = new ContinuationAnchor(lines.Length - 1, first.ReplaceStart,
            first.ReplaceStart + starter.InsertText.Length, starter.Continuation);
        for (var edit = 0; edit < 100; edit++)
        {
            lines[0] = "nop // " + edit;
            var reply = await completer.CompleteAsync(new CompletionRequest(lines, lines.Length - 1, lines[^1].Length, null, [anchor]),
                TestContext.CancellationToken);
            Assert.AreEqual(first.BindingEpoch, reply.BindingEpoch, $"binding changed during replay {edit}; new assemblies: "
                + string.Join(", ", session.Resolver.Assemblies.Except(assemblies).Select(assembly => assembly.FullName)));
            Assert.AreEqual("string", reply.Items[0].InsertText, "Keyword aliases rank by the case they insert.");
            Assert.HasCount(1, reply.Owners, "Replay must retain the selected two-argument overload.");
            Assert.Contains("U", reply.Owners[0]);
        }
    }

    /// <summary>
    /// Invalid completed generic arguments yield no parameter lists without faulting later queries.
    /// </summary>
    [TestMethod]
    public async Task GenericSignature_InvalidArguments_RecoversOnCorrection()
    {
        using var completer = new OperandCompleter(new Session());
        var invalid = await Complete(completer, ["call Array::Empty<void>"]);
        Assert.IsEmpty(invalid.Items);
        var valid = await Complete(completer, ["call Array::Empty<string>"]);
        Assert.AreEqual("()", valid.Items.Single().InsertText);
    }

    /// <summary>
    /// A misspelled completed sibling cannot make a generic construction appear valid.
    /// </summary>
    [TestMethod]
    public async Task GenericArguments_InvalidSibling_RequiresCorrection()
    {
        using var completer = new OperandCompleter(new Session());
        var invalid = await Complete(completer, ["ldtoken Dictionary<NoSuchType, str"]);
        Assert.IsEmpty(invalid.Items);
        var valid = await Complete(completer, ["ldtoken Dictionary<int32, str"]);
        Assert.Contains(item => item.InsertText == "string", valid.Items);
    }

    /// <summary>
    /// Correcting a qualifier's case preserves the resolver's common-namespace preference.
    /// </summary>
    [TestMethod]
    public async Task Qualifier_CaseCorrection_PreservesShortNameResolution()
    {
        using var completer = new OperandCompleter(new Session());
        var reply = await Complete(completer, ["call console::writeline"]);
        Assert.IsNotEmpty(reply.Items);
        Assert.IsTrue(reply.Items.All(item => item.Description == "System.Console"));
        Assert.AreEqual("Console::WriteLine()", reply.Items[0].InsertText);
    }

    private Task<CompletionReply> Complete(OperandCompleter completer, string[] lines, int? caret = null) =>
        completer.CompleteAsync(new CompletionRequest(lines, lines.Length - 1, caret ?? lines[^1].Length, null, []),
            TestContext.CancellationToken);

    private static string Apply(string line, CompletionReply reply, CompletionItem item) =>
        line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..];
}
