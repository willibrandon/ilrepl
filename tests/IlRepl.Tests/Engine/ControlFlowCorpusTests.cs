using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;
using ILVerify;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Checks the shared source corpus against live execution, independent tools, and both export renderers.
/// </summary>
[TestClass]
[TestCategory("ExportConformance")]
public sealed class ControlFlowCorpusTests
{
    /// <summary>
    /// Supplies cancellation for full-document analysis.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Supplies each named reproduction independently to the test runner.
    /// </summary>
    public static IEnumerable<object[]> Cases => ControlFlowExamples.All.Select(example => new object[] { example.Name });

    /// <summary>
    /// The same source has consistent outcomes across analysis, definition, independent verification, and exported execution.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(Cases))]
    public async Task Source_AgreesAcrossEngines(string name)
    {
        var example = ControlFlowExamples.All.Single(example => example.Name == name);
        var session = new Session();
        using var editing = new EditingSession(session);
        var lines = example.Source.Split('\n');
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 1, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(example.Accepted, !preview.Diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error),
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        ReplException? refusal = null;
        foreach (var line in lines)
        {
            try
            {
                session.AddLine(line);
            }
            catch (ReplException error)
            {
                refusal = error;
                break;
            }
        }

        Assert.AreEqual(example.Accepted, refusal is null, refusal?.Message);
        var source = ControlFlowSource.Original(example);
        var original = AssembleOriginal(name, source);
        using var oracle = new IlVerificationOracle();
        var verification = Array.Empty<VerifierError>();
        if (example.VerificationFailure.Length > 0)
        {
            var failure = Assert.ThrowsExactly<InvalidOperationException>(() => oracle.Verify(original));
            Assert.AreEqual("ILVerification could not finish the fixture: " + example.VerificationFailure, failure.Message);
        }
        else
        {
            verification = oracle.Verify(original).ToArray();
        }

        TestContext.WriteLine($"{name}: {string.Join(", ", verification)}");
        var expected = example.Verification.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.AreSequenceEqual(expected.Order(),
            verification.Select(code => code.ToString()).Distinct().Order(), string.Join(", ", verification));
        if (example.Accepted)
        {
            Assert.AreEqual(example.Unverifiable,
                preview.Diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Unverifiable),
                string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        }

        if (name == "PointerFieldArithmetic")
        {
            var diagnostics = preview.Diagnostics.Where(diagnostic => diagnostic.Code == "FLOW007").ToArray();
            Assert.HasCount(1, diagnostics);
            Assert.AreEqual(Array.IndexOf(lines, "add"), diagnostics[0].Location.Line);
        }

        if (name == "InheritedFieldsBeforeBaseCall")
        {
            var diagnostics = preview.Diagnostics.Where(diagnostic => diagnostic.Code == "FLOW007").ToArray();
            Assert.HasCount(3, diagnostics);
            Assert.AreSequenceEqual(["ldfld", "ldflda", "stfld"], diagnostics.Select(diagnostic =>
                diagnostic.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]));
        }

