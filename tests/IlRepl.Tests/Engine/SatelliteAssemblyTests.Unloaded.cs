using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Captures adjacent satellites before first use and detects changes to both populated and empty file inventories.
/// </summary>
public sealed partial class SatelliteAssemblyTests
{
    private const string UnloadedProbeDirectory = "ILREPL_UNLOADED_SATELLITE_DIRECTORY";

    /// <summary>
    /// Unloaded satellites freeze without executing the source, and later additions, changes and deletions reject worker setup.
    /// </summary>
    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task Compare_UnloadedSatelliteFilesRetainCapturedContext()
    {
        var directory = Environment.GetEnvironmentVariable(UnloadedProbeDirectory);
        if (directory is not null)
        {
            foreach (var versioned in new[] { false, true })
            {
                foreach (var lowercase in new[] { false, true })
                {
                    await CaptureUnloadedSatelliteAsync(directory, versioned, lowercase, missing: false);
                }
            }

            await CaptureUnloadedSatelliteAsync(directory, versioned: true, lowercase: false, missing: true);
            return;
        }

        directory = Path.Combine(Path.GetTempPath(), "unloaded-satellite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!, WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            start.ArgumentList.Add("--filter");
            start.ArgumentList.Add("FullyQualifiedName~SatelliteAssemblyTests.Compare_UnloadedSatelliteFilesRetainCapturedContext");
            start.Environment[UnloadedProbeDirectory] = directory;
            using var child = Process.Start(start) ?? throw new InvalidOperationException("the unloaded satellite probe did not start");
            var output = child.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
            var error = child.StandardError.ReadToEndAsync(TestContext.CancellationToken);
            try
            {
                await child.WaitForExitAsync(TestContext.CancellationToken);
                Assert.AreEqual(0, child.ExitCode, await output + Environment.NewLine + await error);
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync(CancellationToken.None);
                }
            }

            var packages = Directory.GetFiles(directory, "*.package.json");
            Assert.HasCount(5, packages);
            foreach (var file in packages)
            {
                await ChangeUnloadedSatelliteAsync(file);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.IsFalse(Directory.Exists(directory));
    }

    private async Task CaptureUnloadedSatelliteAsync(string directory, bool versioned, bool lowercase, bool missing)
    {
        TestContext.WriteLine($"unloaded satellite: versioned={versioned}, lowercase={lowercase}, missing={missing}");
        var fixture = SatelliteAssemblyFixture.Create(versioned, "direct");
        var sourcePath = Path.Combine(directory, fixture.Name + ".dll");
        var culture = lowercase ? SatelliteAssemblyFixture.Culture.ToLowerInvariant() : SatelliteAssemblyFixture.Culture;
        var satelliteDirectory = Path.Combine(directory, culture);
        Directory.CreateDirectory(satelliteDirectory);
        var satellitePath = Path.Combine(satelliteDirectory, fixture.Name + ".resources.dll");
        File.WriteAllBytes(sourcePath, fixture.Source);
        if (!missing)
        {
            File.WriteAllBytes(satellitePath, fixture.Satellite);
        }

        var session = new Session();
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(sourcePath);
        session.Resolver.Load(assembly.FullName!);
        var satelliteName = fixture.Name + ".resources";
        void AssertNotLoaded() => Assert.DoesNotContain(candidate => candidate.GetName().Name == satelliteName,
            session.Resolver.Assemblies);
        AssertNotLoaded();
        var edit = session.PrepareEdit("int32 [" + fixture.Name + "]SatelliteInspection.Owner::Read()", "Copy");
        Assert.Contains(problem => problem.Contains(SatelliteAssemblyFixture.Problem, StringComparison.Ordinal), edit.Problems);
        session.CommitEdit(edit.Name, Corrected(43));
        if (versioned && !lowercase && !missing)
        {
            foreach (var invalid in new[] { new byte[] { 1, 2, 3 }, SatelliteAssemblyFixture.Create(false, "direct").Satellite })
            {
                File.WriteAllBytes(satellitePath, invalid);
                var error = Assert.ThrowsExactly<ReplException>(() => ComparisonCapture.Create(session, "Copy ()"));
                Assert.Contains("cannot capture satellite assembly", error.Message);
                Assert.Contains(satellitePath, error.Message);
                Assert.AreEqual(1, edit.Revision);
                Assert.AreEqual(43, edit.Method!.Invoke(null, null));
                AssertNotLoaded();
            }

            File.WriteAllBytes(satellitePath, fixture.Satellite);
        }

        var package = ComparisonCapture.Create(session, "Copy ()");
        AssertNotLoaded();
        var parent = Assert.ContainsSingle(package.Dependencies.Where(dependency => dependency.Name == assembly.FullName));
        Assert.IsNotNull(parent.OriginalSatelliteFiles);
        if (missing)
        {
            Assert.IsEmpty(parent.OriginalSatelliteFiles);
            Assert.DoesNotContain(dependency => new AssemblyName(dependency.Name).Name == satelliteName, package.Dependencies);
            var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
            Assert.AreEqual("different", result.Outcome);
            Assert.IsNotNull(result.Original.Exception);
            Assert.EndsWith("System.IO.FileNotFoundException", result.Original.Exception.Type);
            Assert.IsNotNull(result.Original.Exception.Message);
            Assert.Contains(satelliteName, result.Original.Exception.Message);
            Assert.HasCount(1, result.Original.Invocations);
            AssertSide(result.Edited, 43);
            var failure = Assert.ThrowsExactly<TargetInvocationException>(() => edit.Original.Requested.Invoke(null, null));
            Assert.IsInstanceOfType<FileNotFoundException>(failure.InnerException);
        }
        else
        {
            var captured = Assert.ContainsSingle(package.Dependencies.Where(dependency =>
                new AssemblyName(dependency.Name).Name == satelliteName));
            Assert.AreSequenceEqual(fixture.Satellite, captured.Image);
            var capturedPath = Assert.ContainsSingle(parent.OriginalSatelliteFiles);
            var canonicalPath = Path.Combine(directory, SatelliteAssemblyFixture.Culture, fixture.Name + ".resources.dll");
            Assert.AreEqual(File.Exists(canonicalPath) ? canonicalPath : satellitePath, capturedPath);
            Assert.AreEqual(capturedPath, captured.OriginalLocation);
            var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
            AssertComparison(result, "different", 43);
            AssertNotLoaded();
            Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
            var loaded = Assert.ContainsSingle(session.Resolver.Assemblies.Distinct()
                .Where(candidate => candidate.GetName().Name == satelliteName));
            AssertSatellite(assembly, loaded, fixture.Name);
        }

        var prefix = Path.Combine(directory, fixture.Name);
        File.WriteAllText(prefix + ".package.json", JsonSerializer.Serialize(package, ProtocolJsonContext.Default.ComparisonPackage));
        File.WriteAllText(prefix + ".satellite.path", satellitePath);
        File.WriteAllBytes(prefix + ".satellite.image", fixture.Satellite);
    }

    private async Task ChangeUnloadedSatelliteAsync(string packagePath)
    {
        var package = JsonSerializer.Deserialize(File.ReadAllText(packagePath), ProtocolJsonContext.Default.ComparisonPackage)!;
        var prefix = packagePath[..^".package.json".Length];
        var path = File.ReadAllText(prefix + ".satellite.path");
        var parent = Assert.ContainsSingle(package.Dependencies.Where(dependency => dependency.Name == package.Original.OriginalAssembly));
        Assert.IsNotNull(parent.OriginalSatelliteFiles);
        if (parent.OriginalSatelliteFiles.Count == 0)
        {
            var image = File.ReadAllBytes(prefix + ".satellite.image");
            File.WriteAllBytes(path, image);
            await AssertUnloadedSetupFailureAsync(package, "the original assembly's satellite files changed after comparison capture",
                parent.OriginalLocation!);
            Assert.AreSequenceEqual(image, File.ReadAllBytes(path));
        }
        else
        {
            var capturedPath = Assert.ContainsSingle(parent.OriginalSatelliteFiles);
            byte[] changed = [1, 2, 3];
            File.WriteAllBytes(path, changed);
            await AssertUnloadedSetupFailureAsync(package, "the original assembly file changed after comparison capture", capturedPath);
            Assert.AreSequenceEqual(changed, File.ReadAllBytes(path));
            File.Delete(path);
            await AssertUnloadedSetupFailureAsync(package, "the original assembly file is unavailable", capturedPath);
            Assert.IsFalse(File.Exists(path));
        }

        Assert.AreSequenceEqual(parent.Image, File.ReadAllBytes(parent.OriginalLocation!));
    }

    private async Task AssertUnloadedSetupFailureAsync(ComparisonPackage package, string message, string path)
    {
        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
        Assert.AreEqual("incomplete", result.Outcome);
        Assert.AreEqual("setup-failed", result.Original.Outcome, result.Original.Detail);
        Assert.IsNotNull(result.Original.Exception);
        Assert.IsNull(result.Original.Result);
        Assert.IsEmpty(result.Original.Invocations);
        Assert.Contains(message, result.Original.Detail!);
        Assert.Contains(path, result.Original.Detail!);
        AssertSide(result.Edited, 43);
    }
}
