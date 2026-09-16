using System.Xml.Linq;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// SDK project loads use evaluated outputs and preserve the running experiment across failed or explicit replacement.
/// </summary>
[TestClass]
public sealed class SessionProjectTests
{
    /// <summary>
    /// Supplies cancellation to SDK builds and real host processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A project directory discovers its sole project and captures the selected configuration's executable output.
    /// </summary>
    /// <param name="configuration">The selected build configuration.</param>
    /// <param name="expected">The configuration-dependent return value.</param>
    [TestMethod]
    [DataRow("Debug", 21)]
    [DataRow("Release", 42)]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Load_DirectoryBuildsRequestedConfiguration(string configuration, int expected)
    {
        using var fixture = new SessionDependencyFixture();
        var project = fixture.WriteProject();
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        var loaded = await SubmitAsync(controller, ".load " + Quote(Path.GetDirectoryName(project)!) + " --configuration " + configuration);
        var reference = (await CaptureAsync(controller)).References.Single();

        Assert.AreEqual("project", reference.Origin);
        Assert.AreEqual(project, reference.Request);
        Assert.AreEqual(configuration, reference.Configuration);
        Assert.AreEqual("net10.0", reference.Framework);
        Assert.IsNotNull(reference.SdkVersion);
        Assert.StartsWith("10.", reference.SdkVersion);
        Assert.Contains(line => line.PlainText.Contains("loaded project " + project, StringComparison.Ordinal)
            && line.PlainText.Contains("net10.0, " + configuration + ", SDK " + reference.SdkVersion, StringComparison.Ordinal)
            && line.PlainText.Contains(" -> " + reference.Assets[0].Path, StringComparison.Ordinal), loaded.Lines);
        Assert.AreEqual(File.ReadAllText(Path.Combine(Path.GetDirectoryName(project)!, "sdk-version.txt")).Trim(), reference.SdkVersion);
        Assert.Contains(asset => asset.Kind == "managed" && asset.Path!.Contains(Path.DirectorySeparatorChar + configuration
            + Path.DirectorySeparatorChar, StringComparison.Ordinal), reference.Assets);
        await AssertValueAsync(controller, fixture.AssemblyName, expected);
    }

    /// <summary>
    /// Existing outputs can be adopted without building, while deterministic newer source timestamps reject stale binaries.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Load_NoBuildAcceptsCurrentOutputAndRejectsStaleSource()
    {
        using var fixture = new SessionDependencyFixture();
        var project = fixture.WriteProject();
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load " + Quote(project));
        var before = (await CaptureAsync(controller)).References.Single();
        var output = before.Assets.Single(asset => asset.Kind == "managed").Path!;
        var written = File.GetLastWriteTimeUtc(output);

        await SubmitAsync(controller, ".load " + Quote(project) + " --no-build");

        Assert.AreEqual(written, File.GetLastWriteTimeUtc(output));
        Assert.AreEqual(before.Assets.Single().Hash, (await CaptureAsync(controller)).References.Single().Assets.Single().Hash);
        var source = Path.Combine(Path.GetDirectoryName(project)!, "Values.cs");
        File.SetLastWriteTimeUtc(source, written.AddMinutes(1));
        var failed = await controller.HandleAsync(".load " + Quote(project) + " --no-build", TestContext.CancellationToken);

        Assert.IsFalse(failed.Succeeded);
        var diagnostic = string.Join('\n', failed.Lines.Select(line => line.PlainText));
        Assert.Contains("stale", diagnostic);
        Assert.Contains("--no-build", diagnostic);
        Assert.AreEqual(written, File.GetLastWriteTimeUtc(output));
        await AssertValueAsync(controller, fixture.AssemblyName, 21);
    }

    /// <summary>
    /// Missing outputs are reported without silently rebuilding a project requested with no-build.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Load_NoBuildRejectsMissingOutput()
    {
        using var fixture = new SessionDependencyFixture();
        var project = fixture.WriteProject();
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load " + Quote(project));
        var reference = (await CaptureAsync(controller)).References.Single();
        var output = reference.Assets.Single(asset => asset.Kind == "managed").Path!;
        File.Delete(output);

        var failed = await controller.HandleAsync(".load " + Quote(project) + " --no-build", TestContext.CancellationToken);

        Assert.IsFalse(failed.Succeeded);
        Assert.Contains("output is missing", string.Join('\n', failed.Lines.Select(line => line.PlainText)));
        Assert.IsFalse(File.Exists(output));
        await AssertValueAsync(controller, fixture.AssemblyName, 21);
    }

    /// <summary>
    /// Empty and ambiguous project directories fail before publishing references or changing accepted source.
    /// </summary>
    /// <param name="ambiguous">Whether the directory contains two projects instead of none.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_RequiresOneProjectInDirectory(bool ambiguous)
    {
        using var fixture = new SessionDependencyFixture();
        var directory = Path.Combine(fixture.DirectoryPath, "project with spaces");
        Directory.CreateDirectory(directory);
        if (ambiguous)
        {
            var project = fixture.WriteProject();
            File.Copy(project, Path.Combine(directory, "Second.csproj"));
        }

        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, "ldc.i4 42");

