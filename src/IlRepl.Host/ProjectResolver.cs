using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using IlRepl.Protocol;
using NuGet.Frameworks;
using NuGet.RuntimeModel;

namespace IlRepl.Host;

/// <summary>
/// Evaluates SDK projects with their selected SDK and captures their actual build outputs.
/// </summary>
internal static class ProjectResolver
{
    /// <summary>
    /// Resolves a project file or unambiguous directory into a verified candidate dependency graph.
    /// </summary>
    /// <param name="document">The current source workspace.</param>
    /// <param name="action">The project load and build options.</param>
    /// <param name="cancellationToken">Cancels the SDK process and asset reads.</param>
    /// <returns>The candidate workspace with its evaluated project outputs.</returns>
    internal static async Task<SessionDocument> ResolveAsync(SessionDocument document, SessionAction action,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(action.Path ?? throw new InvalidDataException("a project path is required"));
        if (Directory.Exists(path))
        {
            var candidates = Directory.EnumerateFiles(path).Where(file => Path.GetExtension(file) is ".csproj" or ".fsproj" or ".vbproj")
                .Order(StringComparer.Ordinal).ToArray();
            if (candidates.Length != 1)
            {
                throw new InvalidDataException(candidates.Length == 0 ? $"no supported project in '{path}'"
                    : "specify one project: " + string.Join(", ", candidates.Select(Path.GetFileName)));
            }

            path = candidates[0];
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("project does not exist: " + path + "; use .load with its current path to locate it", path);
        }

        var directory = Path.GetDirectoryName(path)!;
        var configuration = action.Configuration ?? "Debug";
        using var initial = await EvaluateAsync(directory,
            ["msbuild", path, "-nologo", "-v:quiet", "-property:Configuration=" + configuration,
                "-getProperty:TargetFramework,TargetFrameworks,UsingMicrosoftNETSdk,NETCoreSdkVersion", "-getItem:FrameworkReference"],
            cancellationToken).ConfigureAwait(false);
        var properties = initial.RootElement.GetProperty("Properties");
        var sdk = properties.GetProperty("NETCoreSdkVersion").GetString();
        if (!properties.GetProperty("UsingMicrosoftNETSdk").GetString()!.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("project loading requires an SDK-style C#, F#, or Visual Basic project");
        }

        var targets = (properties.GetProperty("TargetFrameworks").GetString() is { Length: > 0 } multiple ? multiple
            : properties.GetProperty("TargetFramework").GetString()!).Split(';', StringSplitOptions.RemoveEmptyEntries);
        var host = PackageResolver.HostFramework;
        var compatibleHost = OperatingSystem.IsWindows()
            ? NuGetFramework.ParseFolder(host.GetShortFolderName() + "-windows" + Environment.OSVersion.Version) : host;
        var framework = action.Framework ?? new FrameworkReducer().GetNearest(compatibleHost, targets.Select(NuGetFramework.ParseFolder))
            ?.GetShortFolderName() ?? targets.FirstOrDefault()
            ?? throw new InvalidDataException("project has no target framework compatible with " + host);
        if (!targets.Contains(framework, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"target '{framework}' is unavailable or incompatible; project targets: "
                + string.Join(", ", targets));
        }

        var frameworks = FrameworkReferences(initial.RootElement);
        if (!framework.Equals(properties.GetProperty("TargetFramework").GetString(), StringComparison.OrdinalIgnoreCase))
        {
            using var selectedFramework = await EvaluateAsync(directory,
                ["msbuild", path, "-nologo", "-v:quiet", "-property:Configuration=" + configuration,
                    "-property:TargetFramework=" + framework, "-getProperty:TargetFramework", "-getItem:FrameworkReference"],
                cancellationToken).ConfigureAwait(false);
            frameworks = FrameworkReferences(selectedFramework.RootElement);
        }

        if (frameworks.Length != 0)
        {
            throw new InvalidDataException("project requires shared framework " + string.Join(", ", frameworks)
                + "; this ilrepl host runs on Microsoft.NETCore.App");
        }

        if (!DefaultCompatibilityProvider.Instance.IsCompatible(compatibleHost, NuGetFramework.ParseFolder(framework)))
        {
            throw new InvalidDataException($"target '{framework}' is incompatible with {host}; project targets: "
                + string.Join(", ", targets));
        }

        var arguments = new List<string>
        {
            "msbuild", path, "-nologo", "-v:quiet", "-property:Configuration=" + configuration,
            "-property:TargetFramework=" + framework,
            "-getProperty:TargetPath,TargetFramework,MSBuildAllProjects",
            "-getItem:ReferenceCopyLocalPaths,RuntimeCopyLocalItems,NativeCopyLocalItems,ResourceCopyLocalItems,"
                + "RuntimeTargetsCopyLocalItems,Compile,FrameworkReference",
        };
        if (action.NoBuild)
        {
            arguments.Add("-target:ResolveReferences");
            arguments.Add("-property:BuildProjectReferences=false");
        }
        else
        {
            arguments.Add("-restore");
            arguments.Add("-target:Build");
        }

        using var evaluated = await EvaluateAsync(directory, arguments, cancellationToken).ConfigureAwait(false);
        var result = evaluated.RootElement;
        var items = result.GetProperty("Items");
        frameworks = items.GetProperty("FrameworkReference").EnumerateArray()
            .Select(item => item.GetProperty("Identity").GetString()!).Where(name => name != "Microsoft.NETCore.App").ToArray();
        if (frameworks.Length != 0)
        {
            throw new InvalidDataException("project requires shared framework " + string.Join(", ", frameworks)
                + "; this ilrepl host runs on Microsoft.NETCore.App");
        }

        var target = result.GetProperty("Properties").GetProperty("TargetPath").GetString()!;
        if (!File.Exists(target))
        {
            throw new InvalidDataException("project output is missing; load without --no-build: " + target);
        }

        if (action.NoBuild)
        {
            var inputs = items.GetProperty("Compile").EnumerateArray().Select(item => item.GetProperty("FullPath").GetString()!)
                .Concat(result.GetProperty("Properties").GetProperty("MSBuildAllProjects").GetString()!
                    .Split(';', StringSplitOptions.RemoveEmptyEntries)).Append(path);
            var written = File.GetLastWriteTimeUtc(target);
            var newer = inputs.FirstOrDefault(input => File.Exists(input) && File.GetLastWriteTimeUtc(input) > written);
            if (newer is not null)
            {
                throw new InvalidDataException($"project output is stale after '{newer}'; load without --no-build");
            }
        }

        var assets = document.Assets.ToDictionary(asset => asset.Hash, StringComparer.Ordinal);
        var selected = new List<SessionReferenceAsset>
        {
            await DependencyAsset.ReadAsync(target, "managed", assets, cancellationToken).ConfigureAwait(false),
        };
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        string[] copyItems = ["ReferenceCopyLocalPaths", "RuntimeCopyLocalItems", "NativeCopyLocalItems", "ResourceCopyLocalItems",
            "RuntimeTargetsCopyLocalItems"];
        var copies = copyItems.SelectMany(name => items.GetProperty(name).EnumerateArray()).ToArray();
        using var graphStream = typeof(ProjectResolver).Assembly
            .GetManifestResourceStream("IlRepl.Host.PortableRuntimeIdentifierGraph.json")!;
        var runtimes = JsonRuntimeFormat.ReadRuntimeGraph(graphStream).ExpandRuntime(RuntimeInformation.RuntimeIdentifier).ToArray();
        var runtimeCopies = new List<JsonElement>();
        foreach (var group in copies.Where(item => Metadata(item, "RuntimeIdentifier").Length != 0)
            .GroupBy(item => Metadata(item, "NuGetPackageId") + "/" + Metadata(item, "AssetType"), StringComparer.OrdinalIgnoreCase))
        {
            var runtime = runtimes.FirstOrDefault(rid => group.Any(item => Metadata(item, "RuntimeIdentifier") == rid));
            runtimeCopies.AddRange(group.Where(item => Metadata(item, "RuntimeIdentifier") == runtime));
        }

        foreach (var item in copies.Where(item => Metadata(item, "RuntimeIdentifier").Length == 0).Concat(runtimeCopies))
        {
            var file = item.GetProperty("FullPath").GetString()!;
            var extension = Path.GetExtension(file);
            if (extension is ".pdb" or ".xml" or ".json" || !File.Exists(file) || !paths.Add(file))
            {
                continue;
            }

            var kind = Metadata(item, "AssetType") == "native" ? "native"
                : file.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase) ? "satellite"
                : extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ? "managed" : "native";
            try
            {
                var asset = await DependencyAsset.ReadAsync(file, kind, assets, cancellationToken).ConfigureAwait(false);
                selected.Add(asset with { Rid = Metadata(item, "RuntimeIdentifier") is { Length: > 0 } rid ? rid : null });
            }
            catch (BadImageFormatException) when (kind == "managed")
            {
                selected.Add(await DependencyAsset.ReadAsync(file, "native", assets, cancellationToken).ConfigureAwait(false));
            }
        }

