using System.Text;
using IlRepl.Host;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Verifies runtime startup text cannot contaminate user output or consume its capture budget.
/// </summary>
[TestClass]
public sealed class NativeOutputBufferTests
{
    /// <summary>
    /// Split markers and UTF-8 characters preserve user text even when it repeats runtime diagnostics and the marker itself.
    /// </summary>
    /// <param name="fragment">The number of bytes delivered in each pipe read.</param>
    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(4096)]
    public void Append_SplitBoundaryPreservesAllUserText(int fragment)
    {
        var marker = NativeOutputBuffer.StartMarker("ilrepl-native-unique");
        const string banner = "The runtime has been configured to pause during startup\r\n";
        var user = "é漢字🌍" + banner + marker;
        var bytes = Encoding.UTF8.GetBytes(banner + marker + user);
        var capture = new NativeOutputBuffer(4096, marker);

        for (var offset = 0; offset < bytes.Length; offset += fragment)
            capture.Append(bytes.AsSpan(offset, Math.Min(fragment, bytes.Length - offset)));

        Assert.AreEqual(user, capture.Text);
        Assert.IsFalse(capture.Overflowed);
    }

    /// <summary>
    /// Startup text and framing leave the complete user byte budget available and the next byte reports overflow.
    /// </summary>
    [TestMethod]
    public void Append_UserLimitExcludesStartupAndMarker()
    {
        var marker = NativeOutputBuffer.StartMarker("ilrepl-native-limit");
        var capture = new NativeOutputBuffer(16, marker);
        capture.Append(Encoding.UTF8.GetBytes("runtime banner" + marker + "0123456789abcdef"));
        Assert.AreEqual("0123456789abcdef", capture.Text);
        Assert.IsFalse(capture.Overflowed);

        capture.Append("!"u8);
        capture.Append("ignored"u8);

        Assert.IsTrue(capture.Overflowed);
        Assert.AreEqual("0123456789abcdef", capture.Text);
    }

    /// <summary>
    /// Failure before managed startup retains bounded diagnostic text instead of hiding the launch failure.
    /// </summary>
    [TestMethod]
    public void Append_MissingBoundaryRetainsBoundedStartupFailure()
    {
        var capture = new NativeOutputBuffer(12, NativeOutputBuffer.StartMarker("ilrepl-native-missing"));
        capture.Append("launch error"u8);
        Assert.AreEqual("launch error", capture.Text);
        Assert.IsFalse(capture.Overflowed);

        capture.Append(" excess"u8);
        capture.Append(" ignored"u8);

        Assert.IsTrue(capture.Overflowed);
        Assert.AreEqual("launch error", capture.Text);
    }
}
