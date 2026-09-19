using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Checks complete offline instruction help and the runtime distinctions its explanations describe.
/// </summary>
[TestClass]
public sealed class InstructionHelpTests
{
    /// <summary>
    /// Supplies cancellation for contextual help requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Every accepted opcode has its own complete entry, including short forms and the metadata-only no. prefix.
    /// </summary>
    [TestMethod]
    public void Catalog_CoversEveryAcceptedOpcode()
    {
        var items = Completer.Catalog.Where(item => !item.Name.StartsWith('.')).ToArray();
        Assert.AreSequenceEqual(OpcodeTable.Names, items.Select(item => item.Name));
        foreach (var item in items)
        {
            var help = item.InstructionHelp;
            Assert.IsNotNull(help, item.Name);
            Assert.AreEqual(item.Name, help.Mnemonic);
            Assert.StartsWith(item.Name, help.Syntax);
            Assert.IsFalse(string.IsNullOrWhiteSpace(help.StackEffect), item.Name);
            Assert.IsFalse(string.IsNullOrWhiteSpace(help.Explanation), item.Name);
            Assert.AreEqual(help.Explanation, item.Description);
            var url = new Uri(help.DocumentationUrl, UriKind.Absolute);
            Assert.AreEqual(Uri.UriSchemeHttps, url.Scheme);
            Assert.IsFalse(string.IsNullOrEmpty(url.Fragment), item.Name);
            Assert.AreEqual(help.Syntax, Completer.Complete(item.Name).Single(match => match.Name == item.Name).InstructionHelp!.Syntax);
        }

        Assert.Contains(item => item.Name == "no.", items);
        Assert.Contains(item => item.Name == "bge.un.s", items);
        Assert.Contains(item => item.Name == "ldarg.s", items);
        Assert.DoesNotContain(item => item.Name.StartsWith("prefix", StringComparison.Ordinal), items);
        Assert.DoesNotContain(item => item.InstructionHelp is not null, Completer.Commands);
    }

    /// <summary>
    /// Arithmetic instructions explain the signed overflow boundary that their emitted code observes.
    /// </summary>
    /// <param name="opcode">The addition instruction.</param>
    /// <param name="throws">Whether the signed maximum plus one must overflow.</param>
    [TestMethod]
    [DataRow("add", false)]
    [DataRow("add.ovf", true)]
    [DataRow("add.ovf.un", false)]
    public void ArithmeticHelp_MatchesSignedOverflowBoundary(string opcode, bool throws)
    {
        var help = Help(opcode);
        Assert.Contains("overflow", Prose(help));
        if (opcode.EndsWith(".un", StringComparison.Ordinal))
        {
            Assert.Contains("unsigned", Prose(help));
        }

        var session = Load("ldc.i4 2147483647", "ldc.i4.1", opcode);
        if (throws)
        {
            var failure = Assert.ThrowsExactly<CellException>(() => session.Run());
            Assert.IsInstanceOfType<OverflowException>(failure.InnerException);
        }
        else
        {
            Assert.AreEqual(int.MinValue, session.Run().Value);
        }
    }

    /// <summary>
    /// Unsigned checked arithmetic detects overflow at the unsigned boundary without confusing it with signed overflow.
    /// </summary>
    [TestMethod]
    public void UnsignedArithmeticHelp_MatchesUnsignedOverflowBoundary()
    {
        var help = Help("add.ovf.un");
        Assert.Contains("unsigned", Prose(help));
        var session = Load("ldc.i4.m1", "ldc.i4.1", "add.ovf.un");
        var failure = Assert.ThrowsExactly<CellException>(() => session.Run());
        Assert.IsInstanceOfType<OverflowException>(failure.InnerException);
    }

    /// <summary>
    /// Conversion help distinguishes the unsigned source modifier from the signed destination type.
    /// </summary>
    [TestMethod]
    public void ConversionHelp_DistinguishesSourceAndDestination()
    {
        var help = Help("conv.ovf.i8.un");
        Assert.Contains("unsigned", Prose(help));
        Assert.Contains("source", Prose(help));
        Assert.Contains("int64", Prose(help));
        var signed = Load("ldc.i4.m1", "conv.ovf.i8");
        var unsigned = Load("ldc.i4.m1", "conv.ovf.i8.un");
        Assert.AreEqual(-1L, signed.Run().Value);
        Assert.AreEqual(4294967295L, unsigned.Run().Value);
    }

