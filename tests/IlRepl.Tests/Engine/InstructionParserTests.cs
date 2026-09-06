using System.Reflection.Emit;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="InstructionParser"/>.
/// </summary>
[TestClass]
public sealed class InstructionParserTests
{
    private static readonly ParseContext Empty = new([], [], GenericContext.Empty, new TypeResolver(), []);

    /// <summary>
    /// Comments are stripped without touching string literals.
    /// </summary>
    [TestMethod]
    public void StripComments_KeepsSlashesInsideStrings()
    {
        Assert.AreEqual("ldstr \"http://x\" ", InstructionParser.StripComments("ldstr \"http://x\" // comment"));
        Assert.AreEqual("add  ", InstructionParser.StripComments("add /* inline */ "));
    }

    /// <summary>
    /// Leading labels are split off and member separators are left alone.
    /// </summary>
    [TestMethod]
    public void SplitLabels_LabelsAndMemberSeparator()
    {
        var (labels, rest) = InstructionParser.SplitLabels("A: B: call void Console::WriteLine()");
        Assert.HasCount(2, labels);
        Assert.AreEqual("call void Console::WriteLine()", rest);
    }

    /// <summary>
    /// Immediates are parsed into the operand kind the emitter expects.
    /// </summary>
    /// <param name="text">The instruction.</param>
    /// <param name="kind">The expected operand kind.</param>
    [TestMethod]
    [DataRow("ldc.i4 7", OperandKind.Int32)]
    [DataRow("ldc.i4.s -3", OperandKind.SByte)]
    [DataRow("ldc.i8 5", OperandKind.Int64)]
    [DataRow("ldc.r4 1.5", OperandKind.Single)]
    [DataRow("ldc.r8 2", OperandKind.Double)]
    [DataRow("ldstr \"x\"", OperandKind.String)]
    [DataRow("br END", OperandKind.Label)]
    [DataRow("switch (A, B)", OperandKind.Labels)]
    [DataRow("box int32", OperandKind.Type)]
    [DataRow("call int32 Math::Max(int32, int32)", OperandKind.Method)]
    [DataRow("ldsfld string String::Empty", OperandKind.Field)]
    [DataRow("ldtoken int32", OperandKind.Token)]
    [DataRow("calli int32(int32)", OperandKind.Signature)]
    [DataRow("unaligned. 1", OperandKind.Byte)]
    public void Parse_Operands_HaveExpectedKind(string text, OperandKind kind)
    {
        Assert.AreEqual(kind, InstructionParser.Parse(text, Empty).Kind);
    }

    /// <summary>
    /// Locals resolve by name and by index, including the short forms.
    /// </summary>
    [TestMethod]
    public void Parse_Locals_ResolveByNameAndIndex()
    {
        var context = Empty with { Locals = [new LocalDeclaration(typeof(int), "i", false), new LocalDeclaration(typeof(string), "s", false)] };
        Assert.AreEqual(1, InstructionParser.Parse("ldloc s", context).LocalIndex);
        Assert.AreEqual(0, InstructionParser.Parse("stloc 0", context).LocalIndex);
        Assert.AreEqual(1, InstructionParser.Parse("ldloc.1", context).LocalIndex);
        Assert.AreEqual(OpCodes.Ldloca_S, InstructionParser.Parse("ldloca.s i", context).Op);
    }

    /// <summary>
    /// Typos get a suggestion.
    /// </summary>
    [TestMethod]
    public void Parse_UnknownOpcode_Suggests()
    {
        var ex = Assert.ThrowsExactly<ReplException>(() => InstructionParser.Parse("lcd.i4 1", Empty));
        Assert.Contains("did you mean 'ldc.i4'", ex.Message);
    }

    /// <summary>
    /// Range checks on short immediates.
    /// </summary>
    [TestMethod]
    public void Parse_LdcI4S_OutOfRange_Throws()
    {
        var ex = Assert.ThrowsExactly<ReplException>(() => InstructionParser.Parse("ldc.i4.s 200", Empty));
        Assert.Contains("use ldc.i4", ex.Message);
    }

    /// <summary>
    /// Arguments are rejected until they are declared.
    /// </summary>
    [TestMethod]
    public void Parse_LdargWithoutArguments_Throws()
    {
        var ex = Assert.ThrowsExactly<ReplException>(() => InstructionParser.Parse("ldarg.0", Empty));
        Assert.Contains(".args", ex.Message);
    }

    /// <summary>
    /// The no. prefix has no emit API and is reported.
    /// </summary>
    [TestMethod]
    public void Parse_NoPrefix_IsReported()
    {
        Assert.ThrowsExactly<ReplException>(() => InstructionParser.Parse("no. typecheck", Empty));
    }
}
