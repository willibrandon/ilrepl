using System.Globalization;
using System.Numerics;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Numeric argument literals retain their exact managed width and range through real cell and comparison execution.
/// </summary>
[TestClass]
public sealed class NumericArgumentTests
{
    /// <summary>
    /// Supplies cancellation for real comparison worker processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Unsigned 64-bit decimal, hexadecimal, and binary literals preserve values above the signed range in both workers.
    /// </summary>
    /// <param name="literal">The original CIL argument spelling.</param>
    /// <param name="expected">The independently specified decimal value.</param>
    [TestMethod]
    [DataRow("0", "0")]
    [DataRow("18446744073709551615", "18446744073709551615")]
    [DataRow("0xffff_ffff_ffff_ffff", "18446744073709551615")]
    [DataRow("0b1111111111111111_1111111111111111_1111111111111111_1111111111111111", "18446744073709551615")]
    public async Task Unsigned64_PreservesExactArgumentsAndObservedResults(string literal, string expected)
    {
        var session = Echo("uint64");
        var number = ulong.Parse(expected, CultureInfo.InvariantCulture);
        session.AddLine(".args (uint64 value = " + literal + ")");
        Assert.AreEqual(number, Assert.IsInstanceOfType<ulong>(session.State.Arguments.Single().Value));
        session.AddLine("ldarg.0");
        session.AddLine("call Copy");
        Assert.AreEqual(number, session.Run().Value);

        var result = await Compare(session, literal);

        Assert.AreEqual("match", result.Outcome, Details(result));
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, Details(result));
            AssertScalar(side.Result!, "System.UInt64", expected);
            var call = side.Invocations.Single();
            AssertScalar(call.Inputs.Single(member => member.Name == "argument 0").Value, "System.UInt64", expected);
            AssertScalar(call.Outputs.Single(member => member.Name == "return").Value, "System.UInt64", expected);
        }
    }

    /// <summary>
    /// Native integer endpoints bind boxed native values and reach real workers without truncating their numeric conversion.
    /// </summary>
    /// <param name="unsigned">Whether the native argument is unsigned.</param>
    /// <param name="maximum">Whether the upper endpoint is used instead of the lower endpoint.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task NativeEndpoints_PreserveRuntimeWidthAndExplicitNumericResults(bool unsigned, bool maximum)
    {
        var bits = IntPtr.Size * 8;
        var number = unsigned ? maximum ? (BigInteger.One << bits) - 1 : BigInteger.Zero
            : maximum ? (BigInteger.One << (bits - 1)) - 1 : -(BigInteger.One << (bits - 1));
        var type = unsigned ? "native uint" : "native int";
        var resultType = unsigned ? "uint64" : "int64";
        var conversion = unsigned ? "conv.u8" : "conv.i8";
        var session = Echo(type, resultType, conversion);
        var expected = number.ToString(CultureInfo.InvariantCulture);
        var magnitude = BigInteger.Abs(number);
        var sign = number.Sign < 0 ? "-" : "";
        var binary = magnitude.IsZero ? "0" : Convert.ToString(unchecked((long)(ulong)magnitude), 2);
        foreach (var literal in new[]
        {
            expected,
            sign + "0x" + magnitude.ToString("x", CultureInfo.InvariantCulture),
            sign + "0b" + binary,
        })
        {
            var cell = Echo(type, resultType, conversion);
            cell.AddLine(".args (" + type + " value = " + literal + ")");
            var value = cell.State.Arguments.Single().Value!;
            Assert.AreEqual(unsigned ? typeof(nuint) : typeof(nint), value.GetType());
            Assert.AreEqual(expected, ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
            cell.AddLine("ldarg.0");
            cell.AddLine("call Copy");
            Assert.AreEqual(expected, ((IFormattable)cell.Run().Value!).ToString(null, CultureInfo.InvariantCulture));
        }

        var result = await Compare(session, expected);

        Assert.AreEqual("incomplete", result.Outcome, Details(result));
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, Details(result));
            AssertScalar(side.Result!, unsigned ? "System.UInt64" : "System.Int64", expected);
            var input = side.Invocations.Single().Inputs.Single(member => member.Name == "argument 0").Value;
            Assert.AreEqual("unavailable", input.Kind);
            Assert.EndsWith(unsigned ? "System.UIntPtr" : "System.IntPtr", input.Type);
            Assert.Contains("handles", input.Value!);
        }
    }

    /// <summary>
    /// Out-of-range and malformed literals are rejected before changing existing arguments or a committed edit.
    /// </summary>
    /// <param name="type">The declared numeric type.</param>
    /// <param name="literal">The invalid argument spelling.</param>
    [TestMethod]
    [DataRow("uint64", "-1")]
    [DataRow("uint64", "18446744073709551616")]
    [DataRow("uint64", "0x1_0000_0000_0000_0000")]
    [DataRow("uint64", "0b10000000000000000_0000000000000000_0000000000000000_0000000000000000")]
    [DataRow("int64", "9223372036854775808")]
    [DataRow("int64", "-9223372036854775809")]
    [DataRow("int64", "0x8000_0000_0000_0000")]
    [DataRow("int64", "-0x8000_0000_0000_0001")]
    [DataRow("int32", "2147483648")]
    [DataRow("uint32", "4294967296")]
    [DataRow("int8", "128")]
    [DataRow("uint8", "-1")]
    [DataRow("uint64", "0x")]
    [DataRow("uint64", "0b")]
    [DataRow("uint64", "0x_")]
    [DataRow("int64", "--1")]
    [DataRow("int64", "+")]
    public void InvalidLiteral_PreservesPreviousArgumentsAndRejectsComparison(string type, string literal)
    {
        AssertRejected(type, literal);
    }

    /// <summary>
    /// Values immediately beyond native endpoints are rejected according to the current process architecture.
    /// </summary>
    /// <param name="unsigned">Whether the native range is unsigned.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeOverflow_RejectsBothSidesOfTheRuntimeRange(bool unsigned)
    {
        var bits = IntPtr.Size * 8;
        var maximum = (BigInteger.One << (unsigned ? bits : bits - 1)) - 1;
        var minimum = unsigned ? BigInteger.Zero : -(BigInteger.One << (bits - 1));
        var type = unsigned ? "native uint" : "native int";
        AssertRejected(type, (minimum - 1).ToString(CultureInfo.InvariantCulture));
        AssertRejected(type, (maximum + 1).ToString(CultureInfo.InvariantCulture));
    }

    private static Session Echo(string type, string? resultType = null, string? conversion = null)
    {
        var session = IlLines.Load(".method " + (resultType ?? type) + " Echo(" + type + " value) {", "ldarg.0",
            conversion ?? "nop", "ret", "}");
        var edit = session.PrepareEdit("Echo", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        return session;
    }

    private static void AssertRejected(string type, string literal)
    {
        var session = Echo(type);
        session.AddLine(".args (int32 previous = 42)");
        var prior = session.State.Arguments.Single();
        var method = session.Edits.Single().Method;
        var argumentError = Assert.ThrowsExactly<ReplException>(() => session.AddLine(".args (" + type + " value = " + literal + ")"));
        Assert.Contains("does not fit", argumentError.Message);
        Assert.AreSame(prior, session.State.Arguments.Single());
        var comparisonError = Assert.ThrowsExactly<ReplException>(() => ComparisonCapture.Create(session, "Copy (" + literal + ")"));
        Assert.Contains("does not fit", comparisonError.Message);
        Assert.AreSame(method, session.Edits.Single().Method);
        Assert.AreEqual(1, session.Edits.Single().Revision);
        session.AddLine("ldarg.0");
        Assert.AreEqual(42, session.Run().Value);
    }

    private Task<ComparisonReply> Compare(Session session, string literal) =>
        ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy (" + literal + ")"), TestContext.CancellationToken);

    private static void AssertScalar(ObservedValue value, string type, string expected)
    {
        Assert.AreEqual("scalar", value.Kind);
        Assert.EndsWith(type, value.Type);
        Assert.AreEqual(expected, value.Value);
    }

    private static string Details(ComparisonReply reply) => reply.Outcome + ": " + reply.Original.Detail + "; " + reply.Edited.Detail;
}