        var failed = await controller.HandleAsync(".load " + Quote(directory), TestContext.CancellationToken);

        Assert.IsFalse(failed.Succeeded);
        var diagnostic = string.Join('\n', failed.Lines.Select(line => line.PlainText));
        Assert.Contains(ambiguous ? "specify one project" : "no supported project", diagnostic);
        if (ambiguous)
        {
            Assert.Contains("Second.csproj", diagnostic);
            Assert.Contains(fixture.AssemblyName + ".csproj", diagnostic);
        }

        Assert.IsEmpty((await CaptureAsync(controller)).References);
        AssertResult(await SubmitAsync(controller, "ret"), 42);
    }

    /// <summary>
    /// An unavailable framework is rejected before attempting to restore an SDK targeting pack from the network.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_RejectsFrameworkAbsentFromProject()
    {
        using var fixture = new SessionDependencyFixture();
        var project = fixture.WriteProject();
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        var failed = await controller.HandleAsync(".load " + Quote(project) + " --framework net8.0", TestContext.CancellationToken);

        Assert.IsFalse(failed.Succeeded);
        var diagnostic = string.Join('\n', failed.Lines.Select(line => line.PlainText));
        Assert.Contains("net8.0", diagnostic);
        Assert.Contains("project targets: net10.0", diagnostic);
        Assert.IsEmpty((await CaptureAsync(controller)).References);
        Assert.IsFalse(Directory.Exists(Path.Combine(Path.GetDirectoryName(project)!, "bin")));
    }

    /// <summary>
    /// Shared-framework requirements receive named diagnostics before unavailable targeting packs are restored.
    /// </summary>
    /// <param name="desktop">Whether the project targets the Windows desktop shared framework.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_RejectsSharedFrameworkBeforeBuild(bool desktop)
    {
        using var fixture = new SessionDependencyFixture();
        var project = fixture.WriteProject();
        var xml = XDocument.Load(project);
        var framework = desktop ? "Microsoft.WindowsDesktop.App.WPF" : "Microsoft.AspNetCore.App";
        if (desktop)
        {
            xml.Root!.Element("PropertyGroup")!.Element("TargetFramework")!.Value = "net10.0-windows";
            xml.Root.Element("PropertyGroup")!.Add(new XElement("UseWPF", "true"), new XElement("EnableWindowsTargeting", "true"));
        }
        else
        {
            xml.Root!.Add(new XElement("ItemGroup", new XElement("FrameworkReference", new XAttribute("Include", framework))));
        }

        xml.Save(project);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        var failed = await controller.HandleAsync(".load " + Quote(project), TestContext.CancellationToken);

        Assert.IsFalse(failed.Succeeded);
        var diagnostic = string.Join('\n', failed.Lines.Select(line => line.PlainText));
        Assert.Contains("shared framework " + framework, diagnostic);
        Assert.Contains("Microsoft.NETCore.App", diagnostic);
        Assert.IsEmpty((await CaptureAsync(controller)).References);
        Assert.IsFalse(Directory.Exists(Path.Combine(Path.GetDirectoryName(project)!, "obj")));
    }

    /// <summary>
    /// Selecting a compatible target ignores shared-framework requirements conditional on an unselected target.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Load_MultipleTargetsUseSelectedFrameworkReferences()
    {
        using var fixture = new SessionDependencyFixture();
        var project = fixture.WriteProject();
        var xml = XDocument.Load(project);
        xml.Root!.Element("PropertyGroup")!.Element("TargetFramework")!.ReplaceWith(
            new XElement("TargetFrameworks", "net10.0;net10.0-windows"));
        xml.Root.Add(new XElement("ItemGroup", new XAttribute("Condition", "'$(TargetFramework)' == 'net10.0-windows'"),
            new XElement("FrameworkReference", new XAttribute("Include", "Microsoft.WindowsDesktop.App"))));
        xml.Save(project);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        await SubmitAsync(controller, ".load " + Quote(project));

        Assert.AreEqual("net10.0", (await CaptureAsync(controller)).References.Single().Framework);
        await AssertValueAsync(controller, fixture.AssemblyName, 21);
    }

    /// <summary>
    /// A rebuilt dependency can replace an unused assembly, while committed consumers require explicit fresh-host reload.
    /// </summary>
    /// <param name="committed">Whether a committed method depends on the project output.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Load_RebuildAdoptsUnusedOutputOrRequiresExplicitReload(bool committed)
    {
        using var fixture = new SessionDependencyFixture();
        var project = fixture.WriteProject();
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load " + Quote(project));
        if (committed)
        {
            await CommitConsumerAsync(controller, fixture.AssemblyName);
        }

        var before = await CaptureAsync(controller);
        var epoch = controller.AssemblyVersion >> 32;
        var source = Path.Combine(Path.GetDirectoryName(project)!, "Values.cs");
        File.WriteAllText(source, "namespace DependencySamples; public static class Values { public static int Read() => 84; }");
        var reloaded = await controller.HandleAsync(".load " + Quote(project), TestContext.CancellationToken);
        if (committed)
        {
            Assert.IsFalse(reloaded.Succeeded);
            Assert.Contains("ReadSaved", string.Join('\n', reloaded.Lines.Select(line => line.PlainText)));
            Assert.Contains("--reload", string.Join('\n', reloaded.Lines.Select(line => line.PlainText)));
            await SubmitAsync(controller, "call ReadSaved");
            AssertResult(await SubmitAsync(controller, "ret"), 21);
            await SubmitAsync(controller, ".load " + Quote(project) + " --reload");
            Assert.AreEqual(epoch + 1, controller.AssemblyVersion >> 32);
            await SubmitAsync(controller, ".clear");
            await SubmitAsync(controller, "call ReadSaved");
            AssertResult(await SubmitAsync(controller, "ret"), 84);
        }
        else
        {
            Assert.IsTrue(reloaded.Succeeded, string.Join('\n', reloaded.Lines.Select(line => line.PlainText)));
            Assert.AreEqual(epoch, controller.AssemblyVersion >> 32);
            await AssertValueAsync(controller, fixture.AssemblyName, 84);
        }

        var after = await CaptureAsync(controller);
        Assert.AreEqual(before.References.Single().Identity, after.References.Single().Identity);
        Assert.AreNotEqual(before.References.Single().Assets.Single().Hash, after.References.Single().Assets.Single().Hash);
    }

    /// <summary>
    /// A failing explicit rebuild keeps the original host, methods, dependency graph, and editor available.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Load_FailedReloadPreservesRunningExperiment()
    {
        using var fixture = new SessionDependencyFixture();
        var project = fixture.WriteProject();
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load " + Quote(project));
        await CommitConsumerAsync(controller, fixture.AssemblyName);
        controller.Editor = new SessionEditor { Lines = ["// keep this draft"], Caret = 7, Anchor = 2, Revision = 8 };
        var before = await CaptureAsync(controller);
        var epoch = controller.AssemblyVersion >> 32;
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(project)!, "Values.cs"), "this is deliberately invalid C#");

        var failed = await controller.HandleAsync(".load " + Quote(project) + " --reload", TestContext.CancellationToken);

        Assert.IsFalse(failed.Succeeded);
        Assert.Contains("build failed", string.Join('\n', failed.Lines.Select(line => line.PlainText)));
        Assert.AreEqual(epoch, controller.AssemblyVersion >> 32);
        Assert.AreSequenceEqual(before.Editor.Lines, controller.Editor.Lines);
        Assert.AreEqual(7, controller.Editor.Caret);
        Assert.AreEqual(2, controller.Editor.Anchor);
        var after = await CaptureAsync(controller);
        Assert.AreEqual(before.References.Single().Assets.Single().Hash, after.References.Single().Assets.Single().Hash);
        await SubmitAsync(controller, "call ReadSaved");
        AssertResult(await SubmitAsync(controller, "ret"), 21);
    }

    /// <summary>
    /// A shared external project can be explicitly relocated and rebuilt without retaining a phantom missing project reference.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Load_RelocatesMissingProjectAfterPortableSessionOpen()
    {
        using var fixture = new SessionDependencyFixture();
        var project = fixture.WriteProject();
        var path = Path.Combine(fixture.DirectoryPath, "sessions", "project.ilrepl.json");
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load " + Quote(project));
        await SubmitAsync(controller, ".session save " + Quote(path));
        var saved = SessionCodec.Read(await File.ReadAllBytesAsync(path, TestContext.CancellationToken));
        var original = saved.References.Single();
        Assert.AreEqual(Path.GetFileName(project), original.Request);
        Assert.IsNull(original.Assets.Single().Path);
        var identity = Guid.NewGuid().ToString("N");
        saved = saved with
        {
            References = [original with { Identity = identity }],
            Entries = [.. saved.Entries.Select(entry => entry.Reference == original.Identity
                ? entry with { Reference = identity } : entry)],
        };
        await File.WriteAllBytesAsync(path, SessionCodec.Write(saved), TestContext.CancellationToken);
        var movedDirectory = Path.Combine(fixture.DirectoryPath, "moved project");
        Directory.Move(Path.GetDirectoryName(project)!, movedDirectory);
        var moved = Path.Combine(movedDirectory, Path.GetFileName(project));
        await SubmitAsync(controller, ".session open " + Quote(path));

        var missing = await controller.HandleAsync(".session restore --build", TestContext.CancellationToken);
        Assert.IsFalse(missing.Succeeded);
        Assert.Contains(line => line.PlainText.Contains("use .load with its current path", StringComparison.Ordinal), missing.Lines);
        await SubmitAsync(controller, ".load " + Quote(moved) + " --reload");
        var adopted = Assert.ContainsSingle((await CaptureAsync(controller)).References);
        Assert.AreEqual(identity, adopted.Identity);
        Assert.AreEqual(moved, adopted.Request);
        await SubmitAsync(controller, ".session restore --build");
        await AssertValueAsync(controller, fixture.AssemblyName, 21);
    }

    private static string Quote(string path) => "\"" + path + "\"";

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
