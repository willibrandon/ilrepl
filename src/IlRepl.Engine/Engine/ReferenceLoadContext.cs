using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace IlRepl.Engine;

/// <summary>
/// Owns verified dependency images and resolves their managed and native references within one session.
/// </summary>
internal sealed class ReferenceLoadContext : AssemblyLoadContext
{
    private static readonly Dictionary<string, string> FrameworkPaths =
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries)
            .Where(path => Path.GetDirectoryName(path) == Path.GetDirectoryName(typeof(object).Assembly.Location))
            .DistinctBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _native = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReferenceLoadContext? _previous;
    private readonly bool _reusePreviousImages;

    /// <summary>
    /// Creates an isolated dependency context, collectible on desktop runtimes.
    /// </summary>
    /// <param name="previous">The previous context supplying unchanged assembly identities.</param>
    /// <param name="reusePreviousImages">Whether unchanged images may retain their previous dependency bindings.</param>
    public ReferenceLoadContext(ReferenceLoadContext? previous = null, bool reusePreviousImages = true)
        : base("ilrepl.references." + Guid.NewGuid().ToString("N"), !OperatingSystem.IsBrowser())
    {
        _previous = previous;
        _reusePreviousImages = reusePreviousImages;
    }

    /// <summary>
    /// Reads the assembly identity without loading or activating its image.
    /// </summary>
    /// <param name="image">The PE image.</param>
    /// <returns>The complete managed assembly identity.</returns>
    internal static AssemblyName Identity(byte[] image)
    {
        using var pe = new PEReader(new MemoryStream(image, writable: false));
        var reader = pe.GetMetadataReader();
        var definition = reader.GetAssemblyDefinition();
        var name = new AssemblyName(reader.GetString(definition.Name))
        {
            Version = definition.Version,
            CultureName = definition.Culture.IsNil ? null : reader.GetString(definition.Culture),
        };
        if (!definition.PublicKey.IsNil)
        {
            name.SetPublicKey(reader.GetBlobBytes(definition.PublicKey));
        }

        return name;
    }

    /// <summary>
    /// Registers dependency bytes before any type lookup can request their transitive references.
    /// </summary>
    /// <param name="image">The verified managed image.</param>
    /// <param name="force">Whether unchanged bytes must bind against this new dependency graph.</param>
    /// <param name="path">An optional local image path to verify again before mapping it.</param>
    internal void Register(byte[] image, bool force = false, string? path = null)
    {
        var identity = Identity(image);
        if (FrameworkPaths.TryGetValue(identity.Name!, out var frameworkPath))
        {
            var framework = AssemblyName.GetAssemblyName(frameworkPath);
            if (identity.Version > framework.Version)
            {
                throw new ReplException($"dependency '{identity}' conflicts with the shared runtime assembly '{framework}'");
            }

            return;
        }

        if (!force && _reusePreviousImages && _previous?.ContainsImage(identity, image) == true)
        {
            return;
        }

        lock (_gate)
        {
            var key = Key(identity);
            if (_images.TryGetValue(key, out var previous) && !previous.AsSpan().SequenceEqual(image))
            {
                throw new ReplException($"'{identity.Name}' is already loaded with different contents; use .load with --reload");
            }

            _images[key] = image;
            if (path is not null) _paths[key] = Path.GetFullPath(path);
        }
    }

    /// <summary>
    /// Registers a native asset from the selected runtime graph.
    /// </summary>
    /// <param name="name">The library filename.</param>
    /// <param name="path">The verified local asset path.</param>
    internal void RegisterNative(string name, string path)
    {
        lock (_gate)
        {
            _native[name] = path;
        }
    }

    /// <summary>
    /// Copies the effective owned native bindings so an edit can retain its original dependency paths.
    /// </summary>
    internal IReadOnlyDictionary<string, string> NativeLibraries
    {
        get
        {
            lock (_gate)
            {
                var libraries = _previous?.NativeLibraries.ToDictionary(pair => pair.Key, pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, path) in _native) libraries[name] = path;
                return libraries;
            }
        }
    }

    /// <summary>
    /// Resolves a native import for a generated definition using this captured dependency context.
    /// </summary>
    /// <param name="name">The native import name.</param>
    /// <returns>The loaded native handle, or zero when the context does not own the import.</returns>
    internal nint ResolveNative(string name) => LoadUnmanagedDll(name);

    /// <summary>
    /// Loads a registered image without invoking its entry point or constructors.
    /// </summary>
    /// <param name="image">The managed image.</param>
    /// <param name="path">The optional verified source location.</param>
    /// <returns>The session-owned assembly.</returns>
    internal Assembly LoadImage(byte[] image, string? path = null)
    {
        Register(image, path: path);
        return LoadFromAssemblyName(Identity(image));
    }

    /// <summary>
    /// Resolves an exact owned dependency for a generated definition context.
    /// </summary>
    /// <param name="name">The requested assembly identity.</param>
    /// <returns>The owned assembly, or null for a shared framework dependency.</returns>
    internal Assembly? Resolve(AssemblyName name)
    {
        lock (_gate)
        {
            return _images.ContainsKey(Key(name)) ? LoadFromAssemblyName(name) : _previous?.Resolve(name);
        }
    }

    /// <summary>
    /// Copies an already loaded owned binding without loading images or invoking resolution callbacks.
    /// </summary>
    /// <param name="name">The requested assembly identity.</param>
    /// <returns>The existing owned assembly, or null when no binding is loaded.</returns>
    internal Assembly? FindLoaded(AssemblyName name)
    {
        lock (_gate)
        {
            return _images.ContainsKey(Key(name))
                ? Assemblies.FirstOrDefault(assembly => Key(assembly.GetName()).Equals(Key(name), StringComparison.OrdinalIgnoreCase))
                : _previous?.FindLoaded(name);
        }
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        lock (_gate)
        {
            if (!_images.TryGetValue(Key(assemblyName), out var image))
            {
                if (_previous?.Resolve(assemblyName) is { } previous) return previous;
                foreach (var directory in _paths.Values.Select(Path.GetDirectoryName).Distinct().ToArray())
                {
                    var candidate = Path.Combine(directory!, assemblyName.CultureName ?? "", assemblyName.Name + ".dll");
                    if (File.Exists(candidate))
                    {
                        var bytes = File.ReadAllBytes(candidate);
                        Register(bytes, path: candidate);
                        return LoadFromAssemblyPath(candidate);
                    }
                }

                return null;
            }

            var available = Identity(image);
            if (assemblyName.Version > available.Version)
            {
                throw new FileLoadException($"dependency '{assemblyName}' cannot bind to '{available}'");
            }

            if (!OperatingSystem.IsBrowser() && _paths.TryGetValue(Key(assemblyName), out var path) && File.Exists(path))
            {
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (source.Length == image.Length)
                {
                    var current = new byte[image.Length];
                    source.ReadExactly(current);
                    if (current.AsSpan().SequenceEqual(image)) return LoadFromAssemblyPath(path);
                }
            }

            return LoadFromStream(new MemoryStream(image, writable: false));
        }
    }

    /// <inheritdoc />
    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        lock (_gate)
        {
            foreach (var name in new[] { unmanagedDllName, unmanagedDllName + ".dll", unmanagedDllName + ".so",
                "lib" + unmanagedDllName, "lib" + unmanagedDllName + ".so", "lib" + unmanagedDllName + ".dylib" })
            {
                if (_native.TryGetValue(name, out var path))
                {
                    return LoadUnmanagedDllFromPath(path);
                }
            }

            foreach (var directory in _paths.Values.Select(Path.GetDirectoryName).Distinct())
            {
                foreach (var name in new[] { unmanagedDllName, unmanagedDllName + ".dll", unmanagedDllName + ".so",
                    "lib" + unmanagedDllName, "lib" + unmanagedDllName + ".so", "lib" + unmanagedDllName + ".dylib" })
                {
                    var candidate = Path.Combine(directory!, name);
                    if (File.Exists(candidate)) return LoadUnmanagedDllFromPath(candidate);
                }
            }

            return _previous?.LoadUnmanagedDll(unmanagedDllName) ?? 0;
        }
    }

    private static string Key(AssemblyName name) => name.Name + "/" + name.CultureName;

    private bool ContainsImage(AssemblyName name, byte[] image)
    {
        lock (_gate)
        {
            return _images.TryGetValue(Key(name), out var previous) ? previous.AsSpan().SequenceEqual(image)
                : _previous?.ContainsImage(name, image) == true;
        }
    }
}
