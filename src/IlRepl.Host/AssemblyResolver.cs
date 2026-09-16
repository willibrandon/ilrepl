using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// Captures a local assembly and its adjacent managed, satellite, and native dependencies without executing code.
/// </summary>
internal static class AssemblyResolver
{
    /// <summary>
    /// Resolves a local managed image and the adjacent implementation images it references.
    /// </summary>
    /// <param name="document">The current source workspace.</param>
    /// <param name="path">The requested assembly file.</param>
    /// <param name="cancellationToken">Cancels dependency reads.</param>
    /// <returns>The candidate graph with owned dependency bytes.</returns>
    internal static async Task<SessionDocument> ResolveAsync(SessionDocument document, string path, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        var assets = document.Assets.ToDictionary(asset => asset.Hash, StringComparer.Ordinal);
        var selected = new List<SessionReferenceAsset>();
        var pending = new Queue<string>();
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        pending.Enqueue(path);
        while (pending.TryDequeue(out var next))
        {
            if (!visited.Add(next))
            {
                continue;
            }

            var captured = await DependencyAsset.ReadAsync(next, "managed", assets, cancellationToken).ConfigureAwait(false);
            selected.Add(captured);
            using var pe = new PEReader(new MemoryStream(assets[captured.Hash].Image, writable: false));
            var reader = pe.GetMetadataReader();
            var assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
            foreach (var culture in Directory.EnumerateDirectories(directory))
            {
                var satellite = Path.Combine(culture, assemblyName + ".resources.dll");
                if (File.Exists(satellite) && visited.Add(satellite))
                {
                    selected.Add(await DependencyAsset.ReadAsync(satellite, "satellite", assets, cancellationToken).ConfigureAwait(false));
                }
            }

            foreach (var import in DependencyAsset.NativeImports(assets[captured.Hash].Image))
            {
                foreach (var native in Directory.EnumerateFiles(directory).Where(file =>
                    DependencyAsset.MatchesNativeName(import, Path.GetFileName(file))))
                {
                    if (visited.Add(native))
                    {
                        selected.Add(await DependencyAsset.ReadAsync(native, "native", assets, cancellationToken).ConfigureAwait(false));
                    }
                }
            }

            foreach (var handle in reader.AssemblyReferences)
            {
                var name = reader.GetString(reader.GetAssemblyReference(handle).Name);
                var dependency = Path.Combine(directory, name + ".dll");
                if (File.Exists(dependency))
                {
                    pending.Enqueue(dependency);
                }
            }
        }

        var previous = document.References.FirstOrDefault(reference => reference.Origin == "assembly"
            && Path.GetFullPath(reference.Request) == path);
        var reference = new SessionReference
        {
            Identity = previous?.Identity ?? Guid.NewGuid().ToString("N"), Origin = "assembly", Request = path, Assets = [.. selected],
        };
        var replacements = selected.Where(asset => asset.Kind is "managed" or "satellite")
            .ToDictionary(asset => Key(asset.Name), StringComparer.OrdinalIgnoreCase);
        var references = document.References.Where(item => item.Identity != reference.Identity).Select(item =>
            item.Origin != "assembly" ? item : item with
            {
                Assets = [.. item.Assets.Select(asset => asset.Kind is "managed" or "satellite"
                    && replacements.TryGetValue(Key(asset.Name), out var replacement)
                    && string.Equals(asset.Path, replacement.Path, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? replacement : asset)],
            });
        return document with
        {
            References = [.. references, reference],
            Assets = [.. assets.Values],
            Entries = previous is not null ? document.Entries : [.. document.Entries, new SessionEntry
            {
                Kind = SessionEntryKind.Reference, Reference = reference.Identity, Source = [".load " + path],
                Number = document.Cells.Select(cell => cell.Number).DefaultIfEmpty(0).Max() + 1,
            }],
        };
    }

    private static string Key(string identity)
    {
        var name = new AssemblyName(identity);
        return name.Name + "/" + name.CultureName;
    }
}
