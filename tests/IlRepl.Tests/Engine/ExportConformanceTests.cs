using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;
using ILVerify;
using Mono.Cecil.Cil;
using GenericParameter = Mono.Cecil.GenericParameter;
using ModuleDefinition = Mono.Cecil.ModuleDefinition;
using TypeAttributes = Mono.Cecil.TypeAttributes;
using TypeDefinition = Mono.Cecil.TypeDefinition;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Exercises atomic publishing and verifies that independent export controls detect incorrect output.
/// </summary>
[TestClass]
[TestCategory("ExportConformance")]
public sealed class ExportConformanceTests
{
    /// <summary>
    /// Supplies cancellation for real child processes and independent tools.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Cancellation and invalid source preserve an existing destination and leave no temporary sibling behind.
    /// </summary>
    [TestMethod]
    public void Save_CancelledOrInvalidExportPreservesDestination()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-atomic-export-").FullName;
        try
        {
            var path = Path.Join(directory, "kept.dll");
            byte[] original = [1, 3, 5, 7];
            File.WriteAllBytes(path, original);
            var complete = IlLines.Load("ldc.i4.s 42");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.ThrowsExactly<OperationCanceledException>(() => AssemblyExporter.SaveCancellable(complete, path, cancelled.Token));
            Assert.AreSequenceEqual(original, File.ReadAllBytes(path));
            Assert.ThrowsExactly<ReplException>(() => AssemblyExporter.Save(IlLines.Load("br MISSING"), path));
            Assert.AreSequenceEqual(original, File.ReadAllBytes(path));
            Assert.AreSequenceEqual([path], Directory.GetFiles(directory));

            AssemblyExporter.Save(complete, path);
            using var pe = new PEReader(File.OpenRead(path));
            var metadata = pe.GetMetadataReader();
            Assert.AreEqual("kept", metadata.GetString(metadata.GetAssemblyDefinition().Name));
            Assert.AreSequenceEqual([path], Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A directory destination is refused before export and cannot acquire a partially written image.
    /// </summary>
    [TestMethod]
    public void Save_DirectoryDestinationIsRejected()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-export-destination-").FullName;
        try
        {
            var error = Assert.ThrowsExactly<ArgumentException>(() => AssemblyExporter.Save(new Session(), directory));
            Assert.AreEqual("path", error.ParamName);
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    /// <summary>
    /// Cancellation after an intermediate or final write batch preserves the destination and removes the sibling file.
    /// </summary>
    /// <param name="imageSize">Whether the first physical batch is also the final batch before publication.</param>
    [TestMethod]
    [DataRow(64 * 1024)]
    [DataRow(128 * 1024)]
    public void Save_CancellationDuringWriteRemovesTemporaryFile(int imageSize)
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-interrupted-export-").FullName;
        try
        {
            var path = Path.Join(directory, "original.dll");
            byte[] original = [2, 4, 6];
            File.WriteAllBytes(path, original);
            var image = new byte[imageSize];
            using var cancellation = new CancellationTokenSource();
            var observed = 0L;
            Assert.ThrowsExactly<OperationCanceledException>(() => AtomicAssemblyFile.Write(path, image, written =>
            {
                observed = written;
                Assert.HasCount(2, Directory.GetFiles(directory), "The first batch is written to a sibling of the intact destination.");
                Assert.AreSequenceEqual(original, File.ReadAllBytes(path));
                cancellation.Cancel();
            }, cancellation.Token));

            Assert.AreEqual(64 * 1024L, observed);
            Assert.AreSequenceEqual(original, File.ReadAllBytes(path));
            Assert.AreSequenceEqual([path], Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Corrupted source emitted by ilrepl is rejected by the real assembler with a nonzero exit status.
    /// </summary>
    [TestMethod]
    public async Task Ilasm_CorruptedExportReallyRejectsSource()
    {
        var source = IlLines.Load("ldc.i4.s 42").ToIlAsm();
        var corrupted = source.Replace("ldc.i4.s 42", "not.an.opcode 42", StringComparison.Ordinal);
        Assert.AreNotEqual(source, corrupted);
        var result = await IlasmLocator.AssembleResultAsync(corrupted, TestContext.CancellationToken);
        Assert.AreNotEqual(0, result.Tool.ExitCode);
        Assert.IsEmpty(result.Image);
        Assert.Contains("not.an.opcode", result.Tool.StandardOutput + result.Tool.StandardError);
    }

    /// <summary>
    /// A deliberately invalid return in an ilrepl image produces the expected verifier error rather than a harness exception.
    /// </summary>
    [TestMethod]
    public void IlVerify_CorruptedExportReportsExactCode()
    {
        var original = AssemblyExporter.Write(IlLines.Load(".method int32 Answer() { ldc.i4.s 42; ret }"), "VerifierControl");
        using var input = new MemoryStream(original);
        using var module = ModuleDefinition.ReadModule(input);
        var answer = module.Types.Single(type => type.FullName == "IlRepl.Cell").Methods.Single(method => method.Name == "Answer");
        answer.Body.Instructions[0].OpCode = OpCodes.Ldstr;
        answer.Body.Instructions[0].Operand = "wrong return type";
        using var output = new MemoryStream();
        module.Write(output);
        using var cleanVerifier = new IlVerificationOracle();
        Assert.IsEmpty(cleanVerifier.Verify(original));
        using var brokenVerifier = new IlVerificationOracle();
        Assert.AreSequenceEqual([VerifierError.StackUnexpected], brokenVerifier.Verify(output.ToArray()).Distinct().ToArray());
    }

    /// <summary>
    /// Corrupting ilrepl's own rendered body proves the verifier detects invalid stack merges, branches, and exception exits.
    /// </summary>
    /// <param name="name">The existing corpus's invalid body and precise expected verification codes.</param>
    [TestMethod]
    [DataRow("Underflow")]
    [DataRow("WrongDepth")]
    [DataRow("WrongType")]
    [DataRow("BackwardStack")]
    [DataRow("NonemptyTry")]
    [DataRow("ReturnInTry")]
    [DataRow("WrongReturn")]
    public void IlVerify_CorruptedRenderedBodiesReportExactCodes(string name)
    {
        var example = ControlFlowExamples.All.Single(candidate => candidate.Name == name);
        var session = IlLines.Load(".method int32 ExportControl(int32 n) {", "ldc.i4.s 42", "ret", "}");
        var source = session.ToIlAsm();
        using var clean = new IlVerificationOracle();
        Assert.IsEmpty(clean.Verify(IlasmLocator.Assemble(source)));
        var start = source.IndexOf("ldc.i4.s 42", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start);
        var end = source.IndexOf("ret", start, StringComparison.Ordinal) + 3;
        var corrupted = source[..start] + string.Join('\n', example.Body) + source[end..];
        using var broken = new IlVerificationOracle();
        Assert.AreSequenceEqual(example.Verification.Split(',').Order(),
            broken.Verify(IlasmLocator.Assemble(corrupted)).Select(code => code.ToString()).Distinct().Order());
    }

    /// <summary>
    /// The independent metadata reader detects changed layout and custom modifiers even when method results are unchanged.
    /// </summary>
    [TestMethod]
    public void Metadata_CorruptedLayoutAndModifiersAreDetected()
    {
        var session = IlLines.Load(".class public explicit sealed Shape extends [System.Runtime]System.ValueType {",
            ".size 8", ".field [0] public int32 Value", "}", ".class public Signals {",
            ".field public static int32 modreq([System.Runtime]System.Runtime.CompilerServices.IsVolatile) Value", "}");
        var image = AssemblyExporter.Write(session, "MetadataControl");
        var baseline = ExportMetadata.Read(image);
        Assert.Contains(line => line.StartsWith("type Shape ", StringComparison.Ordinal) && line.EndsWith("layout 8,0"), baseline);
        Assert.Contains(line => line.StartsWith("field Signals::Value ", StringComparison.Ordinal)
            && line.Contains("System.Runtime.CompilerServices.IsVolatile)", StringComparison.Ordinal), baseline);
        Assert.AreSequenceEqual(baseline, ExportMetadata.Read(IldasmLocator.RoundTrip(image)));
        using var input = new MemoryStream(image);
        using var module = ModuleDefinition.ReadModule(input);
        var shape = module.Types.Single(type => type.Name == "Shape");
        Assert.AreEqual(8, shape.ClassSize);
        shape.ClassSize = 16;
        module.Types.Single(type => type.Name == "Signals").Fields.Single().FieldType = module.TypeSystem.Int32;
        using var output = new MemoryStream();
        module.Write(output);
        var corrupted = ExportMetadata.Read(output.ToArray());
        Assert.Contains(line => line.StartsWith("type Shape ", StringComparison.Ordinal) && line.EndsWith("layout 16,0"), corrupted);
        Assert.DoesNotContain(line => line.Contains("modreq(", StringComparison.Ordinal), corrupted);
        Assert.HasCount(2, baseline.Except(corrupted).ToArray());
        Assert.HasCount(2, corrupted.Except(baseline).ToArray());
    }

    /// <summary>
    /// An out-of-range generic index in an exported field remains visible to the independent signature comparison.
    /// </summary>
    [TestMethod]
    public void Metadata_CorruptedGenericIndexIsDetected()
    {
        var session = IlLines.Load(".class public Box`1<T> {", ".field public !0 Value", "}");
        var image = AssemblyExporter.Write(session, "GenericIndexControl");
        var baseline = ExportMetadata.Read(image);
        Assert.Contains("field Box`1::Value Public !0 offset -1", baseline);
        Assert.AreSequenceEqual(baseline, ExportMetadata.Read(IldasmLocator.RoundTrip(image)));
        using var input = new MemoryStream(image);
        using var module = ModuleDefinition.ReadModule(input);
        var unexportedOwner = new TypeDefinition("", "NotExported`2", TypeAttributes.Public);
        unexportedOwner.GenericParameters.Add(new GenericParameter("T", unexportedOwner));
        unexportedOwner.GenericParameters.Add(new GenericParameter("U", unexportedOwner));
        module.Types.Single(type => type.Name == "Box`1").Fields.Single().FieldType = unexportedOwner.GenericParameters[1];
        using var output = new MemoryStream();
        module.Write(output);
        var corrupted = ExportMetadata.Read(output.ToArray());
        Assert.Contains("field Box`1::Value Public !1 offset -1", corrupted);
        Assert.HasCount(1, baseline.Except(corrupted).ToArray());
        Assert.HasCount(1, corrupted.Except(baseline).ToArray());
        Assert.AreSequenceEqual(baseline.Where(line => line.Contains(" generic ", StringComparison.Ordinal)),
            corrupted.Where(line => line.Contains(" generic ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Changing a referenced type's assembly is detected even when names and the complete assembly-reference table are unchanged.
    /// </summary>
    [TestMethod]
    public void Metadata_CorruptedReferenceBindingIsDetected()
    {
        var session = IlLines.Load(".class public Bindings {", ".field public class [System.Runtime]System.Uri Value",
            ".field public class [System.Console]System.Console Other", "}");
        var image = AssemblyExporter.Write(session, "ReferenceControl");
        var baseline = ExportMetadata.Read(image);
        Assert.Contains(line => line.StartsWith("field Bindings::Value Public [System.Private.Uri, ", StringComparison.Ordinal)
            && line.EndsWith("]System.Uri offset -1", StringComparison.Ordinal), baseline);
        Assert.AreSequenceEqual(baseline, ExportMetadata.Read(IldasmLocator.RoundTrip(image)));
        using var input = new MemoryStream(image);
        using var module = ModuleDefinition.ReadModule(input);
        var bindings = module.Types.Single(type => type.Name == "Bindings");
        bindings.Fields.Single(field => field.Name == "Value").FieldType.Scope
            = bindings.Fields.Single(field => field.Name == "Other").FieldType.Scope;
        using var output = new MemoryStream();
        module.Write(output);
        var corrupted = ExportMetadata.Read(output.ToArray());
        Assert.Contains(line => line.StartsWith("field Bindings::Value Public [System.Console, ", StringComparison.Ordinal)
            && line.EndsWith("]System.Uri offset -1", StringComparison.Ordinal), corrupted);
        Assert.HasCount(1, baseline.Except(corrupted).ToArray());
        Assert.HasCount(1, corrupted.Except(baseline).ToArray());
        Assert.AreSequenceEqual(baseline.Where(line => line.StartsWith("reference ", StringComparison.Ordinal)),
            corrupted.Where(line => line.StartsWith("reference ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Fresh export processes honor input EOF, Unicode, environment, cultures, and both declared runtime profiles.
    /// </summary>
    /// <param name="profile">The runtime profile selected before process startup.</param>
    [TestMethod]
    [DataRow("deterministic")]
    [DataRow("tiered")]
    public async Task Execution_UsesExplicitInputEnvironmentAndCultures(string profile)
    {
        var session = IlLines.Load(
            "call class [System.Runtime]System.IO.TextReader [System.Console]System.Console::get_In()",
            "callvirt instance string [System.Runtime]System.IO.TextReader::ReadToEnd()",
            "ldstr \"ILREPL_EXPORT_FIXTURE\"",
            "call string [System.Runtime]System.Environment::GetEnvironmentVariable(string)",
            "call class [System.Runtime]System.Globalization.CultureInfo "
                + "[System.Runtime]System.Globalization.CultureInfo::get_CurrentCulture()",
            "callvirt instance string [System.Runtime]System.Globalization.CultureInfo::get_Name()",
            "call class [System.Runtime]System.Globalization.CultureInfo "
                + "[System.Runtime]System.Globalization.CultureInfo::get_CurrentUICulture()",
            "callvirt instance string [System.Runtime]System.Globalization.CultureInfo::get_Name()",
            "call string [System.Runtime]System.String::Concat(string, string, string, string)");
        using var execution = new ExportExecution();
        foreach (var image in new[] { AssemblyExporter.Write(session, "Inputs"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var observation = await execution.RunAsync(image, "IlRepl.Cell", "Run", profile, standardInput: "héllo\r\n世界\n",
                environment: new Dictionary<string, string> { ["ILREPL_EXPORT_FIXTURE"] = "fixture:" }, culture: "fr-FR",
                uiCulture: "de-DE", cancellationToken: TestContext.CancellationToken);
            Assert.IsNull(observation.ExceptionType);
            Assert.AreEqual(typeof(string).AssemblyQualifiedName, observation.Result.Type);
            Assert.AreEqual("héllo\r\n世界\nfixture:fr-FRde-DE", observation.Result.Value.GetString());
            Assert.AreEqual(profile, observation.Profile);
            Assert.AreEqual(Environment.Version.ToString(), observation.Runtime);
        }

        var eof = await execution.RunAsync(AssemblyExporter.Write(session, "Eof"), "IlRepl.Cell", "Run", profile,
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual("", eof.Result.Value.GetString());
        Assert.IsNull(eof.ExceptionType);
    }

    /// <summary>
    /// Independent and exported methods consume identical Unicode, mixed line endings, and EOF through actual worker input pipes.
    /// </summary>
    /// <param name="profile">The process startup compilation settings.</param>
    /// <param name="raw">Whether user IL opens the operating-system input stream directly.</param>
    [TestMethod]
    [DataRow("deterministic", false)]
    [DataRow("deterministic", true)]
    [DataRow("tiered", false)]
    [DataRow("tiered", true)]
    public async Task Execution_ComparisonWorkersPreserveInputBytesAndEof(string profile, bool raw)
    {
        var body = raw
            ? "call class [System.Runtime]System.IO.Stream [System.Console]System.Console::OpenStandardInput()\n"
                + "newobj instance void [System.Runtime]System.IO.StreamReader::.ctor(class [System.Runtime]System.IO.Stream)\n"
            : "call class [System.Runtime]System.IO.TextReader [System.Console]System.Console::get_In()\n";
        body += "callvirt instance string [System.Runtime]System.IO.TextReader::ReadToEnd()\nret";
        var source = ".assembly extern System.Runtime {}\n.assembly extern System.Console {}\n.assembly InputFixture {}\n"
            + ".class public InputFixture extends [System.Runtime]System.Object {\n"
            + ".method public static string Read() cil managed {\n.maxstack 8\n" + body + "\n}\n}";
        var independent = IlasmLocator.Assemble(source);
        var session = IlLines.Load((".method string Read() {\n" + body + "\n}").Split('\n'));
        var saved = AssemblyExporter.Write(session, "InputExports");
        byte[][] artifacts = [saved, IlasmLocator.Assemble(session.ToIlAsm()), IldasmLocator.RoundTrip(saved)];
        using var execution = new ExportExecution();
        foreach (var input in new[] { "", "Aéλ漢字🌍\r\nsecond\nthird\r終" })
        {
            foreach (var artifact in artifacts)
            {
                var original = ObserveInput(independent, "InputFixture", out var originalDependency);
                var exported = ObserveInput(artifact, "IlRepl.Cell", out var exportedDependency);
                var package = new ComparisonPackage("InputConformance", "independent", 1, original, exported,
                    [originalDependency, exportedDependency],
                    execution.CreateEnvironment(profile), "fr-FR", "de-DE", input, [], 10_000, 64 * 1024, true);
                var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
                Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
                foreach (var side in new[] { result.Original, result.Edited })
                {
                    Assert.AreEqual("completed", side.Outcome, side.Detail);
                    Assert.IsNull(side.Exception);
                    Assert.AreEqual(input, side.Result!.Value);
                    Assert.AreEqual("", side.StandardOutput);
                    Assert.AreEqual("", side.StandardError);
                }
            }
        }
    }

    private static ComparisonImage ObserveInput(byte[] image, string type, out ComparisonAssembly dependency)
    {
        using var input = new MemoryStream(image);
        using var module = ModuleDefinition.ReadModule(input);
        dependency = new ComparisonAssembly(module.Assembly.Name.FullName, image);
        var version = module.Assembly.Name.Version;
        // The worker's observation wrapper calls the original bytes through a dependency, without rewriting the tested method.
        var source = $$"""
            .assembly extern System.Runtime {}
            .assembly extern IlRepl.Engine {}
            .assembly extern {{module.Assembly.Name.Name}} {
              .ver {{version.Major}}:{{version.Minor}}:{{version.Build}}:{{version.Revision}}
            }
            .assembly InputObserver {}
            .class public Observer extends [System.Runtime]System.Object {
              .method public static string Read() cil managed {
                .maxstack 8
                .locals init (int32 invocation, string result)
                ldnull
                ldc.i4.0
                newarr [System.Runtime]System.Object
                ldnull
                call int32 [IlRepl.Engine]IlRepl.Engine.ComparisonProbe::Enter(object, object[], int32[])
                stloc.0
                call string [{{module.Assembly.Name.Name}}]{{type}}::Read()
                stloc.1
                ldloc.0
                ldnull
                ldc.i4.0
                newarr [System.Runtime]System.Object
                ldloc.1
                ldnull
                ldnull
                call void [IlRepl.Engine]IlRepl.Engine.ComparisonProbe::Leave(int32, object, object[], object,
                    class [System.Runtime]System.Exception, int32[])
                ldloc.1
                ret
              }
            }
            """;
        return new ComparisonImage(IlasmLocator.Assemble(source), "Observer", "Read", 0, [], [], [], new Dictionary<string, string>());
    }

    /// <summary>
    /// Every independent image starts from the same initial filesystem instead of observing the preceding image's writes.
    /// </summary>
    [TestMethod]
    public async Task Execution_RestoresFilesystemBetweenArtifacts()
    {
        var session = IlLines.Load("ldstr \"state.txt\"", "call string [System.Runtime]System.IO.File::ReadAllText(string)",
            "ldstr \"state.txt\"", "ldstr \"changed\"", "call void [System.Runtime]System.IO.File::WriteAllText(string, string)");
        var saved = AssemblyExporter.Write(session, "Files");
        using var execution = new ExportExecution(new Dictionary<string, string> { ["state.txt"] = "initial" });
        foreach (var image in new[] { saved, IlasmLocator.Assemble(session.ToIlAsm()), IldasmLocator.RoundTrip(saved) })
        {
            var observation = await execution.RunAsync(image, "IlRepl.Cell", "Run", "deterministic",
                cancellationToken: TestContext.CancellationToken);
            Assert.IsNull(observation.ExceptionType);
            Assert.AreEqual("initial", observation.Result.Value.GetString());
        }
    }

    /// <summary>
    /// Exported code retains more than three MiB of live frames on its explicit execution thread under both runtime profiles.
    /// </summary>
    /// <param name="profile">The process startup compilation settings.</param>
    [TestMethod]
    [DataRow("deterministic")]
    [DataRow("tiered")]
    public async Task Execution_ExplicitStackAllowsThreeMiBOfLiveFrames(string profile)
    {
        var session = IlLines.Load("ldc.i4 192", "call int32 IlRepl.Tests.Protocol.ExecutionThreadFixture::Recurse(int32)");
        var saved = AssemblyExporter.Write(session, "ExportStack");
        using var execution = new ExportExecution();
        foreach (var image in new[] { saved, IlasmLocator.Assemble(session.ToIlAsm()), IldasmLocator.RoundTrip(saved) })
        {
            var observed = await execution.RunAsync(image, "IlRepl.Cell", "Run", profile,
                cancellationToken: TestContext.CancellationToken);
            Assert.IsNull(observed.ExceptionType);
            Assert.AreEqual(typeof(int).AssemblyQualifiedName, observed.Result.Type);
            Assert.AreEqual(18528, observed.Result.Value.GetInt32());
            Assert.AreEqual("", observed.StandardOutput);
            Assert.AreEqual("", observed.StandardError);
        }
    }

    /// <summary>
    /// A semantic corruption changes the independently observed result even though the assembly remains valid.
    /// </summary>
    [TestMethod]
    public async Task Execution_CorruptedResultCannotMatchExpectedObservation()
    {
        var saved = AssemblyExporter.Write(IlLines.Load("ldc.i4.s 42"), "ResultControl");
        using var input = new MemoryStream(saved);
        using var module = ModuleDefinition.ReadModule(input);
        var run = module.Types.Single(type => type.FullName == "IlRepl.Cell").Methods.Single(method => method.Name == "Run");
        run.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldc_I4_S).Operand = (sbyte)41;
        using var output = new MemoryStream();
        module.Write(output);
        using var execution = new ExportExecution();
        var original = await execution.RunAsync(saved, "IlRepl.Cell", "Run", "deterministic",
            cancellationToken: TestContext.CancellationToken);
        var corrupted = await execution.RunAsync(output.ToArray(), "IlRepl.Cell", "Run", "deterministic",
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(42, original.Result.Value.GetInt32());
        Assert.AreEqual(41, corrupted.Result.Value.GetInt32());
        Assert.AreNotEqual(original.Result.Value.GetInt32(), corrupted.Result.Value.GetInt32());
    }

    /// <summary>
    /// User exceptions and both console streams remain observable through saved, rendered, and disassembled images.
    /// </summary>
    [TestMethod]
    public async Task Execution_PreservesExceptionTypeAndConsoleStreams()
    {
        var session = IlLines.Load("ldstr \"stdout\"", "call void [System.Console]System.Console::Write(string)",
            "call class [System.Runtime]System.IO.TextWriter [System.Console]System.Console::get_Error()",
            "ldstr \"stderr\"", "callvirt instance void [System.Runtime]System.IO.TextWriter::Write(string)",
            "ldstr \"fixture\"", "newobj instance void [System.Runtime]System.InvalidOperationException::.ctor(string)", "throw");
        var saved = AssemblyExporter.Write(session, "ExceptionObservation");
        using var execution = new ExportExecution();
        foreach (var image in new[] { saved, IlasmLocator.Assemble(session.ToIlAsm()), IldasmLocator.RoundTrip(saved) })
        {
            foreach (var profile in new[] { "deterministic", "tiered" })
            {
                var observed = await execution.RunAsync(image, "IlRepl.Cell", "Run", profile,
                    cancellationToken: TestContext.CancellationToken);
                Assert.AreEqual(typeof(InvalidOperationException).FullName, observed.ExceptionType);
                Assert.IsNull(observed.Result.Type);
                Assert.AreEqual("stdout", observed.StandardOutput);
                Assert.AreEqual("stderr", observed.StandardError);
            }
        }
    }
}
