using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Json;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Real file-loaded originals expose source metadata before unsupported copied observations are rejected recoverably.
/// </summary>
[TestClass]
public sealed class AssemblyLocationTests
{
    private const string ProbeDirectory = "ILREPL_SOURCE_METADATA_PROBE_DIRECTORY";

    /// <summary>
    /// Supplies cancellation for actual file-loading and comparison child processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// File and image observations reject atomically while stable constants and metadata controls retain executable behavior.
    /// </summary>
    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task Edit_FileLoadedSourceMetadataRemainsRecoverable()
    {
        var directory = Environment.GetEnvironmentVariable(ProbeDirectory);
        if (directory is not null)
        {
            foreach (var (target, api, dispatch) in AssemblyLocationFixture.Cases)
                await AssertCaseAsync(directory, target, api, dispatch, supported: false);
            foreach (var (target, api, dispatch) in AssemblyLocationFixture.SupportedCases)
                await AssertCaseAsync(directory, target, api, dispatch, supported: true);
            return;
        }

        directory = Path.Combine(Path.GetTempPath(), "source-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!, WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            start.ArgumentList.Add("--filter");
            start.ArgumentList.Add("FullyQualifiedName~AssemblyLocationTests.Edit_FileLoadedSourceMetadataRemainsRecoverable");
            start.Environment[ProbeDirectory] = directory;
            using var child = Process.Start(start) ?? throw new InvalidOperationException("the source metadata probe did not start");
            var output = child.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
            var error = child.StandardError.ReadToEndAsync(TestContext.CancellationToken);
            try
            {
                await child.WaitForExitAsync(TestContext.CancellationToken);
                Assert.AreEqual(0, child.ExitCode, await output + Environment.NewLine + await error);
                await AssertChangedOriginalFileAsync(directory);
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        Assert.IsFalse(Directory.Exists(directory));
    }

    private async Task AssertChangedOriginalFileAsync(string directory)
    {
        var json = File.ReadAllText(Path.Combine(directory, "frozen-location.json"));
        var package = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ComparisonPackage)!;
        var path = File.ReadAllText(Path.Combine(directory, "frozen-location.path"));
        byte[] changed = [1, 2, 3];
        File.WriteAllBytes(path, changed);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt == 1) File.Delete(path);
            var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
            Assert.AreEqual("incomplete", result.Outcome);
            Assert.AreEqual("setup-failed", result.Original.Outcome, result.Original.Detail);
            Assert.IsNotNull(result.Original.Exception);
            Assert.IsEmpty(result.Original.Invocations);
            Assert.Contains(attempt == 0 ? "the original assembly file changed after comparison capture"
                : "the original assembly file is unavailable", result.Original.Detail!);
            Assert.Contains(path, result.Original.Detail!);
            Assert.AreEqual("completed", result.Edited.Outcome, result.Edited.Detail);
            Assert.IsNull(result.Edited.Exception);
            Assert.AreEqual("43", result.Edited.Result!.Value);
            Assert.HasCount(1, result.Edited.Invocations);
            if (attempt == 0) Assert.AreSequenceEqual(changed, File.ReadAllBytes(path));
            else Assert.IsFalse(File.Exists(path));
        }
    }

    private async Task AssertCaseAsync(string directory, string target, string api, string dispatch, bool supported)
    {
        TestContext.WriteLine(target + "." + api + ": " + dispatch);
        var path = Path.Combine(directory, "actual-source-" + Guid.NewGuid().ToString("N") + ".exe");
        var fixture = AssemblyLocationFixture.Create(target, api, dispatch, path);
        File.WriteAllBytes(path, fixture.Image);
        var session = new Session();
        var assembly = session.Resolver.Load(path);
        Assert.AreEqual(path, assembly.Location);
        Assert.IsTrue(File.Exists(assembly.Location));
        Assert.AreEqual(AssemblyLocationFixture.Scope, assembly.ManifestModule.ScopeName);
        Assert.AreEqual(AssemblyLocationFixture.ImageVersion, assembly.ImageRuntimeVersion);
        Assert.AreEqual("Main", assembly.EntryPoint!.Name);
        if (AssemblyLocationFixture.IsModuleTable(api))
        {
            string[] expectedModules = [AssemblyLocationFixture.Scope];
            Assert.AreNotEqual(AssemblyLocationFixture.Scope, Path.GetFileName(path));
            Assert.AreSame(assembly.ManifestModule, assembly.GetModule(AssemblyLocationFixture.Scope));
            Assert.AreSequenceEqual(expectedModules, assembly.GetModules().Select(module => module.ScopeName));
            Assert.AreSequenceEqual(expectedModules, assembly.GetModules(true).Select(module => module.ScopeName));
            Assert.AreSequenceEqual(expectedModules,
                assembly.GetLoadedModules().Select(module => module.ScopeName));
            Assert.AreSequenceEqual(expectedModules,
                assembly.GetLoadedModules(true).Select(module => module.ScopeName));
            Assert.AreSequenceEqual(expectedModules, assembly.Modules.Select(module => module.ScopeName));
        }
        using (var reader = new PEReader(new MemoryStream(fixture.Image)))
        {
            var metadata = reader.GetMetadataReader();
            Assert.AreEqual(metadata.GetGuid(metadata.GetModuleDefinition().Mvid), assembly.ManifestModule.ModuleVersionId);
            Assert.AreEqual(CorFlags.ILOnly | CorFlags.Requires32Bit | CorFlags.Prefers32Bit, reader.PEHeaders.CorHeader!.Flags);
            Assert.AreEqual(Characteristics.ExecutableImage, reader.PEHeaders.CoffHeader.Characteristics);
            Assert.AreEqual(Machine.I386, reader.PEHeaders.CoffHeader.Machine);
        }
        if (api == "GetPEKind")
        {
            assembly.ManifestModule.GetPEKind(out var kind, out var machine);
            var observation = "runtime PE kind " + kind + " (" + (int)kind + "), machine " + machine + " (" + (int)machine + ")";
            Assert.AreEqual(PortableExecutableKinds.ILOnly | PortableExecutableKinds.Preferred32Bit, kind, observation);
            Assert.AreEqual(ImageFileMachine.I386, machine, observation);
        }
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]SourceInspection.Owner::Read(string)", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, [fixture.Expected]), target + "." + api);
        var source = edit.Source;
        if (supported)
        {
            Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
            session.CommitEdit(edit.Name, source);
        }
        else
        {
            var problem = AssemblyLocationFixture.Problem(target, api);
            var apiName = api.EndsWith("(bool)", StringComparison.Ordinal) ? api[..^6] : api;
            Assert.Contains(item => item.Contains(problem, StringComparison.Ordinal), edit.Problems,
                string.Join("; ", edit.Problems));
            Assert.Contains(dependency => dependency.Symbol.Contains(apiName, StringComparison.Ordinal)
                && dependency.Disposition.Contains(problem, StringComparison.Ordinal), edit.Dependencies);
            var completion = session.CompletionRevision;
            var error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
            Assert.Contains(problem, error.Message);
            Assert.AreEqual(source, edit.Source);
            Assert.AreEqual(0, edit.Revision);
            Assert.AreEqual(completion, session.CompletionRevision);
            Assert.IsNull(edit.Method);
            session.CommitEdit(edit.Name, ".method public static int32 Read(string expected) {\nldc.i4.s 43\nret\n}");
            Assert.IsEmpty(edit.Problems);
            var previous = edit.Method;
            var corrected = edit.Source;
            completion = session.CompletionRevision;
            error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
            Assert.Contains(problem, error.Message);
            Assert.AreSame(previous, edit.Method);
            Assert.AreEqual(corrected, edit.Source);
            Assert.AreEqual(1, edit.Revision);
            Assert.AreEqual(completion, session.CompletionRevision);
        }

        Assert.AreEqual(supported ? 42 : 43, edit.Method!.Invoke(null, [fixture.Expected]));
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, [fixture.Expected]));
        var command = "Copy (" + LiteralParser.Escape(fixture.Expected) + ")";
        var package = ComparisonCapture.Create(session, command);
        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
        Assert.AreEqual(supported ? "match" : "different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.HasCount(1, side.Invocations);
        }
        Assert.AreEqual("42", result.Original.Result!.Value);
        Assert.AreEqual(supported ? "42" : "43", result.Edited.Result!.Value);
        if (!supported && target == "Assembly" && api == "Location" && dispatch == "direct")
        {
            File.WriteAllText(Path.Combine(directory, "frozen-location.json"),
                JsonSerializer.Serialize(package, ProtocolJsonContext.Default.ComparisonPackage));
            File.WriteAllText(Path.Combine(directory, "frozen-location.path"), path);
            foreach (var line in IlLines.Expand(".method int32 Scenario() { ldstr " + LiteralParser.Escape(fixture.Expected)
                + "; call Copy; ret }")) session.AddLine(line);
            var scenario = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
                TestContext.CancellationToken);
            Assert.AreEqual("different", scenario.Outcome, scenario.Original.Detail + "; " + scenario.Edited.Detail);
            Assert.AreEqual("42", scenario.Original.Result!.Value);
            Assert.AreEqual("43", scenario.Edited.Result!.Value);
            foreach (var side in new[] { scenario.Original, scenario.Edited })
            {
                Assert.AreEqual("completed", side.Outcome, side.Detail);
                Assert.IsNull(side.Exception);
                Assert.HasCount(1, side.Invocations);
            }
        }

        session.AddLine("ldstr " + LiteralParser.Escape(fixture.Expected));
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "source-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("source-copy", isCollectible: true);
            try
            {
                var exported = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(supported ? 42 : 43, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
        Assert.AreEqual(path, assembly.Location);
        Assert.AreSequenceEqual(fixture.Image, File.ReadAllBytes(path));
    }

    /// <summary>
    /// A collectible session original retains its real load context when the corrected copy no longer inspects that context.
    /// </summary>
    [TestMethod]
    public async Task Compare_CollectibleSourceMetadataRetainsOriginalContext()
    {
        var session = IlLines.Load(AssemblyLocationFixture.CollectibleSource(collectible: true).Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        Assert.IsTrue(edit.Original.Requested.Module.Assembly.IsCollectible);
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        var problem = AssemblyLocationFixture.Problem("Assembly", "IsCollectible");
        Assert.Contains(item => item.Contains(problem, StringComparison.Ordinal), edit.Problems);
        var source = edit.Source;
        var completion = session.CompletionRevision;
        var error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.Contains(problem, error.Message);
        Assert.AreEqual(source, edit.Source);
        Assert.AreEqual(0, edit.Revision);
        Assert.AreEqual(completion, session.CompletionRevision);
        Assert.IsNull(edit.Method);
        session.CommitEdit(edit.Name, ".method public static int32 Read() {\nldc.i4.s 43\nret\n}");
        Assert.IsEmpty(edit.Problems);
        var previous = edit.Method;
        var corrected = edit.Source;
        completion = session.CompletionRevision;
        error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.Contains(problem, error.Message);
        Assert.AreSame(previous, edit.Method);
        Assert.AreEqual(corrected, edit.Source);
        Assert.AreEqual(1, edit.Revision);
        Assert.AreEqual(completion, session.CompletionRevision);
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("42", result.Original.Result!.Value);
        Assert.AreEqual("43", result.Edited.Result!.Value);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.HasCount(1, side.Invocations);
        }
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        Assert.AreEqual(43, edit.Method!.Invoke(null, null));
    }
}
