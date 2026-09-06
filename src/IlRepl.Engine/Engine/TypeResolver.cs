using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// Finds types by their IL name across the assemblies a session can see: everything loaded in
/// the process plus anything added with <see cref="Load"/>.
/// </summary>
public sealed class TypeResolver
{
    private static readonly string[] CommonNamespaces =
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
        try
        {
            assembly = File.Exists(nameOrPath)
                ? Assembly.LoadFrom(Path.GetFullPath(nameOrPath))
                : Assembly.Load(nameOrPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException or ArgumentException)
        {
            throw new ReplException($"could not load '{nameOrPath}': {ex.Message}", ex);
        }

        if (!_extra.Contains(assembly))
        {
            _extra.Insert(0, assembly);
        }

        return assembly;
    }

    /// <summary>
    /// Enumerates the assemblies searched by <see cref="Resolve"/>, most specific first.
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
                if (!a.IsDynamic && !_extra.Contains(a))
                {
                    yield return a;
                }
            }
        }
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
        var clrName = ilName.Replace('/', '+');

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
