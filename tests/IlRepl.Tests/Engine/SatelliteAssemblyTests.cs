using System.Diagnostics;
using System.Globalization;
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
/// Real cultural satellite resolution remains successful for originals while copied lookups reject recoverably.
/// </summary>
[TestClass]
public sealed partial class SatelliteAssemblyTests
{
    private const string ProbeDirectory = "ILREPL_SATELLITE_PROBE_DIRECTORY";
    private static readonly string[] Dispatches = ["direct", "reflection", "delegate", "token", "lookalike"];

    /// <summary>
    /// Supplies cancellation for actual child processes and comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Both overloads and call forms preserve source probing, precise rejection, atomic recovery, workers and independent exports.
    /// </summary>
    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task Edit_SatelliteLookupsRetainOriginalFileContext()
    {
        var directory = Environment.GetEnvironmentVariable(ProbeDirectory);
        if (directory is not null)
        {
            foreach (var versioned in new[] { false, true })
            {
                foreach (var dispatch in Dispatches)
                {
                    await AssertFileCaseAsync(directory, versioned, dispatch);
                }
            }

            return;
        }

        directory = Path.Combine(Path.GetTempPath(), "satellite-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!, WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            start.ArgumentList.Add("--filter");
            start.ArgumentList.Add("FullyQualifiedName~SatelliteAssemblyTests.Edit_SatelliteLookupsRetainOriginalFileContext");
            start.Environment[ProbeDirectory] = directory;
            using var child = Process.Start(start) ?? throw new InvalidOperationException("the satellite probe did not start");
            var output = child.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
            var error = child.StandardError.ReadToEndAsync(TestContext.CancellationToken);
            try
            {
                await child.WaitForExitAsync(TestContext.CancellationToken);
                Assert.AreEqual(0, child.ExitCode, await output + Environment.NewLine + await error);
                await AssertChangedSatelliteAsync(directory);
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

    private async Task AssertChangedSatelliteAsync(string directory)
    {
        var package = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, "satellite-package.json")),
            ProtocolJsonContext.Default.ComparisonPackage)!;
        var satellitePath = File.ReadAllText(Path.Combine(directory, "satellite.path"));
        var sourcePath = File.ReadAllText(Path.Combine(directory, "satellite-source.path"));
        var source = File.ReadAllBytes(sourcePath);
        byte[] changed = [1, 2, 3];
        File.WriteAllBytes(satellitePath, changed);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt == 1)
            {
                File.Delete(satellitePath);
            }

