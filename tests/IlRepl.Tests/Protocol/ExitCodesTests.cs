using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// An exit code reads the way its platform writes it, and a fatal one carries its name.
/// </summary>
[TestClass]
public sealed class ExitCodesTests
{
    /// <summary>
    /// A failed Windows status is hexadecimal and named when known, and an ordinary code stays a plain number.
    /// </summary>
    /// <param name="code">The exit code as Process.ExitCode reports it.</param>
    /// <param name="expected">The text a message shows.</param>
    [TestMethod]
    [DataRow(-1073741571, "0xC00000FD (stack overflow)")]
    [DataRow(-1073741819, "0xC0000005 (access violation)")]
    [DataRow(-2146232797, "0x80131623 (fail fast)")]
    [DataRow(-1073740791, "0xC0000409 (fail fast)")]
    [DataRow(-532462766, "0xE0434352 (unhandled .NET exception)")]
    [DataRow(-1073741510, "0xC000013A (Ctrl+C)")]
    [DataRow(-1, "0xFFFFFFFF")]
    [DataRow(0, "0")]
    [DataRow(23, "23")]
    [DataRow(134, "134")]
    public void Describe_WindowsStatusIsHexadecimalAndNamed(int code, string expected)
    {
        Assert.AreEqual(expected, ExitCodes.Describe(code, windows: true));
    }

    /// <summary>
    /// A Unix process that a signal ended reports 128 plus the signal, which is named when Linux and macOS agree on it.
    /// </summary>
    /// <param name="code">The exit code as Process.ExitCode reports it.</param>
    /// <param name="expected">The text a message shows.</param>
    [TestMethod]
    [DataRow(134, "134 (SIGABRT)")]
    [DataRow(137, "137 (SIGKILL)")]
    [DataRow(139, "139 (SIGSEGV)")]
    [DataRow(143, "143 (SIGTERM)")]
    [DataRow(135, "135")]
    [DataRow(0, "0")]
    [DataRow(3, "3")]
    [DataRow(-1, "-1")]
    public void Describe_UnixSignalIsNamed(int code, string expected)
    {
        Assert.AreEqual(expected, ExitCodes.Describe(code, windows: false));
    }

    /// <summary>
    /// The short form describes a process on this machine.
    /// </summary>
    [TestMethod]
    public void Describe_UsesThisPlatform()
    {
        Assert.AreEqual(ExitCodes.Describe(139, OperatingSystem.IsWindows()), ExitCodes.Describe(139));
    }
}
