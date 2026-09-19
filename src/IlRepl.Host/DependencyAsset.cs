using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// Reads bounded dependency images and their metadata without activating user code.
/// </summary>
internal static class DependencyAsset
{
    /// <summary>
    /// Captures a verified file with its content hash and managed identity when applicable.
    /// </summary>
    /// <param name="path">The local dependency path.</param>
    /// <param name="kind">The runtime, reference, satellite, or native asset kind.</param>
    /// <param name="assets">The destination content-addressed image table.</param>
    /// <param name="cancellationToken">Cancels reading the asset.</param>
    /// <returns>The immutable asset descriptor.</returns>
    internal static async Task<SessionReferenceAsset> ReadAsync(
        string path,
        string kind,
        Dictionary<string, SessionAsset> assets,
        CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        await using var stream = File.OpenRead(path);
        if (stream.Length > SessionCodec.FileLimit)
        {
            throw new InvalidDataException($"dependency '{path}' exceeds the 64 MiB file limit");
        }

        var image = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(image, cancellationToken).ConfigureAwait(false);
        var hash = SessionCodec.Hash(image);
        var name = Path.GetFileName(path);
        string? mvid = null;
        if (kind != "native")
        {
            using var pe = new PEReader(new MemoryStream(image, writable: false));
            if (!pe.HasMetadata)
            {
                throw new BadImageFormatException("the image has no managed metadata: " + path);
            }

            var metadata = pe.GetMetadataReader();
            DependencyCompatibility.Managed(pe.PEHeaders, path);
            var definition = metadata.GetAssemblyDefinition();
            var identity = new AssemblyName(metadata.GetString(definition.Name))
            {
                Version = definition.Version,
                CultureName = definition.Culture.IsNil ? null : metadata.GetString(definition.Culture),
            };

            if (!definition.PublicKey.IsNil)
            {
                identity.SetPublicKey(metadata.GetBlobBytes(definition.PublicKey));
            }

            name = identity.FullName;
            mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString();
        }
        else
        {
            DependencyCompatibility.Native(image, path);
        }

        assets[hash] = new SessionAsset { Hash = hash, Image = image };
        return new SessionReferenceAsset { Name = name, Hash = hash, Path = path, Mvid = mvid, Kind = kind };
    }

    /// <summary>
    /// Reads native import names without loading the managed image.
    /// </summary>
    /// <param name="image">The managed assembly bytes.</param>
    /// <returns>The module names declared by platform invocation methods.</returns>
    internal static string[] NativeImports(byte[] image)
    {
        using var pe = new PEReader(new MemoryStream(image, writable: false));
        var reader = pe.GetMetadataReader();
        return [.. reader.MethodDefinitions.Select(reader.GetMethodDefinition)
            .Where(method => method.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
            .Select(method => reader.GetString(reader.GetModuleReference(method.GetImport().Module).Name))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Matches a declared native import against the filenames the runtime probes.
    /// </summary>
    /// <param name="import">The native import name.</param>
    /// <param name="file">The available native filename.</param>
    /// <returns>Whether the file is a conventional resolution candidate.</returns>
    internal static bool MatchesNativeName(string import, string file) => new[]
    {
        import, import + ".dll", import + ".so", "lib" + import, "lib" + import + ".so", "lib" + import + ".dylib",
    }
        .Contains(file, StringComparer.OrdinalIgnoreCase);

}
