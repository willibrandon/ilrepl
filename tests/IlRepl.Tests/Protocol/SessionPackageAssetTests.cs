using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Xml.Linq;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Real package assets select the current runtime, resolve satellites and native calls, and reject incompatible machine images.
/// </summary>
[TestClass]
public sealed class SessionPackageAssetTests
{
    /// <summary>
    /// Supplies cancellation for real host and restore operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Runtime-specific native assets are selected from the current RID and execute through the owning assembly context.
    /// </summary>
    /// <param name="edited">Whether execution goes through an imported editable method wrapper.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_SelectsCurrentRidAndExecutesNativeAsset(bool edited)
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 0);
        var (name, image, entryPoint) = NativeImage(fixture.AssemblyName);
        AddNativeCaller(fixture, name, entryPoint);
        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "runtimes/" + RuntimeInformation.RuntimeIdentifier + "/native/" + name, image);
        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "runtimes/" + ForeignRid() + "/native/" + name, [1, 2, 3]);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName + ",[1.0.0]");
        var document = await CaptureAsync(controller);

        var native = document.References.Single().Assets.Single(asset => asset.Kind == "native");
        Assert.AreEqual(name, native.Name);
        Assert.AreEqual(SessionCodec.Hash(image), native.Hash);
        Assert.IsNotNull(native.Path);
        Assert.AreSequenceEqual(image, File.ReadAllBytes(native.Path));
        var target = "int32 [" + fixture.AssemblyName + "]DependencySamples.Values::Read()";
        if (edited)
        {
            var edit = await SubmitAsync(controller, ".edit " + target + " as NativeRead");
            Assert.IsNotNull(edit.EditDocument);
            foreach (var line in edit.EditDocument.Source.Split('\n'))
            {
                await SubmitAsync(controller, line);
            }

            target = "int32 NativeRead()";
        }

        await SubmitAsync(controller, "call " + target);
        AssertResult(await SubmitAsync(controller, "ret"), 1);
    }

    /// <summary>
    /// Native imports in immutable originals and edited methods survive reopening and execute in isolated comparison workers.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_NativeEditSurvivesReopenAndComparison()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 0);
        var (name, image, entryPoint) = NativeImage(fixture.AssemblyName);
        AddNativeCaller(fixture, name, entryPoint);
        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "runtimes/" + RuntimeInformation.RuntimeIdentifier + "/native/" + name, image);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName + ",[1.0.0]");
        var edit = await SubmitAsync(controller,
            ".edit int32 [" + fixture.AssemblyName + "]DependencySamples.Values::Read() as NativeRead");
        Assert.IsNotNull(edit.EditDocument);
        foreach (var line in edit.EditDocument.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal).Split('\n'))
        {
            await SubmitAsync(controller, line);
        }

        fixture.WritePackage(fixture.AssemblyName, "2.0.0", 0);
        fixture.PackageAsset(fixture.AssemblyName, "2.0.0", "lib/net10.0/" + fixture.AssemblyName + ".dll",
            fixture.PackageImage(fixture.AssemblyName, "1.0.0"));
        byte[] changedNative = [.. image, 0];
        fixture.PackageAsset(fixture.AssemblyName, "2.0.0", "runtimes/" + RuntimeInformation.RuntimeIdentifier + "/native/" + name,
            changedNative);
        var replacement = ".load nuget:" + fixture.AssemblyName + ",[2.0.0]";
        var refused = await controller.HandleAsync(replacement, TestContext.CancellationToken);
        Assert.IsFalse(refused.Succeeded);
        Assert.Contains("--reload", string.Join('\n', refused.Lines.Select(line => line.PlainText)));
        await SubmitAsync(controller, replacement + " --reload");
        var retained = await CaptureAsync(controller);
        Assert.AreEqual(SessionCodec.Hash(image), retained.References.Single(reference => reference.Origin == "baseline")
            .Assets.Single(asset => asset.Kind == "native").Hash);
        Assert.AreEqual(SessionCodec.Hash(changedNative), retained.References.Single(reference => reference.Origin == "package")
            .Assets.Single(asset => asset.Kind == "native").Hash);
        var path = Path.Combine(fixture.DirectoryPath, "native.ilrepl.json");
        await SubmitAsync(controller, ".session save \"" + path + "\"");
        await SubmitAsync(controller, ".session open \"" + path + "\"");
        await SubmitAsync(controller, "call int32 NativeRead()");
        AssertResult(await SubmitAsync(controller, "ret"), 2);
        var comparison = await SubmitAsync(controller, ".compare NativeRead ()");
        Assert.IsNotNull(comparison.PendingComparison);
        var report = await controller.CompareAsync(comparison.PendingComparison.Identity, TestContext.CancellationToken);
        Assert.IsNotNull(report.Comparison);
        Assert.AreEqual("1", report.Comparison.Original.Result?.Value,
            string.Join('\n', report.Lines.Select(line => line.PlainText)));
        Assert.AreEqual("2", report.Comparison.Edited.Result?.Value);
        Assert.AreEqual("different", report.Comparison.Outcome);
    }

    /// <summary>
    /// A native-only dependency tracks its managed consumer through graph replacement and binds unused consumers to the new native cache.
    /// </summary>
    /// <param name="activated">Whether the managed consumer has already executed its native import.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Load_NativeOnlyDependencyRebindsUnusedOwnerAndRejectsActivatedOwner(bool activated)
    {
        using var fixture = new SessionDependencyFixture();
        var nativeId = fixture.AssemblyName + "Native";
        var otherId = fixture.AssemblyName + "Other";
        var (name, image, entryPoint) = NativeImage(fixture.AssemblyName);
        byte[] changedNative = [.. image, 0];
        WriteNativePackage("1.0.0", image);
        WriteNativePackage("2.0.0", changedNative);
        fixture.WritePackage(otherId, "1.0.0", 21);
        fixture.WritePackage(otherId, "2.0.0", 84);
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 0, (nativeId, "[1.0.0]"), (otherId, "[1.0.0]"));
        AddNativeCaller(fixture, name, entryPoint);
        fixture.WritePackage(fixture.AssemblyName, "2.0.0", 0, (nativeId, "[2.0.0]"), (otherId, "[2.0.0]"));
        fixture.PackageAsset(fixture.AssemblyName, "2.0.0", "lib/net10.0/" + fixture.AssemblyName + ".dll",
            fixture.PackageImage(fixture.AssemblyName, "1.0.0"));
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName + ",[1.0.0]");
        var before = await CaptureAsync(controller);
        var originalNative = Assert.ContainsSingle(before.References.Single(reference => reference.Request == nativeId).Assets);
        Assert.AreEqual("native", originalNative.Kind);
        var consumer = before.References.Single(reference => reference.Request == fixture.AssemblyName);
        Assert.Contains(before.References.Single(reference => reference.Request == nativeId).Identity, consumer.Dependencies);
        if (activated)
        {
            await SubmitAsync(controller, "call int32 [" + fixture.AssemblyName + "]DependencySamples.Values::Read()");
            AssertResult(await SubmitAsync(controller, "ret"), 1);
            await SubmitAsync(controller, ".clear");
        }

        var epoch = controller.AssemblyVersion >> 32;
        var command = ".load nuget:" + fixture.AssemblyName + ",[2.0.0]";
        var replaced = await controller.HandleAsync(command, TestContext.CancellationToken);
        if (activated)
        {
            Assert.IsFalse(replaced.Succeeded);
            Assert.Contains("activated runtime binding " + fixture.AssemblyName,
                string.Join('\n', replaced.Lines.Select(line => line.PlainText)));
            Assert.AreEqual(originalNative.Hash, (await CaptureAsync(controller)).References
                .Single(reference => reference.Request == nativeId).Assets.Single().Hash);
            Assert.AreEqual(epoch, controller.AssemblyVersion >> 32);
            await SubmitAsync(controller, command + " --reload");
        }
        else
        {
            Assert.IsTrue(replaced.Succeeded, string.Join('\n', replaced.Lines.Select(line => line.PlainText)));
            Assert.AreEqual(epoch, controller.AssemblyVersion >> 32);
            File.Delete(originalNative.Path!);
            Assert.IsFalse(File.Exists(originalNative.Path));
        }

        var current = await CaptureAsync(controller);
        Assert.AreEqual(consumer.Assets.Single().Hash,
            current.References.Single(reference => reference.Request == fixture.AssemblyName).Assets.Single().Hash);
        Assert.AreEqual(SessionCodec.Hash(changedNative),
            current.References.Single(reference => reference.Request == nativeId).Assets.Single().Hash);
        Assert.AreNotEqual(before.References.Single(reference => reference.Request == otherId).Assets.Single().Hash,
            current.References.Single(reference => reference.Request == otherId).Assets.Single().Hash);
        await SubmitAsync(controller, "call int32 [" + fixture.AssemblyName + "]DependencySamples.Values::Read()");
        AssertResult(await SubmitAsync(controller, "ret"), 1);
        await SubmitAsync(controller, ".clear");
        await SubmitAsync(controller, "call int32 [" + otherId + "]DependencySamples.Values::Read()");
        AssertResult(await SubmitAsync(controller, "ret"), 84);

        void WriteNativePackage(string version, byte[] bytes)
        {
            fixture.WritePackage(nativeId, version, 0);
            using (var archive = ZipFile.Open(Path.Combine(fixture.FeedPath, nativeId + "." + version + ".nupkg"), ZipArchiveMode.Update))
            {
                archive.GetEntry("lib/net10.0/" + nativeId + ".dll")!.Delete();
            }

            fixture.PackageAsset(nativeId, version, "runtimes/" + RuntimeInformation.RuntimeIdentifier + "/native/" + name, bytes);
        }
    }

    /// <summary>
    /// A culture-specific package satellite resolves from owned bytes without an artificial assembly reference.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_ResolvesCultureSatellite()
    {
        using var fixture = new SessionDependencyFixture();
        var (source, satellite, name) = SatelliteAssemblyFixture.Create(versioned: true, dispatch: "direct");
        fixture.WritePackage(name, "1.0.0", 0);
        fixture.PackageAsset(name, "1.0.0", "lib/net10.0/" + name + ".dll", source);
        fixture.PackageAsset(name, "1.0.0", "lib/net10.0/fr-FR/" + name + ".resources.dll", satellite);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        await SubmitAsync(controller, ".load nuget:" + name + ",[1.0.0]");
        var document = await CaptureAsync(controller);

        var selected = document.References.Single().Assets.Single(asset => asset.Kind == "satellite");
        Assert.Contains("Culture=fr-FR", selected.Name);
        Assert.AreEqual(SessionCodec.Hash(satellite), selected.Hash);
        await SubmitAsync(controller, "call int32 [" + name + "]SatelliteInspection.Owner::Read()");
        AssertResult(await SubmitAsync(controller, "ret"), 42);
    }

    /// <summary>
    /// SDK output discovery retains package native and satellite assets with both portable and explicit runtime project builds.
    /// </summary>
    /// <param name="runtimeSpecific">Whether the project explicitly selects the current runtime identifier.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Load_ProjectCapturesNativeAndSatelliteAssets(bool runtimeSpecific)
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 0);
        var (nativeName, nativeImage, entryPoint) = NativeImage(fixture.AssemblyName);
        AddNativeCaller(fixture, nativeName, entryPoint);
        fixture.PackageAsset(fixture.AssemblyName, "1.0.0",
            "runtimes/" + RuntimeInformation.RuntimeIdentifier + "/native/" + nativeName, nativeImage);
        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "runtimes/" + ForeignRid() + "/native/" + nativeName, [1, 2, 3]);
        var (source, satellite, name) = SatelliteAssemblyFixture.Create(versioned: true, dispatch: "direct");
        fixture.WritePackage(name, "1.0.0", 0);
        fixture.PackageAsset(name, "1.0.0", "lib/net10.0/" + name + ".dll", source);
        fixture.PackageAsset(name, "1.0.0", "lib/net10.0/fr-FR/" + name + ".resources.dll", satellite);
        var project = fixture.WriteProject();
        var xml = XDocument.Load(project);
        xml.Root!.Element("PropertyGroup")!.Add(new XElement("AssemblyName", fixture.AssemblyName + "Project"));
        if (runtimeSpecific)
        {
            xml.Root.Element("PropertyGroup")!.Add(new XElement("RuntimeIdentifier", RuntimeInformation.RuntimeIdentifier));
        }

        xml.Root.Add(new XElement("ItemGroup",
            new XElement("PackageReference", new XAttribute("Include", fixture.AssemblyName), new XAttribute("Version", "[1.0.0]")),
            new XElement("PackageReference", new XAttribute("Include", name), new XAttribute("Version", "[1.0.0]"))));
        xml.Save(project);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        await SubmitAsync(controller, ".load \"" + project + "\"");
        var assets = (await CaptureAsync(controller)).References.Single().Assets;

        Assert.AreEqual(SessionCodec.Hash(nativeImage), assets.Single(asset => asset.Kind == "native").Hash);
        if (!runtimeSpecific)
        {
            Assert.AreEqual(RuntimeInformation.RuntimeIdentifier, assets.Single(asset => asset.Kind == "native").Rid);
        }

        Assert.AreEqual(SessionCodec.Hash(satellite), assets.Single(asset => asset.Kind == "satellite").Hash);
        await SubmitAsync(controller, "call int32 [" + name + "]SatelliteInspection.Owner::Read()");
        AssertResult(await SubmitAsync(controller, "ret"), 42);
        await SubmitAsync(controller, ".clear");
        await SubmitAsync(controller, "call int32 [" + fixture.AssemblyName + "]DependencySamples.Values::Read()");
        AssertResult(await SubmitAsync(controller, "ret"), 1);
    }

    /// <summary>
    /// A portable wrapper loads without a foreign native library and reports the actual missing import only on explicit execution.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_MissingNativeRidFailsOnlyOnExecution()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 0);
        var (name, image, entryPoint) = NativeImage(fixture.AssemblyName);
        AddNativeCaller(fixture, name, entryPoint);
        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "runtimes/" + ForeignRid() + "/native/" + name, image);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, "ldc.i4 21");

        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName);
        Assert.AreEqual("managed", (await CaptureAsync(controller)).References.Single().Assets.Single().Kind);
        AssertResult(await SubmitAsync(controller, "ret"), 21);
        await SubmitAsync(controller, ".clear");
        await SubmitAsync(controller, "call int32 [" + fixture.AssemblyName + "]DependencySamples.Values::Read()");
        var failed = await controller.HandleAsync("ret", TestContext.CancellationToken);
        Assert.IsFalse(failed.Succeeded);
        var diagnostic = string.Join('\n', failed.Lines.Select(line => line.PlainText));
        Assert.Contains(name, diagnostic);
        Assert.Contains("DllNotFoundException", diagnostic);
    }

    /// <summary>
    /// Managed images built for another CPU fail with a named architecture diagnostic before entering the live graph.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_RejectsManagedImageForDifferentCpu()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42);
        using var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(fixture.PackageImage(fixture.AssemblyName, "1.0.0")));
        var other = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? TargetArchitecture.AMD64 : TargetArchitecture.ARM64;
        assembly.MainModule.Architecture = other;
        using var output = new MemoryStream();
        assembly.Write(output);
        var foreignImage = output.ToArray();
        var runtime = new AssemblyLoadContext("foreign-managed-" + fixture.AssemblyName, isCollectible: true);
        try
        {
            var failure = Assert.ThrowsExactly<FileLoadException>(() => runtime.LoadFromStream(new MemoryStream(foreignImage)));
            Assert.AreEqual(unchecked((int)0x80132006), failure.HResult);
        }
        finally
        {
            runtime.Unload();
        }

        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "lib/net10.0/" + fixture.AssemblyName + ".dll", foreignImage);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        var failed = await controller.HandleAsync(".load nuget:" + fixture.AssemblyName, TestContext.CancellationToken);

        Assert.IsFalse(failed.Succeeded);
        var diagnostic = string.Join('\n', failed.Lines.Select(line => line.PlainText));
        Assert.Contains("architecture", diagnostic);
        Assert.Contains(RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), diagnostic);
        Assert.IsEmpty((await CaptureAsync(controller)).References);
    }

    /// <summary>
    /// A selected native file whose CPU header disagrees with the current process is rejected without loading or executing it.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_RejectsNativeImageForDifferentCpu()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 0);
        var (name, image, entryPoint) = NativeImage(fixture.AssemblyName);
        AddNativeCaller(fixture, name, entryPoint);
        ChangeNativeCpu(image);
        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "runtimes/" + RuntimeInformation.RuntimeIdentifier + "/native/" + name, image);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        var failed = await controller.HandleAsync(".load nuget:" + fixture.AssemblyName, TestContext.CancellationToken);

        Assert.IsFalse(failed.Succeeded);
        var diagnostic = string.Join('\n', failed.Lines.Select(line => line.PlainText));
        Assert.Contains("architecture", diagnostic);
        Assert.Contains(RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), diagnostic);
        Assert.IsEmpty((await CaptureAsync(controller)).References);
    }

    /// <summary>
    /// Packages with a portable managed implementation remain usable when optional native files target another platform.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_AllowsManagedFallbackWithForeignNativeAssets()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42);
        var (name, image, entryPoint) = NativeImage(fixture.AssemblyName);
        AddNativeCaller(fixture, name, entryPoint);
        AddManagedFallback(fixture);
        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "runtimes/" + ForeignRid() + "/native/" + name, image);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName);
        var reference = (await CaptureAsync(controller)).References.Single();
        Assert.AreEqual("managed", Assert.ContainsSingle(reference.Assets).Kind);
        await SubmitAsync(controller, "call int32 [" + fixture.AssemblyName + "]DependencySamples.Values::ReadOrFallback()");
        AssertResult(await SubmitAsync(controller, "ret"), 42);
    }

    /// <summary>
    /// Explicit file loads retain sibling native imports and culture satellites without package or project metadata.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_AssemblyCapturesSiblingNativeAndCultureAssets()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 0);
        var (nativeName, nativeImage, entryPoint) = NativeImage(fixture.AssemblyName);
        AddNativeCaller(fixture, nativeName, entryPoint);
        var root = Path.Combine(fixture.DirectoryPath, fixture.AssemblyName + ".dll");
        await File.WriteAllBytesAsync(root, fixture.PackageImage(fixture.AssemblyName, "1.0.0"), TestContext.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(fixture.DirectoryPath, nativeName), nativeImage, TestContext.CancellationToken);
        var (source, satellite, name) = SatelliteAssemblyFixture.Create(versioned: true, dispatch: "direct");
        var sourcePath = Path.Combine(fixture.DirectoryPath, name + ".dll");
        await File.WriteAllBytesAsync(sourcePath, source, TestContext.CancellationToken);
        var culture = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "fr-FR")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(culture, name + ".resources.dll"), satellite, TestContext.CancellationToken);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        await SubmitAsync(controller, ".load \"" + root + "\"");
        await SubmitAsync(controller, ".load \"" + sourcePath + "\"");
        var document = await CaptureAsync(controller);
        Assert.Contains(asset => asset.Kind == "native" && asset.Hash == SessionCodec.Hash(nativeImage),
            document.References.SelectMany(reference => reference.Assets));
        Assert.Contains(asset => asset.Kind == "satellite" && asset.Hash == SessionCodec.Hash(satellite),
            document.References.SelectMany(reference => reference.Assets));
        await SubmitAsync(controller, "call int32 [" + fixture.AssemblyName + "]DependencySamples.Values::Read()");
        AssertResult(await SubmitAsync(controller, "ret"), 1);
        await SubmitAsync(controller, ".clear");
        await SubmitAsync(controller, "call int32 [" + name + "]SatelliteInspection.Owner::Read()");
        AssertResult(await SubmitAsync(controller, "ret"), 42);
    }

    /// <summary>
    /// Native-only packages still identify unsupported runtime assets before adopting an unusable dependency graph.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_RejectsNativeOnlyPackageForDifferentRuntime()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 0);
        using (var archive = ZipFile.Open(Path.Combine(fixture.FeedPath, fixture.AssemblyName + ".1.0.0.nupkg"), ZipArchiveMode.Update))
        {
            archive.GetEntry("lib/net10.0/" + fixture.AssemblyName + ".dll")!.Delete();
        }

        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "runtimes/" + ForeignRid() + "/native/only.dll", [1, 2, 3]);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        var failed = await controller.HandleAsync(".load nuget:" + fixture.AssemblyName, TestContext.CancellationToken);
        Assert.IsFalse(failed.Succeeded);
        Assert.Contains(line => line.PlainText.Contains("no compatible native assets for " + RuntimeInformation.RuntimeIdentifier,
            StringComparison.Ordinal), failed.Lines);
        Assert.IsEmpty((await CaptureAsync(controller)).References);
    }

    /// <summary>
    /// A versioned Unix import finds its lib-prefixed owned asset without adding another extension.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_ResolvesVersionedNativeLibraryNames()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 0);
        var (_, image, entryPoint) = NativeImage(fixture.AssemblyName);
        var import = fixture.AssemblyName + ".so.1";
        var name = "lib" + import;
        AddNativeCaller(fixture, import, entryPoint);
        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "runtimes/" + RuntimeInformation.RuntimeIdentifier + "/native/" + name, image);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName);

        var native = (await CaptureAsync(controller)).References.Single().Assets.Single(asset => asset.Kind == "native");
        Assert.AreEqual(name, native.Name);
        Assert.AreEqual(RuntimeInformation.RuntimeIdentifier, native.Rid);
        await SubmitAsync(controller, "call int32 [" + fixture.AssemblyName + "]DependencySamples.Values::Read()");
        AssertResult(await SubmitAsync(controller, "ret"), 1);
    }

    /// <summary>
    /// Runtime ReadyToRun images accept the operating system machine encoding used by the actual CLR.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_AcceptsRuntimeReadyToRunMachineEncoding()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 0);
        var image = await File.ReadAllBytesAsync(typeof(Enumerable).Assembly.Location, TestContext.CancellationToken);
        using (var pe = new PEReader(new MemoryStream(image)))
        {
            Assert.IsNotNull(pe.PEHeaders.CorHeader);
            Assert.IsGreaterThan(0, pe.PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size);
        }

        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "lib/net10.0/" + fixture.AssemblyName + ".dll", image);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load nuget:" + fixture.AssemblyName);
        var asset = (await CaptureAsync(controller)).References.Single().Assets.Single();
        Assert.StartsWith("System.Linq,", asset.Name);
        Assert.AreEqual(SessionCodec.Hash(image), asset.Hash);
    }

    private static (string Name, byte[] Image, string EntryPoint) NativeImage(string alias)
    {
        var directory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var name = OperatingSystem.IsWindows() ? "kernel32.dll"
            : OperatingSystem.IsMacOS() ? "libSystem.Native.dylib" : "libSystem.Native.so";
        var path = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name)
            : Path.Combine(directory, name);
        return (alias + "Native" + Path.GetExtension(name), File.ReadAllBytes(path),
            OperatingSystem.IsWindows() ? "GetCurrentProcessId" : "SystemNative_GetPid");
    }

    private static string ForeignRid() => OperatingSystem.IsWindows() ? "linux-arm64" : "win-x64";

    private static void AddNativeCaller(SessionDependencyFixture fixture, string name, string entryPoint)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(fixture.PackageImage(fixture.AssemblyName, "1.0.0")));
        var module = assembly.MainModule;
        var owner = module.GetType("DependencySamples.Values");
        var library = new ModuleReference(name);
        module.ModuleReferences.Add(library);
        var native = new MethodDefinition("NativeProcessId",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.PInvokeImpl,
            module.TypeSystem.Int32)
        {
            ImplAttributes = MethodImplAttributes.PreserveSig,
            PInvokeInfo = new PInvokeInfo(OperatingSystem.IsWindows() ? PInvokeAttributes.CallConvWinapi : PInvokeAttributes.CallConvCdecl,
                entryPoint, library),
        };
        owner.Methods.Add(native);
        var method = owner.Methods.Single(method => method.Name == "Read");
        method.Body.Instructions.Clear();
        var il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Call, module.ImportReference(typeof(Environment).GetProperty(nameof(Environment.ProcessId))!.GetMethod!));
        il.Emit(OpCodes.Call, native);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Ret);
        using var output = new MemoryStream();
        assembly.Write(output);
        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "lib/net10.0/" + fixture.AssemblyName + ".dll", output.ToArray());
    }

    private static void AddManagedFallback(SessionDependencyFixture fixture)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(fixture.PackageImage(fixture.AssemblyName, "1.0.0")));
        var module = assembly.MainModule;
        var owner = module.GetType("DependencySamples.Values");
        var fallback = new MethodDefinition("ReadOrFallback", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
        owner.Methods.Add(fallback);
        var il = fallback.Body.GetILProcessor();
        var managed = Instruction.Create(OpCodes.Ldc_I4, 42);
        var platform = OperatingSystem.IsWindows() ? nameof(OperatingSystem.IsLinux) : nameof(OperatingSystem.IsWindows);
        il.Emit(OpCodes.Call, module.ImportReference(typeof(OperatingSystem).GetMethod(platform)!));
        il.Emit(OpCodes.Brfalse, managed);
        il.Emit(OpCodes.Call, owner.Methods.Single(method => method.Name == "Read"));
        il.Emit(OpCodes.Ret);
        il.Append(managed);
        il.Emit(OpCodes.Ret);
        using var output = new MemoryStream();
        assembly.Write(output);
        fixture.PackageAsset(fixture.AssemblyName, "1.0.0", "lib/net10.0/" + fixture.AssemblyName + ".dll", output.ToArray());
    }

    private static void ChangeNativeCpu(byte[] image)
    {
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows())
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3c));
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + 4), arm ? (ushort)0x8664 : (ushort)0xaa64);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var magic = BinaryPrimitives.ReadUInt32BigEndian(image);
            var other = arm ? 0x01000007u : 0x0100000cu;
            if (magic is 0xcafebabe or 0xcafebabf)
            {
                var count = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(4));
                var size = magic == 0xcafebabf ? 32 : 20;
                for (var index = 0; index < count; index++)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(8 + index * size), other);
                }
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), other);
            }
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(18), arm ? (ushort)62 : (ushort)183);
        }
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

    private static void AssertResult(HandleReply reply, int expected) => Assert.Contains(line => line.Kind == LineKind.Result
        && line.PlainText.Contains("= " + expected + " : int32", StringComparison.Ordinal), reply.Lines,
        string.Join('\n', reply.Lines.Select(line => line.PlainText)));
}
