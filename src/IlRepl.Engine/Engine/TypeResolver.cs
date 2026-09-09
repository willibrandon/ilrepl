using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// Finds types by their IL name across the assemblies a session can see: everything loaded in
/// the process plus anything added with <see cref="Load"/>.
/// </summary>
public sealed class TypeResolver
{
    internal static readonly string[] CommonNamespaces =
    [
        "System", "System.Text", "System.Collections.Generic", "System.Collections", "System.IO", "System.Linq",
        "System.Threading", "System.Threading.Tasks", "System.Diagnostics", "System.Globalization", "System.Numerics",
        "System.Text.RegularExpressions", "System.Reflection", "System.Runtime.CompilerServices", "System.Runtime.InteropServices",
    ];

    private static readonly string[] PreloadedAssemblies =
    [
        "System.Runtime", "System.Console", "System.Collections", "System.Linq", "System.Text.RegularExpressions",
        "System.Runtime.Numerics", "System.Diagnostics.Process", "System.Threading", "System.Memory", "System.ObjectModel",
        "System.Collections.Concurrent", "System.Collections.Immutable", "System.Runtime.InteropServices", "System.Text.Json",
    ];

    private readonly List<Assembly> _extra = [];
    private readonly Dictionary<Assembly, byte[]> _images = [];

    /// <summary>
    /// Initializes a resolver and loads the framework assemblies most cells reach for.
    /// </summary>
    public TypeResolver()
    {
        foreach (var name in PreloadedAssemblies)
        {
            try
            {
                Assembly.Load(name);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                // Optional on this runtime.
            }
        }
    }

    /// <summary>
    /// The assemblies added with <see cref="Load"/>, in load order.
    /// </summary>
    public IReadOnlyList<Assembly> LoadedAssemblies => _extra;

    /// <summary>
    /// Loads an assembly by file path or by name so its types resolve.
    /// </summary>
    /// <param name="nameOrPath">A path to a .dll, or an assembly name such as <c>System.Net.Http</c>.</param>
    /// <returns>The loaded assembly.</returns>
    /// <exception cref="ReplException">The assembly could not be loaded.</exception>
    public Assembly Load(string nameOrPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrPath);
        Assembly assembly;
        byte[]? image = null;
        try
        {
            if (File.Exists(nameOrPath))
            {
                // The bytes are read at the same moment the file is mapped, so a listing later
                // reads the image that was loaded and not whatever the path holds by then. The
                // browser's file system is in memory and its runtime loads from bytes.
                var path = Path.GetFullPath(nameOrPath);
                image = File.ReadAllBytes(path);
                if (OperatingSystem.IsBrowser())
                {
                    return LoadImage(image);
                }

                assembly = Assembly.LoadFrom(path);
            }
            else
            {
                assembly = Assembly.Load(nameOrPath);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException or ArgumentException)
        {
            throw new ReplException($"could not load '{nameOrPath}': {ex.Message}", ex);
        }

        if (!_extra.Contains(assembly))
        {
            _extra.Insert(0, assembly);
        }

        if (image is not null)
        {
            _images.TryAdd(assembly, image);
        }

        return assembly;
    }

    /// <summary>
    /// Loads an assembly from its image so its types resolve, and keeps the image for listings. This
    /// is the path a host without a file system takes, and the one tests take for assemblies they write.
    /// </summary>
    /// <param name="image">The PE image.</param>
    /// <returns>The loaded assembly.</returns>
    /// <exception cref="ReplException">The image could not be loaded.</exception>
    public Assembly LoadImage(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Assembly assembly;
        try
        {
            assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(image, writable: false));
        }
        catch (Exception ex) when (ex is FileLoadException or BadImageFormatException or ArgumentException)
        {
            throw new ReplException($"could not load the image: {ex.Message}", ex);
        }

        if (!_extra.Contains(assembly))
        {
            _extra.Insert(0, assembly);
        }