            var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
            Assert.AreEqual("incomplete", result.Outcome);
            Assert.AreEqual("setup-failed", result.Original.Outcome, result.Original.Detail);
            Assert.IsNotNull(result.Original.Exception);
            Assert.IsEmpty(result.Original.Invocations);
            Assert.IsNull(result.Original.Result);
            Assert.Contains(attempt == 0 ? "the original assembly file changed after comparison capture"
                : "the original assembly file is unavailable", result.Original.Detail!);
            Assert.Contains(satellitePath, result.Original.Detail!);
            AssertSide(result.Edited, 43);
            Assert.AreSequenceEqual(source, File.ReadAllBytes(sourcePath));
            if (attempt == 0)
            {
                Assert.AreSequenceEqual(changed, File.ReadAllBytes(satellitePath));
            }
            else
            {
                Assert.IsFalse(File.Exists(satellitePath));
            }
        }
    }

    /// <summary>
    /// Byte-loaded originals retain an independently loaded satellite in fresh workers without any static satellite reference.
    /// </summary>
    [TestMethod]
    public async Task Compare_ImageLoadedSatelliteIsCapturedForOriginal()
    {
        var fixture = SatelliteAssemblyFixture.Create(versioned: true, "direct");
        var session = new Session();
        var assembly = session.Resolver.LoadImage(fixture.Source);
        var satellite = session.Resolver.LoadImage(fixture.Satellite);
        Assert.AreEqual("", assembly.Location);
        Assert.AreEqual("", satellite.Location);
        AssertMetadata(fixture.Source, fixture.Satellite, fixture.Name);
        AssertSatellite(assembly, satellite, fixture.Name);
        var original = assembly.GetType("SatelliteInspection.Owner")!.GetMethod("Read")!;
        Assert.AreEqual(42, original.Invoke(null, null));
        var edit = session.PrepareEdit("int32 [" + fixture.Name + "]SatelliteInspection.Owner::Read()", "Copy");
        Assert.Contains(problem => problem.Contains(SatelliteAssemblyFixture.Problem, StringComparison.Ordinal), edit.Problems);
        session.CommitEdit(edit.Name, Corrected(43));
        var package = ComparisonCapture.Create(session, "Copy ()");
        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
        AssertComparison(result, "different", 43);
        var captured = Assert.ContainsSingle(package.Dependencies.Where(dependency => dependency.Name == satellite.FullName));
        Assert.AreSequenceEqual(fixture.Satellite, captured.Image);
        Assert.IsNull(captured.OriginalLocation);
        Assert.AreEqual(42, original.Invoke(null, null));
    }

    private async Task AssertFileCaseAsync(string directory, bool versioned, string dispatch)
    {
        TestContext.WriteLine("satellite " + (versioned ? "versioned" : "culture") + ": " + dispatch);
        var fixture = SatelliteAssemblyFixture.Create(versioned, dispatch);
        var sourcePath = Path.Combine(directory, fixture.Name + ".dll");
        var satelliteDirectory = Path.Combine(directory, SatelliteAssemblyFixture.Culture);
        Directory.CreateDirectory(satelliteDirectory);
        var satellitePath = Path.Combine(satelliteDirectory, fixture.Name + ".resources.dll");
        File.WriteAllBytes(sourcePath, fixture.Source);
        File.WriteAllBytes(satellitePath, fixture.Satellite);
        AssertMetadata(fixture.Source, fixture.Satellite, fixture.Name);
        var session = new Session();
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(sourcePath);
        session.Resolver.Load(assembly.FullName!);
        Assert.AreEqual(sourcePath, assembly.Location);
        var satellite = assembly.GetSatelliteAssembly(CultureInfo.GetCultureInfo(SatelliteAssemblyFixture.Culture));
        Assert.AreEqual(satellitePath, satellite.Location);
        AssertSatellite(assembly, satellite, fixture.Name);
        var original = assembly.GetType("SatelliteInspection.Owner")!.GetMethod("Read")!;
        Assert.AreEqual(42, original.Invoke(null, null));
        var edit = session.PrepareEdit("int32 [" + fixture.Name + "]SatelliteInspection.Owner::Read()", "Copy");
        var source = edit.Source;
        var supported = dispatch is "token" or "lookalike";
        if (supported)
        {
            Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
            session.CommitEdit(edit.Name, source);
            Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, null));
        }
        else
        {
            Assert.Contains(problem => problem.Contains(SatelliteAssemblyFixture.Problem, StringComparison.Ordinal), edit.Problems);
            Assert.Contains(dependency => dependency.Symbol.Contains("GetSatelliteAssembly", StringComparison.Ordinal)
                && dependency.Disposition.Contains(SatelliteAssemblyFixture.Problem, StringComparison.Ordinal), edit.Dependencies);
            var completion = session.CompletionRevision;
            var error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
            Assert.Contains(SatelliteAssemblyFixture.Problem, error.Message);
            Assert.AreEqual(source, edit.Source);
            Assert.AreEqual(0, edit.Revision);
            Assert.AreEqual(completion, session.CompletionRevision);
            Assert.IsNull(edit.Method);
            session.CommitEdit(edit.Name, Corrected(42));
            Assert.IsEmpty(edit.Problems);
            var previous = edit.Method;
            var corrected = edit.Source;
            completion = session.CompletionRevision;
            error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
            Assert.Contains(SatelliteAssemblyFixture.Problem, error.Message);
            Assert.AreSame(previous, edit.Method);
            Assert.AreEqual(corrected, edit.Source);
            Assert.AreEqual(1, edit.Revision);
            Assert.AreEqual(completion, session.CompletionRevision);
        }

        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        var package = ComparisonCapture.Create(session, "Copy ()");
        if (!supported)
        {
            var dependency = Assert.ContainsSingle(package.Dependencies.Where(item => item.Name == assembly.FullName));
            Assert.AreEqual(sourcePath, dependency.OriginalLocation);
            Assert.AreSequenceEqual(fixture.Source, dependency.Image);
        }

        AssertComparison(await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken), "match", 42);
        var expected = supported ? 42 : 43;
        if (!supported)
        {
            session.CommitEdit(edit.Name, Corrected(43));
            Assert.AreEqual(43, edit.Method!.Invoke(null, null));
            var changedPackage = ComparisonCapture.Create(session, "Copy ()");
            AssertComparison(await ProcessComparisonRunner.RunAsync(changedPackage, TestContext.CancellationToken), "different", 43);
            if (versioned && dispatch == "direct")
            {
                var captured = Assert.ContainsSingle(changedPackage.Dependencies.Where(item => item.Name == satellite.FullName));
                Assert.AreEqual(satellitePath, captured.OriginalLocation);
                Assert.AreSequenceEqual(fixture.Satellite, captured.Image);
                File.WriteAllText(Path.Combine(directory, "satellite-package.json"),
                    JsonSerializer.Serialize(changedPackage, ProtocolJsonContext.Default.ComparisonPackage));
                File.WriteAllText(Path.Combine(directory, "satellite.path"), satellitePath);
                File.WriteAllText(Path.Combine(directory, "satellite-source.path"), sourcePath);
            }
        }

        Assert.AreEqual(42, original.Invoke(null, null));
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "satellite-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("satellite-copy", isCollectible: true);
            try
            {
                var exported = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(expected, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }

        Assert.AreSequenceEqual(fixture.Source, File.ReadAllBytes(sourcePath));
        Assert.AreSequenceEqual(fixture.Satellite, File.ReadAllBytes(satellitePath));
    }

    private static void AssertMetadata(byte[] source, byte[] satellite, string name)
    {
        using var sourceReader = new PEReader(new MemoryStream(source));
        var sourceMetadata = sourceReader.GetMetadataReader();
        Assert.DoesNotContain(reference => sourceMetadata.GetString(reference.Name).EndsWith(".resources", StringComparison.Ordinal),
            sourceMetadata.AssemblyReferences.Select(sourceMetadata.GetAssemblyReference));
        using var satelliteReader = new PEReader(new MemoryStream(satellite));
        var metadata = satelliteReader.GetMetadataReader();
        var definition = metadata.GetAssemblyDefinition();
        Assert.AreEqual(name + ".resources", metadata.GetString(definition.Name));
        Assert.AreEqual(SatelliteAssemblyFixture.Culture, metadata.GetString(definition.Culture));
        Assert.AreEqual(SatelliteAssemblyFixture.Version, definition.Version);
        var resource = metadata.GetManifestResource(Assert.ContainsSingle(metadata.ManifestResources));
        Assert.AreEqual("Satellite.payload", metadata.GetString(resource.Name));
        Assert.IsTrue(resource.Implementation.IsNil);
    }

    private static void AssertSatellite(Assembly assembly, Assembly satellite, string name)
    {
        Assert.AreEqual(name + ".resources", satellite.GetName().Name);
        Assert.AreEqual(SatelliteAssemblyFixture.Culture, satellite.GetName().CultureName);
        Assert.AreEqual(SatelliteAssemblyFixture.Version, satellite.GetName().Version);
        var culture = CultureInfo.GetCultureInfo(SatelliteAssemblyFixture.Culture);
        Assert.AreSame(satellite, assembly.GetSatelliteAssembly(culture));
        Assert.AreSame(satellite, assembly.GetSatelliteAssembly(culture, SatelliteAssemblyFixture.Version));
        using var stream = satellite.GetManifestResourceStream("Satellite.payload");
        Assert.IsNotNull(stream);
        Assert.AreEqual(42, stream.ReadByte());
        Assert.AreEqual(17, stream.ReadByte());
        Assert.AreEqual(255, stream.ReadByte());
        Assert.AreEqual(-1, stream.ReadByte());
    }

    private static void AssertComparison(ComparisonReply result, string outcome, int edited)
    {
        Assert.AreEqual(outcome, result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        AssertSide(result.Original, 42);
        AssertSide(result.Edited, edited);
    }

    private static void AssertSide(ComparisonSide side, int expected)
    {
        Assert.AreEqual("completed", side.Outcome, side.Detail);
        Assert.IsNull(side.Exception);
        Assert.IsNotNull(side.Result);
        Assert.AreEqual("scalar", side.Result.Kind);
        Assert.AreEqual(expected.ToString(CultureInfo.InvariantCulture), side.Result.Value);
        var invocation = Assert.ContainsSingle(side.Invocations);
        Assert.IsNull(invocation.Exception);
        var returned = Assert.ContainsSingle(invocation.Outputs.Where(member => member.Name == "return")).Value;
        Assert.AreEqual(side.Result, returned);
    }

    private static string Corrected(int value) => ".method public static int32 Read() {\nldc.i4 "
        + value.ToString(CultureInfo.InvariantCulture) + "\nret\n}";
}