        var previous = document.References.FirstOrDefault(reference => reference.Origin == "project"
            && reference.Request.Equals(path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (previous is null)
        {
            var relocated = document.References.Where(reference => reference.Origin == "project" && !File.Exists(reference.Request)
                && Path.GetFileName(reference.Request).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)
                && reference.Assets.FirstOrDefault(asset => asset.Kind == "managed") is { } original
                && new AssemblyName(original.Name).Name == new AssemblyName(selected[0].Name).Name).ToArray();
            if (relocated.Length > 1)
            {
                throw new InvalidDataException("more than one retained project matches '" + Path.GetFileName(path)
                    + "'; place the intended project at its recorded session-relative location");
            }

            previous = relocated.SingleOrDefault();
        }

        var reference = new SessionReference
        {
            Identity = previous?.Identity ?? Guid.NewGuid().ToString("N"), Origin = "project", Request = path, Framework = framework,
            Configuration = configuration, SdkVersion = sdk,
            Assets = [.. selected.DistinctBy(asset => asset.Hash)], Frameworks = frameworks,
        };
        var entries = document.Entries.ToList();
        if (previous is null)
        {
            entries.Add(new SessionEntry { Kind = SessionEntryKind.Reference, Reference = reference.Identity,
                Number = document.Cells.Select(cell => cell.Number).DefaultIfEmpty(0).Max() + 1, Source = [".load " + path] });
        }

        return document with
        {
            References = [.. document.References.Where(item => item.Identity != reference.Identity), reference],
            Assets = [.. assets.Values], Entries = [.. entries],
        };
    }

