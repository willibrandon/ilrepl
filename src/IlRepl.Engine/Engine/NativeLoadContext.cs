using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Resolves the immutable native-inspection graph without rewriting assembly or method identities.
/// </summary>
internal sealed class NativeLoadContext : AssemblyLoadContext
{
    private readonly Dictionary<string, NativeAssembly> _images;
    private readonly TypeResolver _resolver;
    private readonly Dictionary<string, string> _native = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates the isolated graph with the requested assembly lifetime.
    /// </summary>
    /// <param name="target">The captured graph.</param>
    /// <param name="resolver">The cell's metadata resolver.</param>
    /// <param name="collectible">Whether generated code uses collectible storage helpers.</param>
    /// <param name="nativeDirectory">The worker-owned native asset directory.</param>
    internal NativeLoadContext(NativeTarget target, TypeResolver resolver, bool collectible, string nativeDirectory)
        : base("ilrepl.native." + Guid.NewGuid().ToString("N"), collectible)
    {
        _images = target.Assemblies.ToDictionary(image => image.Name, StringComparer.Ordinal);
        _resolver = resolver;
        foreach (var library in target.NativeLibraries)
        {
            if (string.IsNullOrWhiteSpace(library.Name) || library.Name is "." or ".."
                || library.Name.IndexOfAny(['/', '\\', ':']) >= 0 || SessionCodec.Hash(library.Image) != library.Hash)
            {
                throw new ReplException("the native inspection contains an invalid native dependency image");
            }

            Directory.CreateDirectory(nativeDirectory);
            var path = Path.Join(nativeDirectory, library.Name);
            if (!_native.TryAdd(library.Name, path))
            {
                throw new ReplException("duplicate native library: " + library.Name);
            }

            File.WriteAllBytes(path, library.Image);
        }
    }

    /// <summary>
    /// Resolves the captured native library without probing a mutable project output directory.
    /// </summary>
    /// <param name="unmanagedDllName">The imported library name.</param>
    /// <returns>The captured native library handle or the platform fallback.</returns>
    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (var name in new[]
        {
            unmanagedDllName,
            unmanagedDllName + ".dll",
            unmanagedDllName + ".so",
            "lib" + unmanagedDllName + ".so",
            "lib" + unmanagedDllName + ".dylib",
        })
        {
            if (_native.TryGetValue(name, out var path))
            {
                return LoadUnmanagedDllFromPath(path);
            }
        }

        return 0;
    }

    /// <summary>
    /// Loads an unchanged captured image, delegating shared framework identities to the default context.
    /// </summary>
    /// <param name="assemblyName">The required identity.</param>
    /// <returns>The captured assembly or the default framework binding.</returns>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (!_images.TryGetValue(assemblyName.FullName, out var captured))
        {
            var framework = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            if (File.Exists(Path.Join(framework, assemblyName.Name + ".dll")))
            {
                var shared = Default.LoadFromAssemblyName(assemblyName);
                if (Path.GetDirectoryName(shared.Location) == framework)
                {
                    return shared;
                }
            }

            throw new FileNotFoundException($"dependency '{assemblyName.FullName}' is outside the captured native graph; "
                + "load its image and prepare the inspection again");
        }

        using var stream = new MemoryStream(captured.Image, writable: false);
        var assembly = LoadFromStream(stream);
        _resolver.AddCaptured(assembly, captured.Image);
        if (Enum.TryParse<SessionAssemblyKind>(captured.Role, true, out var role))
        {
            SessionAssemblies.RegisterCaptured(assembly, captured.Image, role);
        }

        return assembly;
    }

    /// <summary>
    /// Resolves an assembly-qualified type against this captured graph.
    /// </summary>
    /// <param name="name">The complete captured type identity.</param>
    /// <returns>The loaded closed type.</returns>
    internal Type ResolveType(string name) => Type.GetType(name, LoadFromAssemblyName,
        (assembly, type, ignoreCase) => assembly?.GetType(type, throwOnError: true, ignoreCase), throwOnError: true)!;

    /// <summary>
    /// Resolves an original method token with its exact declaring-type and method arguments.
    /// </summary>
    /// <param name="identity">The captured metadata identity.</param>
    /// <returns>The original runtime method.</returns>
    internal MethodBase ResolveMethod(NativeMethodIdentity identity)
    {
        var assembly = LoadFromAssemblyName(new AssemblyName(identity.Assembly));
        var owner = assembly.GetType(identity.Type, throwOnError: true)!;
        if (identity.TypeArguments.Length != 0)
        {
            owner = owner.MakeGenericType([.. identity.TypeArguments.Select(ResolveType)]);
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.DeclaredOnly;
        var method = owner.GetMethods(flags).Cast<MethodBase>().Concat(owner.GetConstructors(flags))
            .FirstOrDefault(candidate => candidate.MetadataToken == identity.Token)
            ?? (owner.TypeInitializer?.MetadataToken == identity.Token ? owner.TypeInitializer : null)
            ?? throw new ReplException($"captured method '{identity.DisplayName}' is unavailable");
        return identity.MethodArguments.Length == 0 ? method
            : ((MethodInfo)method).MakeGenericMethod([.. identity.MethodArguments.Select(ResolveType)]);
    }
}
