using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Project loading evaluates real SDK language, reference, target-framework, SDK selection, and cancellation behavior offline.
/// </summary>
[TestClass]
public sealed class SessionProjectVariantsTests
{
    /// <summary>
    /// Supplies cancellation for SDK builds, child hosts, and condition-based build synchronization.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// F# and Visual Basic project outputs execute through their real compilers and capture required implementation dependencies.
    /// </summary>
    /// <param name="language">The SDK project language.</param>
    [TestMethod]
    [DataRow("FSharp")]
    [DataRow("VisualBasic")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Load_LanguageProjectsUseActualSdkCompilers(string language)
    {
        using var fixture = new SessionDependencyFixture();
        var directory = Path.Join(fixture.DirectoryPath, "language project");
        Directory.CreateDirectory(directory);
        var fsharp = language == "FSharp";
        var project = Path.Join(directory, fixture.AssemblyName + (fsharp ? ".fsproj" : ".vbproj"));
        var properties = Properties();
        properties.Add(new XElement("RootNamespace", ""));
        var document = new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"), properties));
        if (fsharp)
        {
            document.Root!.Add(new XElement("ItemGroup", new XElement("Compile", new XAttribute("Include", "Values.fs"))));
            File.WriteAllText(Path.Join(directory, "Values.fs"), """
                namespace DependencySamples
                type Values =
                    static member Read() = List.sum [20; 22]
                """);
        }
        else
        {
            File.WriteAllText(Path.Join(directory, "Values.vb"), """
                Namespace DependencySamples
                    Public Class Values
                        Public Shared Function Read() As Integer
                            Return 42
                        End Function
                    End Class
                End Namespace
                """);
        }

        document.Save(project);
        if (fsharp)
        {
            var sdk = (await RunSdkAsync(directory, ["msbuild", project, "-nologo", "-getProperty:MSBuildToolsPath"])).Trim();
            var packages = Directory.GetFiles(Path.Join(sdk, "FSharp", "library-packs"), "FSharp.Core.*.nupkg");
            Assert.IsNotEmpty(packages, "The installed SDK must supply its compiler's matching FSharp.Core package.");
            foreach (var package in packages)
            {
                File.Copy(package, Path.Join(fixture.FeedPath, Path.GetFileName(package)));
            }
        }

        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load " + Quote(project));
        var reference = Assert.ContainsSingle((await CaptureAsync(controller)).References);

        Assert.AreEqual("project", reference.Origin);
        Assert.AreEqual("net10.0", reference.Framework);
        Assert.AreEqual(project, reference.Request);
        if (fsharp)
        {
            Assert.Contains(asset => asset.Name.StartsWith("FSharp.Core,", StringComparison.Ordinal), reference.Assets);
            Assert.IsTrue(Directory.Exists(Path.Join(fixture.PackageCachePath, "fsharp.core")));
        }

        await AssertValueAsync(controller, fixture.AssemblyName, 42);
    }

    /// <summary>
    /// Evaluated custom output paths and project-reference implementations are retained instead of assuming a bin directory layout.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Load_ProjectReferenceUsesCustomEvaluatedOutputPaths()
    {
        using var fixture = new SessionDependencyFixture();
        var dependencyName = fixture.AssemblyName + "Dependency";
        var dependency = WriteCSharpProject(fixture, "dependency", dependencyName,
            "namespace ReferenceSamples; public static class Helper { public static int Read() => 42; }");
        var project = WriteCSharpProject(fixture, "consumer", fixture.AssemblyName,
            "namespace DependencySamples; public static class Values { public static int Read() => ReferenceSamples.Helper.Read(); }");
        foreach (var path in new[] { dependency, project })
        {
            var document = XDocument.Load(path);
            document.Root!.Element("PropertyGroup")!.Add(new XElement("OutputPath", "$(MSBuildProjectDirectory)/custom output/"),
                new XElement("AppendTargetFrameworkToOutputPath", "false"));
            if (path == project)
            {
                document.Root.Add(new XElement("ItemGroup", new XElement("ProjectReference", new XAttribute("Include", dependency))));
            }

            document.Save(path);
        }

        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, ".load " + Quote(project));
        var reference = Assert.ContainsSingle((await CaptureAsync(controller)).References);
        var root = reference.Assets.Single(asset => asset.Name.StartsWith(fixture.AssemblyName + ",", StringComparison.Ordinal));
        var helper = reference.Assets.Single(asset => asset.Name.StartsWith(dependencyName + ",", StringComparison.Ordinal));

        Assert.AreEqual(Path.Join(Path.GetDirectoryName(project)!, "custom output", fixture.AssemblyName + ".dll"), root.Path);
        Assert.AreEqual(Path.Join(Path.GetDirectoryName(dependency)!, "custom output", dependencyName + ".dll"), helper.Path);
        Assert.IsTrue(File.Exists(root.Path));
        Assert.IsTrue(File.Exists(helper.Path));
        await AssertValueAsync(controller, fixture.AssemblyName, 42);
    }

