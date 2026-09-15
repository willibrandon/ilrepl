using IlRepl.Engine;
using IlRepl.Host;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Scenario capture rejects incompatible calls while preserving compatible copied and generic signatures.
/// </summary>
[TestClass]
public sealed class ScenarioSignatureComparisonTests
{
    /// <summary>
    /// Supplies cancellation for actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Signature changes are rejected during capture before a scenario can be exported or executed.
    /// </summary>
    /// <param name="signature">The edited return type and parameter signature.</param>
    /// <param name="body">The edited method body.</param>
    /// <param name="scenarioReturn">The scenario's return type.</param>
    /// <param name="scenarioBody">A valid caller of the edited signature.</param>
    [TestMethod]
    [DataRow("int32 Read(int32 value, int32 other)", "ldarg.1\nret", "int32", "ldc.i4.1\nldc.i4.s 42\ncall Copy\nret")]
    [DataRow("int64 Read(int32 value)", "ldarg.0\nconv.i8\nret", "int64", "ldc.i4.s 42\ncall Copy\nret")]
    [DataRow("void Read(int32 value)", "ret", "int32", "ldc.i4.1\ncall Copy\nldc.i4.s 42\nret")]
    [DataRow("int32 Read(int64 value)", "ldarg.0\nconv.i4\nret", "int32", "ldc.i8 42\ncall Copy\nret")]
    [DataRow("int32 Read(int32& value)", "ldarg.0\nldind.i4\nret", "int32",
        ".locals init (int32 value)\nldc.i4.s 42\nstloc.0\nldloca.s 0\ncall Copy\nret")]
    [DataRow("int32 modopt(System.Runtime.CompilerServices.IsConst) Read(int32 value)", "ldarg.0\nret", "int32",
        "ldc.i4.s 42\ncall Copy\nret")]
    [DataRow("int32 Read(int32 modreq(System.Runtime.CompilerServices.IsConst) value)", "ldarg.0\nret", "int32",
        "ldc.i4.s 42\ncall Copy\nret")]
    public void Create_ChangedScenarioSignature_IsRejected(string signature, string body, string scenarioReturn, string scenarioBody)
    {
        var session = IlLines.Load(".method int32 Read(int32 value) { ldarg.0; ret }");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, ".method public static " + signature + " cil managed {\n" + body + "\n}");
        Add(session, ".method " + scenarioReturn + " Scenario() {\n" + scenarioBody + "\n}");
        var revision = session.CompletionRevision;
        var generation = session.Generation;

        var error = Assert.ThrowsExactly<ReplException>(() => ComparisonCapture.Create(session, "Copy using Scenario"));

