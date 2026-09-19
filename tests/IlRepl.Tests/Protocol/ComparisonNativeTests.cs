using System.Text.Json;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Engine;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Native comparison transport remains additive and rejects corrupted or escaping assets before isolated execution.
/// </summary>
[TestClass]
public sealed class ComparisonNativeTests
{
    /// <summary>
    /// Supplies cancellation to real comparison worker processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Older comparison images omit the additive native collection and deserialize to an empty dependency list.
    /// </summary>
    [TestMethod]
    public void Read_OmittedNativeImagesKeepEmptyDefault()
    {
        var image = JsonSerializer.Deserialize("""
            {"image":"","entryType":"Fixture","entryMethod":"Read","entryToken":0,
            "typeArguments":[],"methodArguments":[],"arguments":[],"typeNames":{}}
            """, ProtocolJsonContext.Default.ComparisonImage);

        Assert.IsNotNull(image);
        Assert.IsNotNull(image.NativeLibraries);
        Assert.IsEmpty(image.NativeLibraries);
    }

    /// <summary>
    /// Each side preserves its exact native name, hash, and binary bytes through the production RPC serializer.
    /// </summary>
    [TestMethod]
    public void RoundTrip_PreservesNativeIdentityAndBytes()
    {
        byte[] bytes = [0, 1, 127, 128, 255];
        var source = new ComparisonImage([], "Fixture", "Read", 0, [], [], [], new Dictionary<string, string>())
        {
            NativeLibraries = [new ComparisonNativeLibrary("fixture-native.so", SessionCodec.Hash(bytes), bytes)],
        };

        var wire = JsonSerializer.SerializeToUtf8Bytes(source, ProtocolJsonContext.Default.ComparisonImage);
        var restored = JsonSerializer.Deserialize(wire, ProtocolJsonContext.Default.ComparisonImage)!;
        var native = Assert.ContainsSingle(restored.NativeLibraries);

        Assert.AreEqual("fixture-native.so", native.Name);
        Assert.AreEqual(SessionCodec.Hash(bytes), native.Hash);
        Assert.AreSequenceEqual(bytes, native.Image);
    }

    /// <summary>
    /// Invalid native images fail setup on their own side while the independent valid side still executes its real method.
    /// </summary>
    /// <param name="malformation">The invalid hash, filename, or duplicate identity to send through the real host.</param>
    [TestMethod]
    [DataRow("hash")]
    [DataRow("path")]
    [DataRow("duplicate")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Compare_InvalidNativeAssetsFailBeforeExecution(string malformation)
    {
        var session = IlLines.Load(".method int32 Read() {", "ldc.i4.s 42", "ret", "}");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var package = ComparisonCapture.Create(session, "Copy ()");
        byte[] bytes = [1, 2, 3];
        var native = new ComparisonNativeLibrary(malformation == "path" ? "../escape.so" : "fixture-native.so",
            malformation == "hash" ? new string('0', 64) : SessionCodec.Hash(bytes), bytes);
        package = package with
        {
            Original = package.Original with { NativeLibraries = malformation == "duplicate" ? [native, native] : [native] },
        };

        var report = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual("incomplete", report.Outcome);
        Assert.AreEqual("setup-failed", report.Original.Outcome);
        Assert.Contains(malformation == "duplicate" ? "duplicate native library" : "invalid native dependency", report.Original.Detail!);
        Assert.IsEmpty(report.Original.Invocations);
        Assert.AreEqual("completed", report.Edited.Outcome, report.Edited.Detail);
        Assert.IsNull(report.Edited.Exception);
        Assert.AreEqual("42", report.Edited.Result!.Value);
    }
}