    /// <summary>
    /// Multi-target projects select the nearest compatible framework by default and honor either explicit available target.
    /// </summary>
    /// <param name="requested">The requested framework, or null to select the nearest target.</param>
    /// <param name="selected">The expected evaluated target framework.</param>
    /// <param name="expected">The framework-specific runtime result.</param>
    [TestMethod]
    [DataRow(null, "net10.0", 42)]
    [DataRow("net10.0", "net10.0", 42)]
    [DataRow("netstandard2.1", "netstandard2.1", 21)]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Load_MultipleFrameworksChooseNearestOrExplicitTarget(string? requested, string selected, int expected)
    {
        using var fixture = new SessionDependencyFixture();
        var project = WriteCSharpProject(fixture, "multi target", fixture.AssemblyName, """
            namespace DependencySamples
            {
                public static class Values
                {
                    public static int Read()
                    {
            #if NET10_0
                        return 42;
            #else
                        return 21;
            #endif
                    }
                }
            }
            """);
        var document = XDocument.Load(project);
        document.Root!.Element("PropertyGroup")!.Element("TargetFramework")!.Remove();
        document.Root.Element("PropertyGroup")!.Add(new XElement("TargetFrameworks", "netstandard2.1;net10.0"));
        // This small fixture uses the SDK's bundled standard contract rather than downloading another targeting pack.
        document.Root.Add(new XElement("PropertyGroup", new XAttribute("Condition", "'$(TargetFramework)' == 'netstandard2.1'"),
            new XElement("DisableImplicitFrameworkReferences", "true")),
            new XElement("ItemGroup", new XAttribute("Condition", "'$(TargetFramework)' == 'netstandard2.1'"),
                new XElement("Reference", new XAttribute("Include", "netstandard"),
                    new XElement("HintPath", "$(MSBuildToolsPath)/ref/netstandard.dll"), new XElement("Private", "false"))));
        document.Save(project);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        await SubmitAsync(controller, ".load " + Quote(project) + (requested is null ? "" : " --framework " + requested));

        var reference = Assert.ContainsSingle((await CaptureAsync(controller)).References);
        Assert.AreEqual(selected, reference.Framework);
        Assert.Contains(asset => asset.Path!.Contains(Path.DirectorySeparatorChar + selected + Path.DirectorySeparatorChar,
            StringComparison.Ordinal), reference.Assets);
        await AssertValueAsync(controller, fixture.AssemblyName, expected);
    }

