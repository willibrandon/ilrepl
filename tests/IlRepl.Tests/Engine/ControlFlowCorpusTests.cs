using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;
using ILVerify;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Runs the browser's source corpus through live binding, preview, independent ILAsm, and verification.
/// </summary>
[TestClass]
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
        var source = $$"""
            .assembly extern System.Runtime { }
            .assembly extern System.Private.CoreLib { }
            .assembly FlowCorpus { }
            .module FlowCorpus.dll
            .class public Fixture extends [System.Runtime]System.Object {
                .method public static int32 {{name}}{{example.GenericHeader}}(int32 n) cil managed {
                    .maxstack 64
                    {{string.Join('\n', example.Body).Replace("} handler {", "} {", StringComparison.Ordinal)}}
                }
            }
            """;
        var original = IlasmLocator.Assemble(source);
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
        if (example.Unverifiable)
        {
            Assert.Contains(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Unverifiable, preview.Diagnostics);
        }
        if (refusal is not null)
        {
            Assert.Contains(example.Finding, refusal.Message);
            Assert.IsNotNull(session.OpenMethod, "A rejected body stays editable.");
            return;
        }

        session.AddLine("ldc.i4.1");
        session.AddLine(example.Call);
        Assert.AreEqual(42, session.Run().Value);
        var arguments = example.GenericArguments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(argument => argument == "int32" ? typeof(int) : typeof(string)).ToArray();
        Execute(original, "Fixture", name, [1], arguments);
        session.AddLine("ldc.i4.1");
        session.AddLine(example.Call);
        Execute(IlasmLocator.Assemble(session.ToIlAsm()), "IlRepl.Cell", "Run", null);
        var path = Path.Combine(Path.GetTempPath(), "ilrepl-flow-" + Guid.NewGuid().ToString("N") + ".dll");
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
}