        _images.TryAdd(assembly, image);
        return assembly;
    }

    /// <summary>
    /// The PE image an assembly was loaded from with <see cref="Load"/>, when it came from a file.
    /// </summary>
    /// <param name="assembly">The assembly.</param>
    /// <param name="image">The bytes read when it was loaded.</param>
    /// <returns>True when the image is known.</returns>
    public bool TryGetImage(Assembly assembly, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? image)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return _images.TryGetValue(assembly, out image);
    }

    /// <summary>
    /// Enumerates the assemblies searched by <see cref="Resolve"/>, most specific first. Assemblies a
    /// session owns are excluded by identity.
    /// </summary>
    public IEnumerable<Assembly> Assemblies
    {
        get
        {
            foreach (var a in _extra)
            {
                yield return a;
            }

            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                // Session assemblies are reached only through the owning session's type table, so a
                // type from another session, a superseded version, or a definition dropped by .reset
                // never comes back through a name search.
                if (!a.IsDynamic && !_extra.Contains(a) && !SessionAssemblies.IsSessionAssembly(a))
                {
                    yield return a;
                }
            }
        }
    }

    /// <summary>
    /// The reflection spelling of an IL type name: nesting with <c>+</c>, and a backslash before
    /// each character reflection's own name grammar reserves, so a type called <c>Comma,Name</c>
    /// is looked up as one name and not as a name and an assembly.
    /// </summary>
    /// <param name="ilName">The IL name.</param>
    /// <returns>The name for <see cref="Assembly.GetType(string)"/>.</returns>
    public static string ReflectionName(string ilName)
    {
        ArgumentNullException.ThrowIfNull(ilName);
        var sb = new System.Text.StringBuilder(ilName.Length);
        foreach (var c in ilName)
        {
            if (c == '/')
            {
                sb.Append('+');
                continue;
            }

            if (c is ',' or '&' or '*' or '[' or ']' or '\\' or '+')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Finds a type by its IL name, with an optional <c>[assembly]</c> hint.
    /// </summary>
    /// <param name="ilName">The name as written in IL: <c>System.String</c>, <c>Outer/Inner</c>, or a bare short name.</param>
    /// <param name="assemblyHint">The assembly named in square brackets, or null.</param>
    /// <returns>The resolved type.</returns>
    /// <exception cref="ReplException">No type matched, or a bare short name was ambiguous.</exception>
    public Type Resolve(string ilName, string? assemblyHint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ilName);
        var clrName = ReflectionName(ilName);

        if (assemblyHint is not null)
        {
            var hinted = FindAssembly(assemblyHint);
            if (hinted is not null)
            {
                var t = hinted.GetType(clrName, throwOnError: false);
                if (t is not null)
                {
                    return t;
                }

                // Reference facades forward most types elsewhere; fall through to a global search.
            }
        }

        var found = Type.GetType(clrName, throwOnError: false);
        if (found is not null)
        {
            return found;
        }

        foreach (var a in Assemblies)
        {
            found = a.GetType(clrName, throwOnError: false);
            if (found is not null)
            {
                return found;
            }
        }

        if (!clrName.Contains('.'))
        {
            foreach (var ns in CommonNamespaces)
            {
                var candidate = ns + "." + clrName;
                found = Type.GetType(candidate, throwOnError: false);
                if (found is not null)
                {
                    return found;
                }

                foreach (var a in Assemblies)
                {
                    found = a.GetType(candidate, throwOnError: false);
                    if (found is not null)
                    {
                        return found;
                    }
                }
            }

            var matches = new List<Type>();
            foreach (var a in Assemblies)
            {
                Type[] types;
                try
                {
                    types = a.GetExportedTypes();
                }
                catch (Exception ex) when (ex is ReflectionTypeLoadException or FileNotFoundException or NotSupportedException)
                {
                    continue;
                }

                foreach (var t in types)
                {
                    if (t.Name == clrName && !matches.Contains(t))
                    {
                        matches.Add(t);
                    }
                }
            }

            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1)
            {
                throw new ReplException($"'{ilName}' is ambiguous: {string.Join(", ", matches.Select(m => m.FullName).Take(6))}");
            }
        }

        var hint = assemblyHint is null ? "" : $" in [{assemblyHint}]";
        throw new ReplException($"type '{ilName}' not found{hint} (load its assembly with .load)");
    }

    private Assembly? FindAssembly(string name)
    {
        foreach (var a in Assemblies)
        {
            if (string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return a;
            }
        }

        try
        {
            return Assembly.Load(name);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }
}