    /// <summary>
    /// A project-local global.json requesting an unavailable SDK reports the SDK selection failure and preserves accepted input.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Load_UnavailableProjectSdkKeepsExperimentUsable()
    {
        using var fixture = new SessionDependencyFixture();
        var project = WriteCSharpProject(fixture, "sdk selection", fixture.AssemblyName,
            "namespace DependencySamples; public static class Values { public static int Read() => 1; }");
        var global = Path.Join(Path.GetDirectoryName(project)!, "global.json");
        File.WriteAllText(global, """{"sdk":{"version":"99.0.100","rollForward":"disable"}}""");
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, "ldc.i4 42");

        var failed = await controller.HandleAsync(".load " + Quote(project), TestContext.CancellationToken);

        Assert.IsFalse(failed.Succeeded);
        var diagnostic = string.Join('\n', failed.Lines.Select(line => line.PlainText));
        Assert.Contains("99.0.100", diagnostic);
        Assert.Contains("global.json", diagnostic);
        Assert.IsEmpty((await CaptureAsync(controller)).References);
        Assert.IsFalse(Directory.Exists(Path.Join(Path.GetDirectoryName(project)!, "bin")));
        AssertResult(await SubmitAsync(controller, "ret"), 42);
    }

    /// <summary>
    /// Cancelling a real blocked SDK build terminates its process and leaves the previous source and dependency catalog usable.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Load_CancelledBuildTerminatesSdkAndPreservesExperiment()
    {
        using var fixture = new SessionDependencyFixture();
        var project = WriteCSharpProject(fixture, "cancel build", fixture.AssemblyName,
            "namespace DependencySamples; public static class Values { public static int Read() => 1; }");
        var marker = Path.Join(fixture.DirectoryPath, "build.pid");
        var release = Path.Join(fixture.DirectoryPath, "release.build");
        AddBuildGate(project, marker, release);
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(fixture.DirectoryPath, "build.pid")
        {
            NotifyFilter = NotifyFilters.LastWrite
                | NotifyFilters.FileName | NotifyFilters.Size,
        };

        void Observe(object sender, FileSystemEventArgs args)
        {
            try
            {
                if (int.TryParse(File.ReadAllText(marker), CultureInfo.InvariantCulture, out var pid))
                {
                    started.TrySetResult(pid);
                }
            }
            catch (IOException)
            {
                // A later size or last-write event observes the completed marker.
            }
        }

        watcher.Created += Observe;
        watcher.Changed += Observe;
        watcher.EnableRaisingEvents = true;
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);
        await SubmitAsync(controller, "ldc.i4 42");
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var loading = controller.HandleAsync(".load " + Quote(project), cancelled.Token);
        try
        {
            var completed = await Task.WhenAny(started.Task, loading).WaitAsync(TestContext.CancellationToken);
            Assert.AreSame(started.Task, completed, loading.IsCompletedSuccessfully
                ? string.Join('\n', loading.Result.Lines.Select(line => line.PlainText)) : "The SDK build did not reach its gate.");
            using var process = Process.GetProcessById(await started.Task);
            await cancelled.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => loading);
            await process.WaitForExitAsync(TestContext.CancellationToken);

            Assert.IsTrue(process.HasExited);
            Assert.IsEmpty((await CaptureAsync(controller)).References);
            AssertResult(await SubmitAsync(controller, "ret"), 42);
        }
        finally
        {
            File.WriteAllText(release, "release");
            await cancelled.CancelAsync();
            try
            {
                await loading;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Build messages containing JSON-like braces cannot displace MSBuild's final structured properties and items.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Load_IgnoresBracesInBuildDiagnostics()
    {
        using var fixture = new SessionDependencyFixture();
        var project = fixture.WriteProject();
        var document = XDocument.Load(project);
        document.Root!.Add(new XElement("Target", new XAttribute("Name", "BraceDiagnostic"), new XAttribute("BeforeTargets", "Build"),
            new XElement("Warning", new XAttribute("Text", "generated { diagnostic } before evaluated JSON"))));
        document.Save(project);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        await SubmitAsync(controller, ".load " + Quote(project));

        Assert.AreEqual("project", (await CaptureAsync(controller)).References.Single().Origin);
        await AssertValueAsync(controller, fixture.AssemblyName, 21);
    }

    /// <summary>
    /// Windows platform targets compatible with the current host load without requiring WindowsDesktop shared frameworks.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Load_AcceptsWindowsPlatformTargetOnWindows()
    {
        using var fixture = new SessionDependencyFixture();
        var project = fixture.WriteProject();
        var document = XDocument.Load(project);
        document.Root!.Element("PropertyGroup")!.Element("TargetFramework")!.Value = "net10.0-windows";
        document.Save(project);
        await using var controller = await fixture.StartAsync(TestContext.CancellationToken);

        await SubmitAsync(controller, ".load " + Quote(project));

        Assert.AreEqual("net10.0-windows", (await CaptureAsync(controller)).References.Single().Framework);
        await AssertValueAsync(controller, fixture.AssemblyName, 21);
    }

    private static XElement Properties() => new("PropertyGroup", new XElement("TargetFramework", "net10.0"),
        new XElement("NuGetAudit", "false"));

    private static string WriteCSharpProject(SessionDependencyFixture fixture, string folder, string name, string source)
    {
        var directory = Path.Join(fixture.DirectoryPath, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, name + ".csproj");
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"), Properties())).Save(path);
        File.WriteAllText(Path.Join(directory, "Values.cs"), source);
        return path;
    }

    private static void AddBuildGate(string project, string marker, string release)
    {
        var document = XDocument.Load(project);
        document.Root!.Add(new XElement("UsingTask", new XAttribute("TaskName", "WaitForBuildRelease"),
            new XAttribute("TaskFactory", "RoslynCodeTaskFactory"),
            new XAttribute("AssemblyFile", "$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll"),
            new XElement("ParameterGroup", new XElement("Marker", new XAttribute("ParameterType", "System.String")),
                new XElement("Release", new XAttribute("ParameterType", "System.String"))),
            new XElement("Task", new XElement("Using", new XAttribute("Namespace", "System.IO")),
                new XElement("Using", new XAttribute("Namespace", "System.Diagnostics")),
                new XElement("Using", new XAttribute("Namespace", "System.Threading")),
                new XElement("Code", new XAttribute("Type", "Fragment"), new XAttribute("Language", "cs"), new XCData("""
                    File.WriteAllText(Marker, Process.GetCurrentProcess().Id.ToString());
                    while (!File.Exists(Release)) Thread.Yield();
                    """)))),
            new XElement("Target", new XAttribute("Name", "BlockBuildForCancellation"), new XAttribute("BeforeTargets", "CoreCompile"),
                new XElement("WaitForBuildRelease", new XAttribute("Marker", marker), new XAttribute("Release", release))));
        document.Save(project);
    }

    private async Task<string> RunSdkAsync(string directory, string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.CancellationToken);
            Assert.AreEqual(0, process.ExitCode, await error);
            return await output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private async Task<SessionDocument> CaptureAsync(SessionController controller) =>
        (await controller.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Capture },
            Editor = controller.Editor,
        }, TestContext.CancellationToken)).Document;

    private async Task<HandleReply> SubmitAsync(SessionController controller, string line)
    {
        var reply = await controller.HandleAsync(line, TestContext.CancellationToken);
        Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(output => output.PlainText)));
        return reply;
    }

    private async Task AssertValueAsync(SessionController controller, string assembly, int expected)
    {
        await SubmitAsync(controller, "call int32 [" + assembly + "]DependencySamples.Values::Read()");
        AssertResult(await SubmitAsync(controller, "ret"), expected);
    }

    private static void AssertResult(HandleReply reply, int expected) => Assert.Contains(line => line.Kind == LineKind.Result
        && line.PlainText.Contains("= " + expected + " : int32", StringComparison.Ordinal), reply.Lines);

    private static string Quote(string path) => "\"" + path + "\"";
}
