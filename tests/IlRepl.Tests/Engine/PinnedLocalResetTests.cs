using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using ILVerify;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Pinned managed references accept the compiler's native-null reset without weakening unrelated store validation.
/// </summary>
[TestClass]
public sealed class PinnedLocalResetTests
{
    /// <summary>
    /// The cancellation token for editor preview analysis.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Pinned array references retain results and signatures through native-zero resets, import, and independent assembly.
    /// </summary>
    /// <param name="constant">The zero constant's instruction encoding.</param>
    /// <param name="conversion">The native conversion's signedness.</param>
    [TestMethod]
    [DataRow("ldc.i4.0", "conv.u")]
    [DataRow("ldc.i4 0", "conv.i")]
    [DataRow("ldc.i4.s 0", "conv.u")]
    [DataRow("ldc.i8 0", "conv.i")]
    public void Reset_PinnedByRefPreservesRuntimeAndImportedBehavior(string constant, string conversion)
    {
        var session = IlLines.Load(".method int32 Read(uint8[] bytes) {",
            ".locals init (uint8& pinned pointer, int32 result)",
            "ldarg.0", "ldc.i4.0", "ldelema uint8", "stloc.0", "ldloc.0", "ldind.u1", "stloc.1",
            constant, conversion, "stloc.0", "ldloc.1", "ret", "}");
        var original = session.Methods.Single().Version.Body;
        Assert.AreEqual(42, original.Invoke(null, [new byte[] { 42 }]));
        Assert.IsTrue(original.GetMethodBody()!.LocalVariables[0].IsPinned);
        Assert.AreEqual(typeof(byte).MakeByRefType(), original.GetMethodBody()!.LocalVariables[0].LocalType);
        var listing = MethodDisassembler.Disassemble(original, session);
        var reset = listing.Entries.Last(entry => entry.Instruction?.Op.Name == "stloc.0");
        var diagnostics = StackAnalysis.Diagnostics(listing);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, diagnostics);
        Assert.Contains(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Unverifiable
            && diagnostic.Location.Offset == reset.Offset, diagnostics);

        foreach (var image in new[]
        {
            AssemblyExporter.Write(session, "pinned-reset"),
            IlasmLocator.Assemble(IlAsmRenderer.Render(session)),
        })
        {
            using var oracle = new IlVerificationOracle();
            Assert.Contains(VerifierError.StackUnexpected, oracle.Verify(image));
            var context = new AssemblyLoadContext("pinned-reset-" + Guid.NewGuid(), isCollectible: true);
            try
            {
                var assembly = context.LoadImage(image);
                var method = assembly.GetTypes().SelectMany(type => type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    .Single(method => method.Name == "Read");
                Assert.AreEqual(42, method.Invoke(null, [new byte[] { 42 }]));
                Assert.IsTrue(method.GetMethodBody()!.LocalVariables[0].IsPinned);
            }
            finally
            {
                context.Unload();
            }
        }

        var edit = session.PrepareEdit("Read", "PinnedCopy");
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, [new byte[] { 42 }]));
        Assert.IsTrue(edit.Method.GetMethodBody()!.LocalVariables[0].IsPinned);
    }

    /// <summary>
    /// Both incoming branches must establish zero before a pinned reset is accepted.
    /// </summary>
    [TestMethod]
    public void Reset_JoinedZeroValuesRemainKnownAcrossDifferentEncodings()
    {
        var session = IlLines.Load(".method int32 Read(bool branch) {",
            ".locals init (uint8& pinned pointer)",
            "ldarg.0", "brtrue OTHER", "ldc.i4.0", "conv.u", "br STORE",
            "OTHER: ldc.i4.s 0", "conv.i", "STORE: dup", "stloc.0", "stloc.0", "ldc.i4 42", "ret", "}");

        Assert.AreEqual(42, session.Methods.Single().Version.Body.Invoke(null, [true]));
        Assert.AreEqual(42, session.Methods.Single().Version.Body.Invoke(null, [false]));
    }

    /// <summary>
    /// Non-pinned references and pinned values without a proven native zero keep their normal type mismatch diagnostics.
    /// </summary>
    /// <param name="local">The target local declaration.</param>
    /// <param name="value">The value placed on the stack.</param>
    /// <param name="convert">Whether the value is converted to native unsigned integer.</param>
    [TestMethod]
    [DataRow("uint8& pointer", "ldc.i4.0", true)]
    [DataRow("uint8& pinned pointer", "ldc.i4.1", true)]
    [DataRow("uint8& pinned pointer", "ldarg.0", true)]
    [DataRow("uint8& pinned pointer", "ldc.i4.0", false)]
    [DataRow("uint8& pinned pointer", "ldnull", false)]
    public void Reset_InvalidStoresRemainRejected(string local, string value, bool convert)
    {
        var session = IlLines.Load(".method void Invalid(int32 value) {", ".locals init (" + local + ")", value);
        if (convert)
        {
            session.AddLine("conv.u");
        }

        var error = Assert.ThrowsExactly<ReplException>(() => session.AddLine("stloc.0"));

        Assert.Contains("stloc.0 needs uint8& but found", error.Message);
        Assert.IsEmpty(session.Methods);
    }

    /// <summary>
    /// Merging a zero path with a nonzero path cannot hide the invalid value behind a nearby constant instruction.
    /// </summary>
    [TestMethod]
    public void Reset_MixedIncomingValuesRemainRejected()
    {
        var session = IlLines.Load(".method void Invalid(bool branch) {", ".locals init (uint8& pinned pointer)",
            "ldarg.0", "brtrue OTHER", "ldc.i4.0", "conv.u", "br STORE", "OTHER: ldc.i4.1", "conv.u", "STORE:");

        var error = Assert.ThrowsExactly<ReplException>(() => session.AddLine("stloc.0"));

        Assert.Contains("stloc.0 needs uint8& but found native uint", error.Message);
        Assert.IsEmpty(session.Methods);
    }

    /// <summary>
    /// The symbolic editor uses the same pinned-slot and zero facts as runtime compilation.
    /// </summary>
    [TestMethod]
    public async Task Reset_EditorPreviewAcceptsNativeNullAndDiagnosesNonzero()
    {
        var source = ".method void Reset() {\n.locals init (uint8& pinned pointer)\nldc.i4.0\nconv.u\nstloc.0\nret\n}";
        var session = new Session();
        using var editor = new EditingSession(session);

        var valid = await editor.AnalyzeAsync(new AnalysisRequest(source.Split('\n'), 6, 0, 1), TestContext.CancellationToken);
        var invalid = await editor.AnalyzeAsync(new AnalysisRequest(source.Replace("ldc.i4.0", "ldc.i4.1",
            StringComparison.Ordinal).Split('\n'), 6, 0, 2), TestContext.CancellationToken);

        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, valid.Diagnostics);
        Assert.Contains(diagnostic => diagnostic.Code == "FLOW005" && diagnostic.Message.Contains("needs uint8&",
            StringComparison.Ordinal), invalid.Diagnostics);
        Assert.IsEmpty(session.Methods);
    }
}