    private static string[] FrameworkReferences(JsonElement result) => [.. result.GetProperty("Items")
        .GetProperty("FrameworkReference").EnumerateArray().Select(item => item.GetProperty("Identity").GetString()!)
        .Where(name => name != "Microsoft.NETCore.App")];

    private static async Task<JsonDocument> EvaluateAsync(string directory, IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var output = await RunAsync(directory, arguments, cancellationToken).ConfigureAwait(false);
        for (var start = output.IndexOf('{'); start >= 0; start = output.IndexOf('{', start + 1))
        {
            try
            {
                var parsed = JsonDocument.Parse(output[start..]);
                if (parsed.RootElement.TryGetProperty("Properties", out _)) return parsed;
                parsed.Dispose();
            }
            catch (JsonException)
            {
                // Build messages may contain braces before MSBuild's final structured result.
            }
        }

        throw new InvalidDataException("the SDK did not return evaluated project information: " + output.Trim());
    }

    private static string Metadata(JsonElement item, string name) => item.TryGetProperty(name, out var value)
        ? value.GetString() ?? "" : "";

    private static async Task<string> RunAsync(string directory, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        process.StartInfo.Environment["UseSharedCompilation"] = "false";

        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new InvalidDataException("loading projects requires a .NET SDK available as 'dotnet'", exception);
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await output.ConfigureAwait(false);
            var stderr = await error.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException("project evaluation or build failed: " + (stdout + Environment.NewLine + stderr).Trim());
            }

            return stdout;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }
}