    /// <summary>
    /// Ordered and unordered comparisons explain the different result for a NaN operand.
    /// </summary>
    /// <param name="opcode">The comparison instruction.</param>
    /// <param name="expected">The result when the left operand is NaN.</param>
    [TestMethod]
    [DataRow("cgt", 0)]
    [DataRow("cgt.un", 1)]
    [DataRow("clt", 0)]
    [DataRow("clt.un", 1)]
    public void ComparisonHelp_MatchesUnorderedResult(string opcode, int expected)
    {
        var help = Help(opcode);
        Assert.Contains("nan", Prose(help));
        var session = Load("ldc.r8 nan", "ldc.r8 1", opcode);
        Assert.AreEqual(expected, session.Run().Value);
    }

    /// <summary>
    /// Right-shift help distinguishes sign extension from zero fill and preserves operand order.
    /// </summary>
    [TestMethod]
    public void ShiftHelp_DistinguishesArithmeticAndLogicalResults()
    {
        Assert.Contains("sign", Prose(Help("shr")));
        Assert.Contains("zero", Prose(Help("shr.un")));
        var arithmetic = Load("ldc.i4.m1", "ldc.i4.1", "shr");
        var logical = Load("ldc.i4.m1", "ldc.i4.1", "shr.un");
        Assert.AreEqual(-1, arithmetic.Run().Value);
        Assert.AreEqual(int.MaxValue, logical.Run().Value);
    }

    /// <summary>
    /// Calls explain direct dispatch, virtual dispatch, null checking, and nonvirtual instance targets.
    /// </summary>
    [TestMethod]
    public void CallHelp_ExplainsDispatchAndNonvirtualTargets()
    {
        Assert.Contains("direct", Prose(Help("call")));
        var virtualCall = Prose(Help("callvirt"));
        Assert.Contains("virtual", virtualCall);
        Assert.Contains("nonvirtual", virtualCall.Replace("non-virtual", "nonvirtual", StringComparison.Ordinal));
        Assert.Contains("null", virtualCall);
        var session = Load("ldstr \"abc\"", "callvirt instance int32 string::get_Length()");
        Assert.AreEqual(3, session.Run().Value);
        var direct = Load("ldstr \"value\"", "call instance string object::ToString()");
        var dispatched = Load("ldstr \"value\"", "callvirt instance string object::ToString()");
        Assert.AreEqual("System.String", direct.Run().Value);
        Assert.AreEqual("value", dispatched.Run().Value);
    }

    /// <summary>
    /// Boxing and unboxing help distinguishes copying, nullable values, generic operands, and managed addresses.
    /// </summary>
    [TestMethod]
    public void BoxingHelp_DistinguishesValuesAndAddresses()
    {
        var box = Prose(Help("box"));
        Assert.Contains("cop", box);
        Assert.Contains("nullable", box);
        Assert.Contains("generic", box);
        Assert.Contains("managed pointer", Prose(Help("unbox")));
        Assert.Contains("value", Prose(Help("unbox.any")));
        var address = Load("ldc.i4.s 42", "box int32", "unbox int32");
        var value = Load("ldc.i4.s 42", "box int32", "unbox.any int32");
        Assert.AreEqual("[int32&]", address.State.StackText);
        Assert.AreEqual("[int32]", value.State.StackText);
        address.AddLine("ldind.i4");
        Assert.AreEqual(42, address.Run().Value);
        Assert.AreEqual(42, value.Run().Value);
    }

    /// <summary>
    /// Nullable boxing follows the underlying value state while generic boxing follows the actual type argument.
    /// </summary>
    [TestMethod]
    public void BoxingHelp_MatchesNullableAndGenericOperands()
    {
        const string nullable = "valuetype [System.Runtime]System.Nullable`1<int32>";
        var empty = Load($".locals init ({nullable} value)", "ldloc value", $"box {nullable}");
        Assert.IsNull(empty.Run().Value);
        var present = Load("ldc.i4.s 42", $"newobj instance void {nullable}::.ctor(!0)", $"box {nullable}");
        Assert.AreEqual(42, present.Run().Value);
        var generic = Load(".typeparams (T)", ".typeargs (int32)", "ldc.i4.s 42", "box int32", "unbox.any !!T", "box !!T");
        Assert.AreEqual(42, generic.Run().Value);
        Assert.Contains("nullable", Prose(Help("box")));
        Assert.Contains("generic", Prose(Help("box")));
    }