        if (name == "InitOnlyFieldAddresses")
        {
            var diagnostics = preview.Diagnostics.Where(diagnostic => diagnostic.Code == "FLOW007").ToArray();
            Assert.HasCount(2, diagnostics);
            Assert.AreSequenceEqual(["ldflda", "ldsflda"], diagnostics.Select(diagnostic =>
                diagnostic.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]));
        }

        if (refusal is not null)
        {
            Assert.Contains(example.Finding, refusal.Message);
            Assert.IsNotNull(session.OpenMethod, "A rejected body stays editable.");
            return;
        }

        session.AddLine("ldc.i4 " + example.Input);
        session.AddLine(example.Call);
        Assert.AreEqual(example.Expected, session.Run().Value);
        var arguments = example.GenericArguments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(argument => argument == "int32" ? typeof(int) : typeof(string)).ToArray();
        using var execution = new ExportExecution();
        try
        {
            execution.Record("independent.il", Encoding.UTF8.GetBytes(source));
            execution.Record("independent.dll", original);
            foreach (var profile in new[] { "deterministic", "tiered" })
            {
                var observed = await execution.RunAsync(original, "Fixture", name, profile, [example.Input], arguments,
                    cancellationToken: TestContext.CancellationToken);
                AssertObservation(example, observed);
                await execution.RunLiveAsync(example, profile, TestContext.CancellationToken);
            }

            foreach (var edited in new[] { false, true })
            {
                var exported = IlLines.Load(example.Source.Split('\n'));
                if (edited)
                {
                    Add(exported, ".method int32 ExportRendererWitness() {", "ldc.i4.0", "ret", "}");
                    var edit = exported.PrepareEdit("ExportRendererWitness", "ExportRendererCopy");
                    exported.CommitEdit(edit.Name, edit.Source);
                }

                Add(exported, "ldc.i4 " + example.Input, example.Call);
                var identity = edited ? "edited" : "text";
                var saved = AssemblyExporter.Write(exported, "FlowExport");
                execution.Record(identity + "-saved.dll", saved);
                var rendered = exported.ToIlAsm();
                execution.Record(identity + ".il", Encoding.UTF8.GetBytes(rendered));
                var assembled = IlasmLocator.Assemble(rendered);
                execution.Record(identity + "-ilasm.dll", assembled);
                var roundTrip = IldasmLocator.RoundTrip(saved);
                execution.Record(identity + "-ildasm.dll", roundTrip);
                Assert.AreSequenceEqual(ExportMetadata.Read(saved), ExportMetadata.Read(roundTrip));
                foreach (var image in new[] { saved, assembled, roundTrip })
                {
                    VerifyExport(example, image);
                    foreach (var profile in new[] { "deterministic", "tiered" })
                    {
                        var observed = await execution.RunAsync(image, "IlRepl.Cell", "Run", profile,
                            cancellationToken: TestContext.CancellationToken);
                        AssertObservation(example, observed);
                    }
                }
            }
        }
        catch
        {
            execution.RetainArtifacts();
            throw;
        }
    }

    private static void AssertObservation(ControlFlowExample example, ExportObservation observed)
    {
        Assert.IsNull(observed.ExceptionType, observed.ExceptionType);
        Assert.AreEqual(typeof(int).AssemblyQualifiedName, observed.Result.Type);
        Assert.AreEqual(example.Expected, observed.Result.Value.GetInt32());
        Assert.AreEqual("", observed.StandardOutput);
        Assert.AreEqual("", observed.StandardError);
    }

    private static void VerifyExport(ControlFlowExample example, byte[] image)
    {
        using var oracle = new IlVerificationOracle();
        if (example.VerificationFailure.Length > 0)
        {
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => oracle.Verify(image));
            Assert.AreEqual("ILVerification could not finish the fixture: " + example.VerificationFailure, error.Message);
            return;
        }

        var actual = oracle.Verify(image).Select(code => code.ToString()).Distinct().Order();
        var expected = (example.ExportVerification ?? example.Verification)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order();
        Assert.AreSequenceEqual(expected, actual, "The exported image must have precisely the expected ILVerify diagnostics.");
    }

    /// <summary>
    /// A function-pointer signature returns from a cell as its native-integer stack value.
    /// </summary>
    [TestMethod]
    public async Task CellFunctionPointer_ReturnsNativeInteger()
    {
        var session = new Session();
        foreach (var line in new[] { ".method int32 Id(int32 value) {", "ldarg value", "ret", "}" })
        {
            session.AddLine(line);
        }

        var lines = new[]
        {
            ".locals init (method int32 *(int32) pointer)", "ldftn int32 Id(int32)", "stloc pointer", "ldloc pointer", "ret",
        };

        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 4, 3, 1), TestContext.CancellationToken);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind is AnalysisDiagnosticKind.Error
            or AnalysisDiagnosticKind.Unverifiable, preview.Diagnostics,
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        Assert.IsInstanceOfType<nint>(session.Run().Value);
    }

    /// <summary>
    /// A session method keeps its function-pointer signature through execution and export.
    /// </summary>
    [TestMethod]
    public void SessionMethodFunctionPointer_RetainsExactSignature()
    {
        var session = new Session();
        Add(session, ".method int32 Id(int32 value) {", "ldarg value", "ret", "}",
            ".method method int32 *(int32) Pointer() {", "ldftn int32 Id(int32)", "ret", "}");

        AddCall(session);
        Assert.AreEqual(42, session.Run().Value);

        AddCall(session);
        var il = session.ToIlAsm();
        Assert.Contains(".method public static method int32 *(int32) Pointer()", il);
        Execute(IlasmLocator.Assemble(il), "IlRepl.Cell", "Run", null);

        var path = Path.Join(Path.GetTempPath(), "ilrepl-flow-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            session.Save(path);
            Execute(File.ReadAllBytes(path), "IlRepl.Cell", "Run", null);
        }
        finally
        {
            File.Delete(path);
        }

        static void AddCall(Session target) => Add(target,
            "call method int32 *(int32) Pointer()", "pop", "ldc.i4.s 42", "ret");
    }

    /// <summary>
    /// Closing a structured finally emits the endfinally that clears its remaining stack.
    /// </summary>
    [TestMethod]
    public void StructuredFinally_ImplicitEndfinallyClearsStack()
    {
        var session = new Session();
        foreach (var line in new[]
        {
            ".method int32 ImplicitEndfinally(int32 n) {", ".try {", "leave DONE", "} finally {", "ldc.i4.1", "}",
            "DONE: ldc.i4.s 42", "ret", "}",
        })
        {
            session.AddLine(line);
        }

        var il = session.ToIlAsm();
        Assert.Contains("endfinally", il);
        session.AddLine("ldc.i4.1");
        session.AddLine("call int32 ImplicitEndfinally(int32)");
        session.AddLine("ret");
        Assert.AreEqual(42, session.Run().Value);
        session.AddLine("ldc.i4.1");
        session.AddLine("call int32 ImplicitEndfinally(int32)");
        session.AddLine("ret");
        Execute(IlasmLocator.Assemble(session.ToIlAsm()), "IlRepl.Cell", "Run", null);
        var path = Path.Join(Path.GetTempPath(), "ilrepl-flow-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            session.Save(path);
            Execute(File.ReadAllBytes(path), "IlRepl.Cell", "Run", null);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Execute(byte[] image, string type, string method, object[]? arguments, Type[]? typeArguments = null)
    {
        var context = new AssemblyLoadContext("flow corpus", isCollectible: true);
        try
        {
            using var stream = new MemoryStream(image, writable: false);
            var assembly = context.LoadFromStream(stream);
            var entry = assembly.GetType(type)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!;
            if (entry.IsGenericMethodDefinition)
            {
                entry = entry.MakeGenericMethod(typeArguments!);
            }

            Assert.AreEqual(42, entry.Invoke(null, arguments));
        }
        finally
        {
            context.Unload();
        }
    }

    private static void Add(Session session, params string[] lines)
    {
        foreach (var line in lines)
        {
            session.AddLine(line);
        }
    }

    private static byte[] AssembleOriginal(string name, string source)
    {
        if (name is not ("WrongStaticVirtualCall" or "WrongStaticConstructorAllocation"))
        {
            return IlasmLocator.Assemble(source);
        }

        var constructor = name == "WrongStaticConstructorAllocation";
        var validSource = constructor
            ? source.Replace("newobj void Fixture::.cctor()", "call void Fixture::.cctor()", StringComparison.Ordinal)
            : source.Replace("callvirt int32 WrongStaticVirtualCall(int32)",
                "call int32 Fixture::WrongStaticVirtualCall(int32)", StringComparison.Ordinal);
        using var input = new MemoryStream(IlasmLocator.Assemble(validSource), writable: false);
        using var module = ModuleDefinition.ReadModule(input);
        var method = module.Types.Single(type => type.Name == "Fixture").Methods.Single(candidate => candidate.Name == name);
        method.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Call).OpCode = constructor
            ? OpCodes.Newobj : OpCodes.Callvirt;
        using var output = new MemoryStream();
        module.Write(output);
        return output.ToArray();
    }
}