        Assert.AreEqual("the original and edited signatures must match to compare this method through a scenario", error.Message);
        Assert.AreEqual(revision, session.CompletionRevision);
        Assert.AreEqual(generation, session.Generation);
        Assert.AreEqual(1, edit.Revision);
        Assert.AreEqual(41, edit.OriginalMethod.Invoke(null, [41]));
        session.AddLine("call Scenario");
        Assert.AreEqual(scenarioReturn == "int64" ? (object)42L : 42, session.Run().Value);
    }

    /// <summary>
    /// Closed generic method and owner arguments remain compatible when parameter names or instructions change.
    /// </summary>
    /// <param name="genericOwner">Whether the generic parameter belongs to the declaring type.</param>
    /// <returns>The completed comparison assertions.</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Create_CompatibleClosedGenericSignature_RemainsCallable(bool genericOwner)
    {
        var session = genericOwner
            ? IlLines.Load(".class public Choice`1<T> {", ".method public static !0 Pick(!0 first, !0 second) { ldarg.0; ret }", "}")
            : IlLines.Load(".class public Choice {", ".method public static !!0 Pick<T>(!!0 first, !!0 second) { ldarg.0; ret }", "}");
        var edit = session.PrepareEdit(genericOwner ? "!0 Choice`1<int32>::Pick(!0, !0)" : "!!0 Choice::Pick<int32>(!!0, !!0)", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldarg.0", "ldarg.1", StringComparison.Ordinal)
            .Replace("first", "left", StringComparison.Ordinal).Replace("second", "right", StringComparison.Ordinal));
        Add(session, ".method int32 Scenario() {\nldc.i4.s 41\nldc.i4.s 42\ncall Copy\nret\n}");

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("41", result.Original.Result!.Value);
        Assert.AreEqual("42", result.Edited.Result!.Value);
        Assert.HasCount(1, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);
    }

    /// <summary>
    /// Copied type names inside array parameters and return values normalize to their captured source identities.
    /// </summary>
    /// <returns>The completed copied-type assertions.</returns>
    [TestMethod]
    public async Task Create_CompatibleCopiedTypes_RemainCallable()
    {
        var session = IlLines.Load(".class public Counter {", ".field public int32 Value",
            ".method public instance void .ctor() { ldarg.0; call instance void Object::.ctor(); ret }",
            ".method public static class Counter Read(class Counter[] values) {", "ldarg.0", "ldlen", "brtrue.s EXISTING",
            "newobj instance void Counter::.ctor()", "ret", "EXISTING: ldarg.0", "ldc.i4.0", "ldelem.ref", "ret", "}", "}");
        var edit = session.PrepareEdit("class Counter Counter::Read(class Counter[])", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        Add(session, """
            .method int32 Scenario() {
              ldc.i4.1
              newarr IlRepl.Edits.Copy.Owner
              dup
              ldc.i4.0
              newobj instance void IlRepl.Edits.Copy.Owner::.ctor()
              dup
              ldc.i4.s 42
              stfld int32 IlRepl.Edits.Copy.Owner::Value
              stelem.ref
              call Copy
              ldfld int32 IlRepl.Edits.Copy.Owner::Value
              ret
            }
            """);

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.AreEqual("42", side.Result!.Value);
            Assert.HasCount(1, side.Invocations);
            Assert.AreEqual("reference", side.Invocations[0].Outputs.Single(member => member.Name == "return").Value.Kind);
        }
    }

    /// <summary>
    /// Direct calls can still compare different return types because they do not rewrite a scenario's calls.
    /// </summary>
    /// <returns>The completed direct comparison assertions.</returns>
    [TestMethod]
    public async Task Create_DirectCallWithChangedReturnType_RemainsSupported()
    {
        var session = IlLines.Load(".method int32 Read() { ldc.i4.s 41; ret }");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, ".method public static string Read() cil managed {\nldstr \"after\"\nret\n}");

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);

        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("41", result.Original.Result!.Value);
        Assert.AreEqual("after", result.Edited.Result!.Value);
    }

    /// <summary>
    /// An external original uses the same early signature validation as a copied baseline.
    /// </summary>
    [TestMethod]
    public void Create_ChangedExternalSignature_IsRejectedBeforeExport()
    {
        var session = new Session();
        var edit = session.PrepareEdit("int32 Math::Abs(int32)", "Copy");
        Assert.IsNotEmpty(edit.Problems);
        session.CommitEdit(edit.Name, ".method public static int64 Abs(int32 value) cil managed {\nldarg.0\nconv.i8\nret\n}");
        Add(session, ".method int64 Scenario() {\nldc.i4.s -42\ncall Copy\nret\n}");

        var error = Assert.ThrowsExactly<ReplException>(() => ComparisonCapture.Create(session, "Copy using Scenario"));

        Assert.AreEqual("the original and edited signatures must match to compare this method through a scenario", error.Message);
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, [-42]));
        session.AddLine("call Scenario");
        Assert.AreEqual(-42L, session.Run().Value);
    }

    /// <summary>
    /// Matching required and optional signature modifiers remain valid when the implementation changes.
    /// </summary>
    /// <returns>The completed modified-signature assertions.</returns>
    [TestMethod]
    public async Task Create_UnchangedSignatureModifiers_RemainCallable()
    {
        const string modifier = "System.Runtime.CompilerServices.IsConst";
        var session = IlLines.Load(".method int32 modopt(" + modifier + ") Read(int32 modreq(" + modifier + ") value) {",
            "ldarg.0", "ret", "}");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
        Add(session, ".method int32 Scenario() {\nldc.i4.s 41\ncall Copy\nret\n}");

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.IsNull(result.Original.Exception);
        Assert.IsNull(result.Edited.Exception);
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("41", result.Original.Result!.Value);
        Assert.AreEqual("42", result.Edited.Result!.Value);
        Assert.HasCount(1, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);
    }

    /// <summary>
    /// Modified reference parameters and returns preserve their shared storage and observed writes.
    /// </summary>
    /// <returns>The completed reference observation assertions.</returns>
    [TestMethod]
    public async Task Create_ModifiedReferences_PreserveAliases()
    {
        const string modifier = "System.Runtime.CompilerServices.IsConst";
        var session = IlLines.Load(".method int32& modopt(" + modifier + ") Read(int32& modreq(" + modifier + ") value) {",
            "ldarg.0", "ret", "}");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ret", "dup\ndup\nldind.i4\nldc.i4.1\nadd\nstind.i4\nret",
            StringComparison.Ordinal));
        Add(session, ".method int32 Scenario() {\n.locals init (int32 value)\nldc.i4.s 41\nstloc.0\nldloca.s 0\n"
            + "call Copy\nldind.i4\nret\n}");

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("41", result.Original.Result!.Value);
        Assert.AreEqual("42", result.Edited.Result!.Value);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.HasCount(1, side.Invocations);
            Assert.IsNotNull(side.Result);
            var outputs = side.Invocations[0].Outputs;
            Assert.AreEqual("-1,1,1", outputs.Single(member => member.Name == "reference aliases").Value.Value);
            Assert.AreEqual(side.Result.Value, outputs.Single(member => member.Name == "argument 0").Value.Value);
            Assert.AreEqual(side.Result.Value, outputs.Single(member => member.Name == "return").Value.Value);
        }
    }

    /// <summary>
    /// Modified void returns do not create an invalid return local or value observation.
    /// </summary>
    /// <returns>The completed void and console observation assertions.</returns>
    [TestMethod]
    public async Task Create_ModifiedVoidReturn_RemainsCallable()
    {
        var session = IlLines.Load(".method void modopt(System.Runtime.CompilerServices.IsConst) Read(int32 value) {",
            "ldarg.0", "call void Console::Write(int32)", "ret", "}");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldarg.0", "ldarg.0\nldc.i4.1\nadd", StringComparison.Ordinal));
        Add(session, ".method int32 Scenario() {\nldc.i4.s 41\ncall Copy\nldc.i4.s 42\nret\n}");

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("41", result.Original.StandardOutput);
        Assert.AreEqual("42", result.Edited.StandardOutput);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("42", side.Result!.Value);
            Assert.HasCount(1, side.Invocations);
            Assert.AreEqual("null", side.Invocations[0].Outputs.Single(member => member.Name == "return").Value.Kind);
        }
    }

    private static void Add(Session session, string source)
    {
        foreach (var line in source.Split('\n'))
        {
            session.AddLine(line);
        }
    }
}
