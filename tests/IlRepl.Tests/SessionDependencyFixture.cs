using System.IO.Compression;
using System.Xml.Linq;
using IlRepl.Processes;
using IlRepl.Protocol;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests;

/// <summary>
/// Creates isolated offline packages, SDK projects, and real host processes for dependency integration tests.
/// </summary>
internal sealed class SessionDependencyFixture : IDisposable
{
    /// <summary>
    /// Creates a local-only NuGet configuration and pins fixture builds to the repository SDK.
    /// </summary>
    public SessionDependencyFixture()
    {
        Directory.CreateDirectory(FeedPath);
        Directory.CreateDirectory(Path.Combine(DirectoryPath, "user", ".nuget", "NuGet"));
        Directory.CreateDirectory(Path.Combine(DirectoryPath, "user", "appdata", "NuGet"));
        File.Copy(Path.Combine(RepoPaths.Root, "global.json"), Path.Combine(DirectoryPath, "global.json"));
        new XDocument(new XElement("configuration",
            new XElement("packageSources", new XElement("clear"),
                new XElement("add", new XAttribute("key", "local"), new XAttribute("value", FeedPath))),
            new XElement("fallbackPackageFolders", new XElement("clear")),
            new XElement("config", new XElement("add", new XAttribute("key", "globalPackagesFolder"),
                new XAttribute("value", PackageCachePath))),
            new XElement("packageSourceMapping", new XElement("clear"),
                new XElement("packageSource", new XAttribute("key", "local"),
                    new XElement("package", new XAttribute("pattern", "*"))))))
            .Save(Path.Combine(DirectoryPath, "NuGet.Config"));
    }

    /// <summary>
    /// The owned temporary workspace used as every host's working directory.
    /// </summary>
    public string DirectoryPath { get; } = Directory.CreateTempSubdirectory("ilrepl-dependencies-").FullName;

    /// <summary>
    /// The fixture's sole configured package source.
    /// </summary>
    public string FeedPath => Path.Combine(DirectoryPath, "feed");

    /// <summary>
    /// The fixture's package extraction cache, independent of user and sibling test state.
    /// </summary>
    public string PackageCachePath => Path.Combine(DirectoryPath, "packages");

