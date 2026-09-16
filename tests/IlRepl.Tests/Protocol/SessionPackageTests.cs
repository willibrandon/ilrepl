using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IlRepl.Hosting;
using IlRepl.Protocol;
using Mono.Cecil;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Real offline packages follow NuGet range, graph, lock, and atomic replacement conventions through the host protocol.
/// </summary>
[TestClass]
public sealed class SessionPackageTests
{
    /// <summary>
    /// Supplies cancellation to package restore and real host processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Bare versions are minimums, brackets are exact, and omitted versions choose the latest stable release.
    /// </summary>
    /// <param name="range">The optional user version range.</param>
    /// <param name="version">The expected selected version.</param>
    /// <param name="value">The executable value in that version.</param>
    [TestMethod]
    [DataRow("1.0.0", "1.0.0", 10)]
    [DataRow("1.1.0", "1.5.0", 15)]
    [DataRow("[1.0.0]", "1.0.0", 10)]
    [DataRow("[1.1.0,2.0.0)", "1.5.0", 15)]
    [DataRow(null, "1.5.0", 15)]
    [DataRow("", "1.5.0", 15)]
    [DataRow("[2.0.0-beta]", "2.0.0-beta", 20)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_ResolvesNuGetVersionConventions(string? range, string version, int value)
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 10);
        fixture.WritePackage(fixture.AssemblyName, "1.5.0", 15);
        fixture.WritePackage(fixture.AssemblyName, "2.0.0-beta", 20);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        var loaded = await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName + (range is null ? "" : "," + range));
        var document = await CaptureAsync(controller);

        var reference = document.References.Single(item => item.Origin == "package");
        Assert.AreEqual(fixture.AssemblyName, reference.Request);
        Assert.AreEqual(version, reference.Version);
        Assert.AreEqual("net10.0", reference.Framework);
        Assert.IsNotNull(reference.RequestedVersion);
        Assert.IsNotNull(document.PackageLock);
        Assert.Contains(version, document.PackageLock);
        Assert.Contains(line => line.PlainText.Contains(version, StringComparison.Ordinal), loaded.Lines);
        Assert.Contains(asset => asset.Kind == "managed" && asset.Path!.StartsWith(fixture.PackageCachePath, StringComparison.Ordinal),
            reference.Assets);
        await AssertValueAsync(controller, fixture.AssemblyName, value);
    }

    /// <summary>
    /// A direct minimum can raise a shared transitive dependency while preserving graph identities and executable assets.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_UnifiesSharedDependencyUpward()
    {
        using var fixture = new SessionDependencyFixture();
        var shared = fixture.AssemblyName + "Shared";
        fixture.WritePackage(shared, "1.0.0", 10);
        fixture.WritePackage(shared, "1.5.0", 15);
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42, (shared, "1.0.0"));
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName + ",[1.0.0]");
        var before = await CaptureAsync(controller);
        var previous = before.References.Single(reference => reference.Request == shared);
        Assert.AreEqual("1.0.0", previous.Version);
        Assert.IsNull(previous.RequestedVersion);

        await SubmitAsync(controller, ".load nuget:" + shared + ",1.5.0");
        var after = await CaptureAsync(controller);

        var root = after.References.Single(reference => reference.Request == fixture.AssemblyName);
        var selected = after.References.Single(reference => reference.Request == shared);
        Assert.AreEqual(previous.Identity, selected.Identity);
        Assert.AreEqual("1.5.0", selected.Version);
        Assert.IsNotNull(selected.RequestedVersion);
        Assert.AreSequenceEqual([selected.Identity], root.Dependencies);
        await AssertValueAsync(controller, shared, 15);
    }

    /// <summary>
    /// A package upgrade drops obsolete transitives from live binding and saved documents while retaining the new executable graph.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_UpgradeRemovesObsoleteTransitiveAssembly()
    {
        using var fixture = new SessionDependencyFixture();
        var removed = fixture.AssemblyName + "Removed";
        fixture.WritePackage(removed, "1.0.0", 21);
        using (var unique = AssemblyDefinition.ReadAssembly(new MemoryStream(fixture.PackageImage(removed, "1.0.0"))))
        {
            unique.MainModule.GetType("DependencySamples.Values").Name = "RemovedValues";
            using var bytes = new MemoryStream();
            unique.Write(bytes);
            fixture.PackageAsset(removed, "1.0.0", "lib/net10.0/" + removed + ".dll", bytes.ToArray());
        }
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42, (removed, "[1.0.0]"));
        fixture.WritePackage(fixture.AssemblyName, "2.0.0", 84);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName + ",[1.0.0]");
        Assert.Contains(reference => reference.Request == removed, (await CaptureAsync(controller)).References);

        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName + ",[2.0.0]");

        var document = await CaptureAsync(controller);
        Assert.AreEqual("2.0.0", Assert.ContainsSingle(document.References).Version);
        var missing = await controller.HandleAsync("call int32 [" + removed + "]DependencySamples.RemovedValues::Read()",
            TestContext.CancellationToken);
        Assert.IsFalse(missing.Succeeded);
        Assert.Contains("not found", string.Join('\n', missing.Lines.Select(line => line.PlainText)));
        await AssertValueAsync(controller, fixture.AssemblyName, 84);
        var path = Path.Combine(fixture.DirectoryPath, "upgraded.ilrepl.json");
        await SubmitAsync(controller, ".session save \"" + path + "\" --embed");
        await SubmitAsync(controller, ".session open \"" + path + "\"");
        await AssertValueAsync(controller, fixture.AssemblyName, 84);
        await SubmitAsync(controller, ".load nuget:" + removed + ",[1.0.0]");
        await SubmitAsync(controller, ".clear");
        await SubmitAsync(controller, "call int32 [" + removed + "]DependencySamples.RemovedValues::Read()");
        AssertResult(await SubmitAsync(controller, "ret"), 21);
    }

    /// <summary>
    /// Package shared-framework requirements identify the unavailable framework and preserve the current experiment.
    /// </summary>
    /// <param name="framework">The shared framework declared by the package.</param>
    [TestMethod]
    [DataRow("Microsoft.AspNetCore.App")]
    [DataRow("Microsoft.WindowsDesktop.App")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_NamesUnavailableSharedFramework(string framework)
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42);
        fixture.RequireFramework(fixture.AssemblyName, "1.0.0", framework);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, "ldc.i4 21");

        var failed = await controller.HandleAsync(".load nuget:" + fixture.AssemblyName, TestContext.CancellationToken);

        Assert.IsFalse(failed.Succeeded);
        var diagnostic = string.Join('\n', failed.Lines.Select(line => line.PlainText));
        Assert.Contains("requires shared framework " + framework, diagnostic);
        Assert.Contains("Microsoft.NETCore.App", diagnostic);
        Assert.IsEmpty((await CaptureAsync(controller)).References);
        AssertResult(await SubmitAsync(controller, "ret"), 21);
    }

    /// <summary>
    /// The working directory's NuGet.Config source mapping excludes packages that exist in an otherwise available feed.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_HonorsPackageSourceMapping()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42);
        fixture.MapPackages("UnrelatedAllowedPackages.*");
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, "ldc.i4 21");

        var failed = await controller.HandleAsync(".load nuget:" + fixture.AssemblyName, TestContext.CancellationToken);

        Assert.IsFalse(failed.Succeeded);
        Assert.Contains("PackageSourceMapping", string.Join('\n', failed.Lines.Select(line => line.PlainText)));
        Assert.IsEmpty((await CaptureAsync(controller)).References);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.PackageCachePath, fixture.AssemblyName.ToLowerInvariant())));
        AssertResult(await SubmitAsync(controller, "ret"), 21);
    }

    /// <summary>
    /// Standard configured credentials answer a real HTTP challenge and restore executable bytes from a private feed.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_UsesNuGetConfigCredentialsForAuthenticatedFeed()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42);
        await using var feed = new SessionAuthenticatedFeed(Path.Combine(fixture.FeedPath, fixture.AssemblyName + ".1.0.0.nupkg"),
            fixture.AssemblyName, "1.0.0");
        fixture.UseAuthenticatedFeed(feed);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName + ",[1.0.0]");

        Assert.IsGreaterThanOrEqualTo(1, feed.UnauthorizedRequests);
        Assert.IsGreaterThanOrEqualTo(3, feed.AuthorizedRequests);
        Assert.AreEqual("1.0.0", (await CaptureAsync(controller)).References.Single().Version);
        await AssertValueAsync(controller, fixture.AssemblyName, 42);
    }

    /// <summary>
    /// An executable credential provider answers a real feed challenge through NuGet's plugin discovery and authentication protocol.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_UsesExecutableCredentialProviderForAuthenticatedFeed()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42);
        await using var feed = new SessionAuthenticatedFeed(Path.Combine(fixture.FeedPath, fixture.AssemblyName + ".1.0.0.nupkg"),
            fixture.AssemblyName, "1.0.0");
        fixture.UseAuthenticatedFeed(feed, credentials: false);
        var marker = Path.Combine(fixture.DirectoryPath, "provider-request.json");
        var configuration = Path.Combine(fixture.DirectoryPath, "provider.json");
        await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new
        {
            username = feed.Username, password = feed.Password, marker,
        }), TestContext.CancellationToken);
        await using var controller = await fixture.StartAsync(new Dictionary<string, string?>
        {
            ["NUGET_NETCORE_PLUGIN_PATHS"] = typeof(SessionPackageTests).Assembly.Location,
            ["NUGET_PLUGIN_PATHS"] = null,
            ["ILREPL_TEST_CREDENTIAL_PROVIDER"] = configuration,
        }, TestContext.CancellationToken);

        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName + ",[1.0.0]");

        Assert.IsGreaterThanOrEqualTo(1, feed.UnauthorizedRequests);
        Assert.IsGreaterThanOrEqualTo(3, feed.AuthorizedRequests);
        using var request = JsonDocument.Parse(await File.ReadAllTextAsync(marker, TestContext.CancellationToken));
        Assert.IsTrue(request.RootElement.GetProperty("IsNonInteractive").GetBoolean());
        Assert.StartsWith("http://127.0.0.1:", request.RootElement.GetProperty("Uri").GetString()!);
        await AssertValueAsync(controller, fixture.AssemblyName, 42);
    }

    /// <summary>
    /// Locked restore recovers the recorded graph from an empty private cache even after newer feed versions appear.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Restore_RecoversLockedGraphWithoutUpgrading()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 10);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName);
        var before = await CaptureAsync(controller);
        var previous = before.References.Single();
        fixture.WritePackage(fixture.AssemblyName, "1.5.0", 15);
        Directory.Delete(fixture.PackageCachePath, recursive: true);
        var epoch = controller.AssemblyVersion >> 32;

        await SubmitAsync(controller, ".session restore");
        var after = await CaptureAsync(controller);

        Assert.AreEqual(epoch + 1, controller.AssemblyVersion >> 32);
        Assert.AreEqual(before.PackageLock, after.PackageLock);
        Assert.AreEqual(previous.Identity, after.References.Single().Identity);
        Assert.AreEqual("1.0.0", after.References.Single().Version);
        Assert.IsTrue(Directory.Exists(Path.Combine(fixture.PackageCachePath, fixture.AssemblyName.ToLowerInvariant(), "1.0.0")));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.PackageCachePath, fixture.AssemblyName.ToLowerInvariant(), "1.5.0")));
        await AssertValueAsync(controller, fixture.AssemblyName, 10);
    }

    /// <summary>
    /// Unavailable exact versions and conflicting constraints preserve the working graph and committed methods.
    /// </summary>
    /// <param name="conflict">Whether the candidate contains a graph conflict instead of a missing package version.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_FailedRestorePreservesCurrentGraph(bool conflict)
    {
        using var fixture = new SessionDependencyFixture();
        var shared = fixture.AssemblyName + "Shared";
        fixture.WritePackage(shared, "1.0.0", 10);
        fixture.WritePackage(shared, "2.0.0", 20);
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42, (shared, "[1.0.0]"));
        var incompatible = fixture.AssemblyName + "Conflict";
        fixture.WritePackage(incompatible, "1.0.0", 0, (shared, "[2.0.0]"));
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName + ",[1.0.0]");
        await CommitConsumerAsync(controller, fixture.AssemblyName);
        var before = await CaptureAsync(controller);
        var epoch = controller.AssemblyVersion >> 32;

        var failed = await controller.HandleAsync(".load nuget:" + (conflict ? incompatible + ",[1.0.0]"
            : fixture.AssemblyName + ",[9.0.0]"), TestContext.CancellationToken);
        var after = await CaptureAsync(controller);

        Assert.IsFalse(failed.Succeeded);
        var diagnostic = string.Join('\n', failed.Lines.Select(line => line.PlainText));
        Assert.Contains("NuGet", diagnostic);
        Assert.Contains(conflict ? "Version conflict" : "Unable to find package", diagnostic);
        Assert.AreEqual(epoch, controller.AssemblyVersion >> 32);
        Assert.AreEqual(before.PackageLock, after.PackageLock);
        Assert.AreSequenceEqual(before.References.Select(reference => (reference.Identity, reference.Version)),
            after.References.Select(reference => (reference.Identity, reference.Version)));
        await SubmitAsync(controller, "call ReadSaved");
        AssertResult(await SubmitAsync(controller, "ret"), 42);
    }

    /// <summary>
    /// Clearing an executed cell does not release its activated assembly binding, while explicit reload starts fresh.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_ExecutedAssemblyRequiresReloadAfterCellCleared()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 21);
        fixture.WritePackage(fixture.AssemblyName, "2.0.0", 84);
        var path = Path.Combine(fixture.DirectoryPath, "owned dependency.dll");
        File.WriteAllBytes(path, fixture.PackageImage(fixture.AssemblyName, "1.0.0"));
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load \"" + path + "\"");
        await AssertValueAsync(controller, fixture.AssemblyName, 21);
        await SubmitAsync(controller, ".clear");
        var before = await CaptureAsync(controller);
        var epoch = controller.AssemblyVersion >> 32;
        AssemblyFileCleanup.Replace(path, fixture.PackageImage(fixture.AssemblyName, "2.0.0"));

        var failed = await controller.HandleAsync(".load \"" + path + "\"", TestContext.CancellationToken);

        Assert.IsFalse(failed.Succeeded);
        Assert.Contains("activated runtime binding " + fixture.AssemblyName,
            string.Join('\n', failed.Lines.Select(line => line.PlainText)));
        Assert.AreEqual(epoch, controller.AssemblyVersion >> 32);
        Assert.AreEqual(before.References.Single().Assets.Single().Hash,
            (await CaptureAsync(controller)).References.Single().Assets.Single().Hash);
        await SubmitAsync(controller, ".load \"" + path + "\" --reload");
        Assert.AreEqual(epoch + 1, controller.AssemblyVersion >> 32);
        await AssertValueAsync(controller, fixture.AssemblyName, 84);
    }

    /// <summary>
    /// Omitted versions become resolved minimums, so later unrelated package loads do not silently float the existing root.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_OmittedVersionBecomesStableMinimum()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 10);
        var other = fixture.AssemblyName + "Other";
        fixture.WritePackage(other, "1.0.0", 21);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName);
        var first = (await CaptureAsync(controller)).References.Single();
        Assert.AreEqual("[1.0.0, )", first.RequestedVersion);
        fixture.WritePackage(fixture.AssemblyName, "2.0.0", 20);

        await SubmitAsync(controller, ".load nuget:" + other);
        var current = await CaptureAsync(controller);
        Assert.AreEqual("1.0.0", current.References.Single(reference => reference.Identity == first.Identity).Version);
        Assert.DoesNotContain("*", current.PackageLock!);
        await SubmitAsync(controller, ".session restore");
        await AssertValueAsync(controller, fixture.AssemblyName, 10);
    }

    /// <summary>
    /// Restore targets only the active runtime and ignores unavailable dependencies declared exclusively for other platforms.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_RestoresOnlyActiveRuntimeWithPortableFallbacks()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42);
        var foreign = OperatingSystem.IsWindows() ? "linux-x64" : "win-x64";
        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "runtime.json", Encoding.UTF8.GetBytes(
            "{\"runtimes\":{\"" + foreign + "\":{\"" + fixture.AssemblyName + "\":{\"Absent.Foreign.Dependency\":\"1.0.0\"}}}}"));
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName);
        var document = await CaptureAsync(controller);
        using var graph = JsonDocument.Parse(document.PackageLock!);
        Assert.AreEquivalent(new[] { "net10.0", "net10.0/" + RuntimeInformation.RuntimeIdentifier },
            graph.RootElement.GetProperty("dependencies").EnumerateObject().Select(target => target.Name));
        await AssertValueAsync(controller, fixture.AssemblyName, 42);
    }

    /// <summary>
    /// Successful restores retain constraint warnings while direct dependency downgrades fail before graph adoption.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_ReportsWarningsAndRejectsDowngrades()
    {
        using var fixture = new SessionDependencyFixture();
        var shared = fixture.AssemblyName + "Shared";
        fixture.WritePackage(shared, "0.5.0", 5);
        fixture.WritePackage(shared, "1.0.0", 10);
        fixture.WritePackage(shared, "2.0.0", 20);
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42, (shared, "[1.0.0,2.0.0)"));
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName + ",[1.0.0]");
        var loaded = await SubmitAsync(controller, ".load nuget:" + shared + ",2.0.0");
        Assert.Contains(line => line.PlainText.Contains("NU1608", StringComparison.Ordinal), loaded.Lines);
        var before = await CaptureAsync(controller);
        var failed = await controller.HandleAsync(".load nuget:" + shared + ",0.5.0", TestContext.CancellationToken);
        Assert.IsFalse(failed.Succeeded);
        Assert.Contains(line => line.PlainText.Contains("NU1605", StringComparison.Ordinal), failed.Lines);
        Assert.AreEqual(before.PackageLock, (await CaptureAsync(controller)).PackageLock);
        await AssertValueAsync(controller, shared, 20);
    }

    /// <summary>
    /// A foreign runtime lock recovers pinned roots and transitives without resolving unrelated foreign packages or floating upward.
    /// </summary>
    /// <param name="corruptHash">Whether the recorded portable package hash was tampered with.</param>
    /// <param name="changedRequest">Whether a saved root request differs from the recorded lock.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Restore_ForeignRuntimePreservesLockedVersionsAndValidatesHashes(bool corruptHash, bool changedRequest)
    {
        using var fixture = new SessionDependencyFixture();
        var shared = fixture.AssemblyName + "Shared";
        fixture.WritePackage(shared, "1.0.0", 10);
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42, (shared, "1.0.0"));
        var foreign = OperatingSystem.IsWindows() ? "linux-arm64" : "win-arm64";
        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "runtime.json", Encoding.UTF8.GetBytes(
            "{\"runtimes\":{\"" + foreign + "\":{\"" + fixture.AssemblyName + "\":{\"Absent.Foreign.Dependency\":\"1.0.0\"}}}}"));
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName + ",*");
        var document = await CaptureAsync(controller);
        var parsed = JsonNode.Parse(document.PackageLock!)!;
        var targets = parsed["dependencies"]!.AsObject();
        var runtimeKey = "net10.0/" + RuntimeInformation.RuntimeIdentifier;
        var portable = targets["net10.0"]!;
        var archived = targets[runtimeKey]!.DeepClone();
        targets.Remove(runtimeKey);
        targets["net10.0/" + foreign] = archived;
        archived["Absent.Foreign.Dependency"] = new JsonObject
        {
            ["type"] = "Transitive", ["resolved"] = "1.0.0", ["contentHash"] = Convert.ToBase64String(new byte[64]),
        };
        if (corruptHash) portable[fixture.AssemblyName]!["contentHash"] = Convert.ToBase64String(new byte[64]);
        if (changedRequest)
        {
            document = document with { References = [.. document.References.Select(reference => reference.Request == fixture.AssemblyName
                ? reference with { RequestedVersion = "[2.0.0]" } : reference)] };
        }

        var path = Path.Combine(fixture.DirectoryPath, "foreign.ilrepl.json");
        await File.WriteAllBytesAsync(path, SessionCodec.Write(document with { PackageLock = parsed.ToJsonString() }),
            TestContext.CancellationToken);
        await SubmitAsync(controller, ".session open \"" + path + "\" --force");
        fixture.WritePackage(shared, "2.0.0", 20);
        fixture.WritePackage(fixture.AssemblyName, "2.0.0", 84, (shared, "2.0.0"));
        Directory.Delete(fixture.PackageCachePath, recursive: true);

        var restored = await controller.HandleAsync(".session restore", TestContext.CancellationToken);
        var after = await CaptureAsync(controller);

        Assert.AreEqual(!corruptHash && !changedRequest, restored.Succeeded,
            string.Join('\n', restored.Lines.Select(line => line.PlainText)));
        Assert.AreEqual("1.0.0", after.References.Single(reference => reference.Request == fixture.AssemblyName).Version);
        Assert.AreEqual(changedRequest ? "[2.0.0]" : "[*, )",
            after.References.Single(reference => reference.Request == fixture.AssemblyName).RequestedVersion);
        Assert.AreEqual("1.0.0", after.References.Single(reference => reference.Request == shared).Version);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.PackageCachePath, fixture.AssemblyName.ToLowerInvariant(), "2.0.0")));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.PackageCachePath, "absent.foreign.dependency")));
        if (corruptHash || changedRequest)
        {
            Assert.Contains(line => line.PlainText.Contains("locked restore", StringComparison.Ordinal), restored.Lines);
            Assert.AreEqual(parsed.ToJsonString(), after.PackageLock);
        }
        else
        {
            var merged = JsonNode.Parse(after.PackageLock!)!["dependencies"]!.AsObject();
            Assert.IsNotNull(merged[runtimeKey]);
            Assert.IsNotNull(merged["net10.0/" + foreign]);
            Assert.AreEqual("Transitive", merged["net10.0"]![shared]!["type"]!.GetValue<string>());
            await SubmitAsync(controller, ".session restore");
            await AssertValueAsync(controller, shared, 10);
        }
    }

    /// <summary>
    /// A newly selected platform recovers locked runtime-only versions even when the latest floating candidate cannot run.
    /// </summary>
    /// <param name="incompatibleLatest">Whether the newer candidate requires a framework the host cannot support.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Restore_ForeignRuntimeRecoversKnownRuntimeDependency(bool incompatibleLatest)
    {
        using var fixture = new SessionDependencyFixture();
        var runtimePackage = fixture.AssemblyName + "Runtime";
        fixture.WritePackage(runtimePackage, "1.0.0", 10);
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42);
        var runtime = RuntimeInformation.RuntimeIdentifier;
        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "runtime.json", Encoding.UTF8.GetBytes(
            "{\"runtimes\":{\"" + runtime + "\":{\"" + fixture.AssemblyName + "\":{\"" + runtimePackage + "\":\"*\"}}}}"));
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName);
        var document = await CaptureAsync(controller);
        Assert.AreEqual("1.0.0", document.References.Single(reference => reference.Request == runtimePackage).Version);
        var parsed = JsonNode.Parse(document.PackageLock!)!;
        var targets = parsed["dependencies"]!.AsObject();
        Assert.IsNull(targets["net10.0"]![runtimePackage]);
        var archived = targets["net10.0/" + runtime]!.DeepClone();
        targets.Remove("net10.0/" + runtime);
        targets["net10.0/" + (OperatingSystem.IsWindows() ? "linux-arm64" : "win-arm64")] = archived;
        var path = Path.Combine(fixture.DirectoryPath, "runtime-only.ilrepl.json");
        await File.WriteAllBytesAsync(path, SessionCodec.Write(document with { PackageLock = parsed.ToJsonString() }),
            TestContext.CancellationToken);
        await SubmitAsync(controller, ".session open \"" + path + "\" --force");
        fixture.WritePackage(runtimePackage, "2.0.0", 20);
        if (incompatibleLatest)
        {
            fixture.PackageAsset(runtimePackage, "2.0.0", "lib/net11.0/" + runtimePackage + ".dll",
                fixture.PackageImage(runtimePackage, "2.0.0"));
            using var archive = ZipFile.Open(Path.Combine(fixture.FeedPath, runtimePackage + ".2.0.0.nupkg"), ZipArchiveMode.Update);
            archive.GetEntry("lib/net10.0/" + runtimePackage + ".dll")!.Delete();
        }
        Directory.Delete(fixture.PackageCachePath, recursive: true);

        var restored = await controller.HandleAsync(".session restore", TestContext.CancellationToken);

        Assert.IsTrue(restored.Succeeded, string.Join('\n', restored.Lines.Select(line => line.PlainText)));
        Assert.DoesNotContain(line => line.PlainText.Contains("NU1202", StringComparison.Ordinal), restored.Lines);
        var after = await CaptureAsync(controller);
        Assert.AreEqual("1.0.0", after.References.Single(reference => reference.Request == runtimePackage).Version);
        var merged = JsonNode.Parse(after.PackageLock!)!["dependencies"]!;
        Assert.IsNull(merged["net10.0"]![runtimePackage]);
        Assert.AreEqual("1.0.0", merged["net10.0/" + runtime]![runtimePackage]!["resolved"]!.GetValue<string>());
        Assert.AreEqual("Transitive", merged["net10.0/" + runtime]![runtimePackage]!["type"]!.GetValue<string>());
        await SubmitAsync(controller, ".session restore");
        await AssertValueAsync(controller, runtimePackage, 10);
    }

    /// <summary>
    /// A platform requirement incompatible with every locked runtime version fails transactionally instead of upgrading.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Restore_ForeignRuntimeRejectsIncompatibleRuntimeRequirement()
    {
        using var fixture = new SessionDependencyFixture();
        var runtimePackage = fixture.AssemblyName + "Runtime";
        fixture.WritePackage(runtimePackage, "0.5.0", 5);
        fixture.WritePackage(runtimePackage, "1.0.0", 10);
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42);
        var runtime = RuntimeInformation.RuntimeIdentifier;
        var foreign = OperatingSystem.IsWindows() ? "linux-arm64" : "win-arm64";
        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "runtime.json", Encoding.UTF8.GetBytes(
            "{\"runtimes\":{\"" + runtime + "\":{\"" + fixture.AssemblyName + "\":{\"" + runtimePackage + "\":\"1.0.0\"}},"
            + "\"" + foreign + "\":{\"" + fixture.AssemblyName + "\":{\"" + runtimePackage + "\":\"0.5.0\"}}}}"));
        SessionDocument old;
        await using (var originalHost = await fixture.StartAsync(TestContext.CancellationToken))
        {
            await SubmitAsync(originalHost, ".load nuget:" + runtimePackage + ",[0.5.0]");
            old = await CaptureAsync(originalHost);
        }

        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName);
        var document = await CaptureAsync(controller);
        var previous = document.References.Single(reference => reference.Request == runtimePackage);
        var parsed = JsonNode.Parse(document.PackageLock!)!;
        var targets = parsed["dependencies"]!.AsObject();
        var archived = targets["net10.0/" + runtime]!.DeepClone();
        targets.Remove("net10.0/" + runtime);
        targets["net10.0/" + foreign] = archived;
        archived[runtimePackage] = JsonNode.Parse(old.PackageLock!)!["dependencies"]!["net10.0"]![runtimePackage]!.DeepClone();
        archived[runtimePackage]!["type"] = "Transitive";
        archived[runtimePackage]!.AsObject().Remove("requested");
        archived[fixture.AssemblyName]!["dependencies"]![runtimePackage] = "[0.5.0, )";
        document = document with
        {
            References = [.. document.References.Select(reference => reference.Identity == previous.Identity
                ? old.References.Single() with { Identity = previous.Identity, RequestedVersion = null } : reference)],
            Assets = [.. document.Assets, .. old.Assets], PackageLock = parsed.ToJsonString(),
        };
        var path = Path.Combine(fixture.DirectoryPath, "incompatible-runtime.ilrepl.json");
        await File.WriteAllBytesAsync(path, SessionCodec.Write(document), TestContext.CancellationToken);
        await SubmitAsync(controller, ".session open \"" + path + "\" --force");

        var restored = await controller.HandleAsync(".session restore", TestContext.CancellationToken);

        Assert.IsFalse(restored.Succeeded);
        Assert.Contains(line => line.PlainText.Contains("NU1605", StringComparison.Ordinal), restored.Lines);
        var after = await CaptureAsync(controller);
        Assert.AreEqual(parsed.ToJsonString(), after.PackageLock);
        Assert.AreEqual("0.5.0", after.References.Single(reference => reference.Request == runtimePackage).Version);
    }

    /// <summary>
    /// Recovery uses the recorded tool target rather than runtime description metadata or a later rolled-forward runtime version.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Restore_KeepsRecordedPackageFramework()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42);
        var image = fixture.PackageImage(fixture.AssemblyName, "1.0.0");
        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "lib/net9.0/" + fixture.AssemblyName + ".dll", image);
        using (var archive = ZipFile.Open(Path.Combine(fixture.FeedPath, fixture.AssemblyName + ".1.0.0.nupkg"), ZipArchiveMode.Update))
        {
            archive.GetEntry("lib/net10.0/" + fixture.AssemblyName + ".dll")!.Delete();
        }

        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName);
        var document = await CaptureAsync(controller);
        var path = Path.Combine(fixture.DirectoryPath, "framework.ilrepl.json");
        await File.WriteAllBytesAsync(path, SessionCodec.Write(document with
        {
            Runtime = document.Runtime with { Framework = "net9.0", Description = ".NET 9.0.0" },
            References = [.. document.References.Select(reference => reference with { Framework = "net9.0" })],
            PackageLock = document.PackageLock!.Replace("net10.0", "net9.0", StringComparison.Ordinal),
        }), TestContext.CancellationToken);
        await SubmitAsync(controller, ".session open \"" + path + "\" --force");

        await SubmitAsync(controller, ".session restore");

        Assert.AreEqual("net9.0", (await CaptureAsync(controller)).References.Single().Framework);
        await AssertValueAsync(controller, fixture.AssemblyName, 42);
    }

    /// <summary>
    /// Fixture hosts isolate a user configuration that demonstrably disables restore in an unisolated child on every platform.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_IsolatesUserNuGetConfiguration()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42);
        var outside = Path.Combine(fixture.DirectoryPath, "unrelated-profile");
        foreach (var directory in new[] { Path.Combine(outside, ".nuget", "NuGet"), Path.Combine(outside, "appdata", "NuGet") })
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "NuGet.Config"),
                "<configuration><disabledPackageSources><add key=\"local\" value=\"true\" /></disabledPackageSources></configuration>",
                TestContext.CancellationToken);
        }

        var environment = new Dictionary<string, string?>
        {
            ["DOTNET_CLI_HOME"] = outside, ["APPDATA"] = Path.Combine(outside, "appdata"),
            ["NUGET_PACKAGES"] = fixture.PackageCachePath,
            ["NUGET_COMMON_APPLICATION_DATA"] = Path.Combine(outside, "machine"),
        };
        async Task<IReplEngine> StartUnisolated(CancellationToken token) =>
            await HostProcessEngine.StartAsync(HostPaths.HostAssembly, fixture.DirectoryPath, environment, token);
        await using (var unisolated = new SessionController(await StartUnisolated(TestContext.CancellationToken), StartUnisolated))
        {
            var rejected = await unisolated.HandleAsync(".load nuget:" + fixture.AssemblyName, TestContext.CancellationToken);
            Assert.IsFalse(rejected.Succeeded, "The conflicting user profile must prevent the control host from restoring.");
            Assert.Contains(line => line.Kind == LineKind.Error && line.PlainText.Contains("NU1100", StringComparison.Ordinal),
                rejected.Lines);
            Assert.IsEmpty((await CaptureAsync(unisolated)).References);
        }

        await using var controller = await fixture.StartAsync(environment, TestContext.CancellationToken);
        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName);
        var reference = Assert.ContainsSingle((await CaptureAsync(controller)).References);
        Assert.AreEqual("1.0.0", reference.Version);
        Assert.StartsWith(fixture.PackageCachePath + Path.DirectorySeparatorChar, Assert.ContainsSingle(reference.Assets).Path!);
        await AssertValueAsync(controller, fixture.AssemblyName, 42);
    }

    /// <summary>
    /// Saved package assets retain package-relative locations through Save As and reopening under another machine's package root.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SaveAs_PackageLocatorsSurviveDifferentPackageRoots()
    {
        using var first = new SessionDependencyFixture();
        using var second = new SessionDependencyFixture();
        first.WritePackage(first.AssemblyName, "1.0.0", 42);
        var path = Path.Combine(first.DirectoryPath, "sessions", "example.ilrepl.json");
        var renamed = Path.Combine(first.DirectoryPath, "another", "example.ilrepl.json");
        await using (var controller = await first.StartAsync(TestContext.CancellationToken))
        {
            await SubmitAsync(controller, ".load nuget:" + first.AssemblyName);
            await SubmitAsync(controller, ".session save \"" + path + "\"");
            await SubmitAsync(controller, ".session open \"" + path + "\"");
            await SubmitAsync(controller, ".session save \"" + renamed + "\"");
        }

        foreach (var savedPath in new[] { path, renamed })
        {
            var document = SessionCodec.Read(await File.ReadAllBytesAsync(savedPath, TestContext.CancellationToken));
            var asset = document.References.Single().Assets.Single();
            Assert.AreEqual("lib/net10.0/" + first.AssemblyName + ".dll", asset.PackagePath);
            Assert.IsNull(asset.Path);
        }

        Directory.Move(first.PackageCachePath, second.PackageCachePath);
        var moved = Path.Combine(second.DirectoryPath, "example.ilrepl.json");
        File.Copy(renamed, moved);
        await using var reopened = await second.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(reopened, ".session open \"" + moved + "\"");
        var selected = (await CaptureAsync(reopened)).References.Single().Assets.Single();
        Assert.StartsWith(second.PackageCachePath + Path.DirectorySeparatorChar, selected.Path!);
        Assert.AreEqual("lib/net10.0/" + first.AssemblyName + ".dll", selected.PackagePath);
        await AssertValueAsync(reopened, first.AssemblyName, 42);
    }

    private async Task<SessionDocument> CaptureAsync(SessionController controller) =>
        (await controller.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Capture }, Editor = controller.Editor,
        }, TestContext.CancellationToken)).Document;

    private async Task<HandleReply> SubmitAsync(SessionController controller, string line)
    {
        var reply = await controller.HandleAsync(line, TestContext.CancellationToken);
        Assert.IsTrue(reply.Succeeded, line + "\n" + string.Join('\n', reply.Lines.Select(output => output.PlainText)));
        return reply;
    }

    private async Task CommitConsumerAsync(SessionController controller, string assembly)
    {
        await SubmitAsync(controller, ".method int32 ReadSaved() {");
        await SubmitAsync(controller, "call int32 [" + assembly + "]DependencySamples.Values::Read()");
        await SubmitAsync(controller, "ret");
        await SubmitAsync(controller, "}");
    }

    private async Task AssertValueAsync(SessionController controller, string assembly, int expected)
    {
        await SubmitAsync(controller, ".clear");
        await SubmitAsync(controller, "call int32 [" + assembly + "]DependencySamples.Values::Read()");
        AssertResult(await SubmitAsync(controller, "ret"), expected);
    }

    private static void AssertResult(HandleReply reply, int expected) => Assert.Contains(line => line.Kind == LineKind.Result
        && line.PlainText.Contains("= " + expected + " : int32", StringComparison.Ordinal), reply.Lines);
}
