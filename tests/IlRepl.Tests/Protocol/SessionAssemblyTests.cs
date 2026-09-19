using System.Diagnostics;
using System.Reflection;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Explicit assembly files retain path behavior while preserving pinned dependency identities and frozen images.
/// </summary>
[TestClass]
public sealed class SessionAssemblyTests
{
    private const string LocationProbePath = "ILREPL_SESSION_ASSEMBLY_LOCATION_PROBE_PATH";

    /// <summary>
    /// Supplies cancellation for real dependency hosts.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Verified file images expose their source location while a rebuilt path cannot replace an owned original snapshot.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_PreservesLocationAndFallsBackToFrozenImageAfterFileChanges()
    {
        if (Environment.GetEnvironmentVariable(LocationProbePath) is { } mappedPath)
        {
            var original = await File.ReadAllBytesAsync(mappedPath, TestContext.CancellationToken);
            using var resolver = new TypeResolver();
            var loaded = resolver.Load(mappedPath);
            Assert.AreEqual(mappedPath, loaded.Location);
            Assert.AreEqual(10, loaded.GetType("DependencySamples.Values")!.GetMethod("Read")!.Invoke(null, null));
            Assert.IsTrue(resolver.TryGetImage(loaded, out var saved));
            Assert.AreSequenceEqual(original, saved);
            return;
        }

        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 10);
        fixture.WritePackage(fixture.AssemblyName, "2.0.0", 20);
        var image = fixture.PackageImage(fixture.AssemblyName, "1.0.0");
        var path = Path.Combine(fixture.DirectoryPath, fixture.AssemblyName + ".dll");
        await File.WriteAllBytesAsync(path, image, TestContext.CancellationToken);
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            WorkingDirectory = AppContext.BaseDirectory, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false,
        };

        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add("FullyQualifiedName~SessionAssemblyTests.Load_PreservesLocationAndFallsBackToFrozenImageAfterFileChanges");
        start.Environment[LocationProbePath] = path;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("the assembly location probe did not start");
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

        await File.WriteAllBytesAsync(path, fixture.PackageImage(fixture.AssemblyName, "2.0.0"), TestContext.CancellationToken);
        using var frozen = new TypeResolver();
        var retained = frozen.LoadImage(image, path);
        Assert.AreEqual("", retained.Location);
        Assert.AreEqual(new Version(1, 0, 0, 0), retained.GetName().Version);
        Assert.AreEqual(10, retained.GetType("DependencySamples.Values")!.GetMethod("Read")!.Invoke(null, null));
    }

    /// <summary>
    /// Loading another file with a package assembly's name cannot rewrite the package's recorded bytes or provenance.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_DifferentAssemblyCannotRewritePinnedPackageAssets()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 10);
        fixture.WritePackage(fixture.AssemblyName, "2.0.0", 20);
        var path = Path.Combine(fixture.DirectoryPath, fixture.AssemblyName + ".dll");
        await File.WriteAllBytesAsync(path, fixture.PackageImage(fixture.AssemblyName, "2.0.0"), TestContext.CancellationToken);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        var loaded = await controller.HandleAsync(".load nuget:" + fixture.AssemblyName + ",[1.0.0]", TestContext.CancellationToken);
        Assert.IsTrue(loaded.Succeeded, string.Join('\n', loaded.Lines.Select(line => line.PlainText)));
        var before = await CaptureAsync(controller);

        var failed = await controller.HandleAsync(".load \"" + path + "\"", TestContext.CancellationToken);

        Assert.IsFalse(failed.Succeeded);
        Assert.Contains(line => line.PlainText.Contains("different contents", StringComparison.Ordinal), failed.Lines);
        var after = await CaptureAsync(controller);
        Assert.AreEqual(before.PackageLock, after.PackageLock);
        var package = Assert.ContainsSingle(after.References);
        Assert.AreEqual("package", package.Origin);
        Assert.AreEqual(before.References.Single().Assets.Single().Hash, package.Assets.Single().Hash);
        Assert.AreEqual("1.0.0", package.Version);
        await controller.HandleAsync("call int32 [" + fixture.AssemblyName + "]DependencySamples.Values::Read()",
            TestContext.CancellationToken);
        var ran = await controller.HandleAsync("ret", TestContext.CancellationToken);
        Assert.IsTrue(ran.Succeeded);
        Assert.Contains(line => line.Kind == LineKind.Result
            && line.PlainText.Contains("= 10 : int32", StringComparison.Ordinal), ran.Lines);
    }

    /// <summary>
    /// Owned bindings follow the runtime's version policy without imposing an extra public-key-token equality requirement.
    /// </summary>
    /// <param name="newer">Whether the reference requires a newer unavailable assembly version.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Load_UsesRuntimeIdentityBindingRules(bool newer)
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42);
        var context = new ReferenceLoadContext();
        try
        {
            context.Register(fixture.PackageImage(fixture.AssemblyName, "1.0.0"));
            var request = new AssemblyName(fixture.AssemblyName) { Version = new Version(newer ? 2 : 1, 0, 0, 0) };
            request.SetPublicKeyToken([1, 2, 3, 4, 5, 6, 7, 8]);
            if (newer)
            {
                Assert.ThrowsExactly<FileLoadException>(() => context.Resolve(request));
            }
            else
            {
                var loaded = context.Resolve(request);
                Assert.IsNotNull(loaded);
                Assert.AreEqual(42, loaded.GetType("DependencySamples.Values")!.GetMethod("Read")!.Invoke(null, null));
            }
        }
        finally
        {
            context.Unload();
        }
    }

    private async Task<SessionDocument> CaptureAsync(SessionController controller) =>
        (await controller.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Capture }, Editor = controller.Editor,
        }, TestContext.CancellationToken)).Document;
}