    /// <summary>
    /// A unique assembly and package name that cannot collide with another test.
    /// </summary>
    public string AssemblyName { get; } = "SessionFixture" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Writes a real managed package with an executable method and optional NuGet dependencies.
    /// </summary>
    /// <param name="id">The package and assembly identity.</param>
    /// <param name="version">The package version.</param>
    /// <param name="value">The value returned by DependencySamples.Values.Read.</param>
    /// <param name="dependencies">Package IDs and requested version ranges.</param>
    public void WritePackage(string id, string version, int value, params (string Id, string Range)[] dependencies)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(id, Version.Parse(version.Split('-')[0])), id + ".dll", ModuleKind.Dll);
        var module = assembly.MainModule;
        var type = new TypeDefinition("DependencySamples", "Values",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
        module.Types.Add(type);
        var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
        type.Methods.Add(method);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, value));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        using var bytes = new MemoryStream();
        assembly.Write(bytes);
        using var archive = ZipFile.Open(Path.Combine(FeedPath, id + "." + version + ".nupkg"), ZipArchiveMode.Create);
        using (var image = archive.CreateEntry("lib/net10.0/" + id + ".dll").Open())
        {
            bytes.Position = 0;
            bytes.CopyTo(image);
        }

        var metadata = new XElement("metadata", new XElement("id", id), new XElement("version", version),
            new XElement("authors", "ilrepl tests"), new XElement("description", "Isolated offline test package"));
        if (dependencies.Length != 0)
        {
            metadata.Add(new XElement("dependencies", new XElement("group", new XAttribute("targetFramework", "net10.0"),
                dependencies.Select(dependency => new XElement("dependency", new XAttribute("id", dependency.Id),
                    new XAttribute("version", dependency.Range))))));
        }

        using var manifest = archive.CreateEntry(id + ".nuspec").Open();
        new XDocument(new XElement("package", metadata)).Save(manifest);
    }

    /// <summary>
    /// Adds a shared-framework requirement to an existing real package manifest.
    /// </summary>
    /// <param name="id">The existing package identity.</param>
    /// <param name="version">The existing package version.</param>
    /// <param name="framework">The required shared framework.</param>
    public void RequireFramework(string id, string version, string framework)
    {
        using var archive = ZipFile.Open(Path.Combine(FeedPath, id + "." + version + ".nupkg"), ZipArchiveMode.Update);
        var entry = archive.GetEntry(id + ".nuspec")!;
        XDocument document;
        using (var original = entry.Open())
        {
            document = XDocument.Load(original);
        }

        document.Root!.Element("metadata")!.Add(new XElement("frameworkReferences",
            new XElement("group", new XAttribute("targetFramework", "net10.0"),
                new XElement("frameworkReference", new XAttribute("name", framework)))));
        entry.Delete();
        using var manifest = archive.CreateEntry(id + ".nuspec").Open();
        document.Save(manifest);
    }

    /// <summary>
    /// Reads the managed implementation from one package without installing or loading it.
    /// </summary>
    /// <param name="id">The existing package identity.</param>
    /// <param name="version">The existing package version.</param>
    /// <returns>The exact implementation bytes.</returns>
    public byte[] PackageImage(string id, string version)
    {
        using var archive = ZipFile.OpenRead(Path.Combine(FeedPath, id + "." + version + ".nupkg"));
        using var source = archive.GetEntry("lib/net10.0/" + id + ".dll")!.Open();
        using var image = new MemoryStream();
        source.CopyTo(image);
        return image.ToArray();
    }

    /// <summary>
    /// Adds or replaces an actual managed, native, or culture-specific asset in the owned package.
    /// </summary>
    /// <param name="id">The existing package identity.</param>
    /// <param name="version">The existing package version.</param>
    /// <param name="path">The package-relative asset path.</param>
    /// <param name="image">The asset bytes.</param>
    public void PackageAsset(string id, string version, string path, byte[] image)
    {
        using var archive = ZipFile.Open(Path.Combine(FeedPath, id + "." + version + ".nupkg"), ZipArchiveMode.Update);
        archive.GetEntry(path)?.Delete();
        using var destination = archive.CreateEntry(path).Open();
        destination.Write(image);
    }

    /// <summary>
    /// Restricts the fixture's local source to the specified package-source mapping pattern.
    /// </summary>
    /// <param name="pattern">The only package ID pattern allowed to restore from the feed.</param>
    public void MapPackages(string pattern)
    {
        var path = Path.Combine(DirectoryPath, "NuGet.Config");
        var configuration = XDocument.Load(path);
        configuration.Root!.Element("packageSourceMapping")!.Element("packageSource")!.Element("package")!
            .SetAttributeValue("pattern", pattern);
        configuration.Save(path);
    }

    /// <summary>
    /// Configures an authenticated loopback source using standard NuGet.Config credentials.
    /// </summary>
    /// <param name="feed">The real local authenticated feed.</param>
    /// <param name="credentials">Whether to store credentials in configuration instead of using an executable provider.</param>
    public void UseAuthenticatedFeed(SessionAuthenticatedFeed feed, bool credentials = true)
    {
        var path = Path.Combine(DirectoryPath, "NuGet.Config");
        var configuration = XDocument.Load(path);
        var source = configuration.Root!.Element("packageSources")!.Element("add")!;
        source.SetAttributeValue("value", feed.Source);
        source.SetAttributeValue("allowInsecureConnections", "true");
        if (credentials) configuration.Root.Add(new XElement("packageSourceCredentials", new XElement("local",
            new XElement("add", new XAttribute("key", "Username"), new XAttribute("value", feed.Username)),
            new XElement("add", new XAttribute("key", "ClearTextPassword"), new XAttribute("value", feed.Password)))));
        configuration.Save(path);
    }

    /// <summary>
    /// Creates one SDK project using only the SDK's own target framework and configured offline feed.
    /// </summary>
    /// <returns>The project file path.</returns>
    public string WriteProject()
    {
        var directory = Path.Combine(DirectoryPath, "project with spaces");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, AssemblyName + ".csproj");
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"),
                new XElement("NuGetAudit", "false")),
            new XElement("Target", new XAttribute("Name", "RecordFixtureSdk"), new XAttribute("AfterTargets", "Build"),
                new XElement("WriteLinesToFile", new XAttribute("File", "$(MSBuildProjectDirectory)/sdk-version.txt"),
                    new XAttribute("Lines", "$(NETCoreSdkVersion)"), new XAttribute("Overwrite", "true"))))).Save(path);
        File.WriteAllText(Path.Combine(directory, "Values.cs"), """
            namespace DependencySamples;
            public static class Values
            {
                public static int Read()
                {
            #if DEBUG
                    return 21;
            #else
                    return 42;
            #endif
                }
            }
            """);
        return path;
    }

    /// <summary>
    /// Starts a real host with NuGet configuration discovery anchored to the fixture's directory.
    /// </summary>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>A replaceable host controller.</returns>
    public Task<SessionController> StartAsync(CancellationToken cancellationToken) => StartAsync(null, cancellationToken);

    /// <summary>
    /// Starts a real isolated host with additional process-scoped dependency tooling configuration.
    /// </summary>
    /// <param name="environment">Optional overrides added to the child host environment.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>A replaceable host controller.</returns>
    public async Task<SessionController> StartAsync(IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
    {
        var variables = environment?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? new Dictionary<string, string?>();
        variables["NUGET_PACKAGES"] = PackageCachePath;
        variables["DOTNET_CLI_HOME"] = Path.Combine(DirectoryPath, "user");
        variables["APPDATA"] = Path.Combine(DirectoryPath, "user", "appdata");
        variables["NUGET_COMMON_APPLICATION_DATA"] = Path.Combine(DirectoryPath, "machine");
        variables["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        variables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        async Task<IReplEngine> Start(CancellationToken token) =>
            await HostProcessEngine.StartAsync(HostPaths.HostAssembly, DirectoryPath,
                variables, token);
        return new SessionController(await Start(cancellationToken), Start);
    }

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