    /// <summary>
    /// Prefix help describes valid targets and requirements without promising elided checks or atomic memory access.
    /// </summary>
    /// <param name="opcode">The prefix.</param>
    /// <param name="target">An essential target or requirement.</param>
    [TestMethod]
    [DataRow("constrained.", "callvirt")]
    [DataRow("readonly.", "ldelema")]
    [DataRow("tail.", "ret")]
    [DataRow("unaligned.", "1, 2, or 4")]
    [DataRow("volatile.", "atomic")]
    [DataRow("no.", "check")]
    public void PrefixHelp_ExplainsRequirements(string opcode, string target)
    {
        var help = Help(opcode);
        Assert.Contains(target, Prose(help));
        Assert.IsNotEmpty(help.Notes);
        if (opcode == "constrained.")
        {
            Assert.Contains("static virtual", Prose(help));
            Assert.Contains("ldftn", Prose(help));
        }

        if (opcode == "readonly.")
        {
            Assert.Contains("address", Prose(help));
        }

        if (opcode == "no.")
        {
            Assert.Contains("unverifiable", Prose(help));
            Assert.Contains("optional", Prose(help));
        }

        if (opcode == "volatile.")
        {
            Assert.Contains("does not make", Prose(help));
        }
    }

    /// <summary>
    /// Cached caret movement picks the current instruction and does not leak a previous instruction into declarations.
    /// </summary>
    [TestMethod]
    public async Task CaretHelp_TracksInstructionAcrossCachedPositions()
    {
        await using var engine = new InProcessEngine();
        string[] lines = [".method int32 F() {", "ldc.i4.s -42", "call int32 Math::Abs(int32)", "ret", "}"];
        var call = await engine.AnalyzeAsync(new(lines, 2, 8, 1), TestContext.CancellationToken);
        Assert.IsNotNull(call.InstructionHelp);
        Assert.AreEqual("call", call.InstructionHelp.Mnemonic);
        Assert.Contains("Abs(int32)", call.InstructionHelp.Syntax);
        Assert.Contains("int32", call.InstructionHelp.StackEffect);
        var constant = await engine.AnalyzeAsync(new(lines, 1, 3, 2), TestContext.CancellationToken);
        Assert.IsNotNull(constant.InstructionHelp);
        Assert.AreEqual("ldc.i4.s", constant.InstructionHelp.Mnemonic);
        Assert.AreEqual(2, constant.DocumentVersion);
        var header = await engine.AnalyzeAsync(new(lines, 0, 3, 3), TestContext.CancellationToken);
        Assert.IsNull(header.InstructionHelp);
    }

    /// <summary>
    /// Unresolved operands still expose generic opcode help without claiming to have resolved a target signature.
    /// </summary>
    [TestMethod]
    public async Task IncompleteOperand_ProvidesGenericInstructionHelp()
    {
        await using var engine = new InProcessEngine();
        const string text = "call MissingType::";
        var reply = await engine.AnalyzeAsync(new([text], 0, text.Length, 1), TestContext.CancellationToken);
        Assert.IsNotNull(reply.InstructionHelp);
        Assert.AreEqual("call", reply.InstructionHelp.Mnemonic);
        Assert.DoesNotContain("MissingType", reply.InstructionHelp.Syntax);
        Assert.Contains(item => item.Kind == AnalysisDiagnosticKind.Incomplete, reply.Diagnostics);
    }

    /// <summary>
    /// Resolved operand help retains the chosen overload and explains when an instance target cannot dispatch virtually.
    /// </summary>
    [TestMethod]
    public async Task OperandHelp_PreservesSignatureAndExplainsResolvedDispatch()
    {
        await using var engine = new InProcessEngine();
        const string prefix = "callvirt string::get_Len";
        var completed = await engine.CompleteAsync(new(["ldstr \"abc\"", prefix], 1, prefix.Length, null, []),
            TestContext.CancellationToken);
        var candidate = Assert.ContainsSingle(completed.Items);
        Assert.IsNotNull(candidate.InstructionHelp);
        Assert.IsNotNull(candidate.FullDetail);
        Assert.Contains("string::get_Length()", candidate.FullDetail);
        Assert.Contains("string::get_Length()", candidate.InstructionHelp.Syntax);
        Assert.Contains("int32", candidate.InstructionHelp.StackEffect);
        Assert.Contains("nonvirtual", Prose(candidate.InstructionHelp).Replace("non-virtual", "nonvirtual", StringComparison.Ordinal));
        Assert.Contains("does not select an override", Prose(candidate.InstructionHelp));
    }

    private static InstructionHelp Help(string opcode)
    {
        var help = Completer.Catalog.Single(item => item.Name == opcode).InstructionHelp;
        Assert.IsNotNull(help, opcode);
        return help;
    }

    private static string Prose(InstructionHelp help) => (help.Explanation + " " + string.Join(' ', help.Notes)).ToLowerInvariant();

    private static Session Load(params string[] lines)
    {
        var session = new Session();
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        return session;
    }
}
