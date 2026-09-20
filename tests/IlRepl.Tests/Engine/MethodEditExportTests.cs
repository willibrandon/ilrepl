using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Saved images and independently assembled source execute complete imported method families.
/// </summary>
[TestClass]
[TestCategory("ExportConformance")]
public sealed class MethodEditExportTests
{
    /// <summary>
    /// Supplies cancellation to independent export processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// IL inspection preserves a pending cell while saved output still requires complete control flow.
    /// </summary>
    [TestMethod]
    public void Render_UnfinishedCellRetainsCopiedDeclarationsAndPendingLabels()
    {
        var session = IlLines.Load(".method int32 Value() { ldc.i4.s 42; ret }");
        var edit = session.PrepareEdit("Value", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        session.AddLine("call Copy");
        session.AddLine("br DONE");
        var entries = session.Cell.Entries.Count;

        var text = session.ToIlAsm();

        Assert.Contains("IlRepl.Edits.Copy.Owner", text);
        Assert.Contains("object Run()", text);
        Assert.Contains("br DONE", text);
        Assert.HasCount(entries, session.Cell.Entries);
        Assert.AreEqual(1, edit.Revision);
        var failure = Assert.ThrowsExactly<ReplException>(() => AssemblyExporter.Write(session, "unfinished"));
        Assert.Contains("DONE", failure.Message);

        session.AddLine("DONE: nop");
        var context = new AssemblyLoadContext("completed-edit-inspection", isCollectible: true);
        try
        {
            var image = IlasmLocator.Assemble(session.ToIlAsm());
            var assembly = context.LoadImage(image);
            Assert.AreEqual(42, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// The exported cell calls the edited framework method without referencing a live session assembly.
    /// </summary>
    /// <param name="source">Whether Microsoft ILAsm independently assembles the source.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Export_FrameworkEditRunsFromIndependentAssembly(bool source)
    {
        var session = new Session();
        var edit = session.PrepareEdit("int32 Math::Max(int32, int32)", "Maximum");
        session.CommitEdit(edit.Name, edit.Source);
        session.AddLine("ldc.i4.s 17");
        session.AddLine("ldc.i4.s 42");
        session.AddLine("call Maximum");
        var image = source ? IlasmLocator.Assemble(session.ToIlAsm()) : AssemblyExporter.Write(session, "maximum");
        await VerifyIsolatedAsync(image);
        var context = new AssemblyLoadContext("edited-export", isCollectible: true);
        try
        {
            var assembly = context.LoadImage(image);
            Assert.AreEqual(42, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            var copied = assembly.GetType(edit.Method!.DeclaringType!.FullName!)!.GetMethod(edit.Method.Name)!;
            Assert.AreEqual(17, copied.Invoke(null, [17, 2]));
            Assert.IsEmpty(assembly.GetReferencedAssemblies().Where(reference =>
                reference.Name!.StartsWith("ilrepl_", StringComparison.Ordinal)));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Copied private helpers, static initialization, and exception handlers execute independently after export.
    /// </summary>
    /// <param name="source">Whether Microsoft ILAsm independently assembles the source.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Export_PrivateClosureRetainsInitializationAndExceptionRegions(bool source)
    {
        var session = IlLines.Load(
            ".class public Calculator {",
            ".field private static int32 Increment",
            ".method private static void .cctor() { ldc.i4.1; stsfld int32 Calculator::Increment; ret }",
            ".method private static int32 Add(int32 x) { ldarg.0; ldsfld int32 Calculator::Increment; add; ret }",
            ".method public static int32 Calculate(int32 x) {",
            ".locals init (int32 result)",
            ".try {", "ldarg.0", "call int32 Calculator::Add(int32)", "stloc.0", "leave DONE",
            "} catch Exception {", "pop", "ldc.i4.m1", "stloc.0", "leave DONE", "}",
            "DONE: ldloc.0", "ret", "}", "}");
        var edit = session.PrepareEdit("int32 Calculator::Calculate(int32)", "Calculation");
        session.CommitEdit(edit.Name, edit.Source);
        session.AddLine("ldc.i4.s 41");
        session.AddLine("call Calculation");
        var image = source ? IlasmLocator.Assemble(session.ToIlAsm()) : AssemblyExporter.Write(session, "closure");
        await VerifyIsolatedAsync(image);
        var context = new AssemblyLoadContext("closure-export", isCollectible: true);
        try
        {
            var assembly = context.LoadImage(image);
            Assert.AreEqual(42, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            var owner = assembly.GetType(edit.Method!.DeclaringType!.FullName!)!;
            var helper = owner.GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.IsTrue(helper.IsPrivate);
            Assert.HasCount(1, owner.GetMethod("Calculate")!.GetMethodBody()!.ExceptionHandlingClauses);
            Assert.AreEqual(1, owner.GetField("Increment", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
        }
        finally
        {
            context.Unload();
        }
    }

    private async Task VerifyIsolatedAsync(byte[] image)
    {
        var roundTrip = IldasmLocator.RoundTrip(image);
        Assert.AreSequenceEqual(ExportMetadata.Read(image), ExportMetadata.Read(roundTrip));
        using var execution = new ExportExecution();
        foreach (var artifact in new[] { image, roundTrip })
        {
            using var verifier = new IlVerificationOracle();
            Assert.IsEmpty(verifier.Verify(artifact));
            foreach (var profile in new[] { "deterministic", "tiered" })
            {
                var observed = await execution.RunAsync(artifact, "IlRepl.Cell", "Run", profile,
                    cancellationToken: TestContext.CancellationToken);
                Assert.IsNull(observed.ExceptionType);
                Assert.AreEqual(typeof(int).AssemblyQualifiedName, observed.Result.Type);
                Assert.AreEqual(42, observed.Result.Value.GetInt32());
                Assert.AreEqual("", observed.StandardOutput);
                Assert.AreEqual("", observed.StandardError);
            }
        }
    }
}
