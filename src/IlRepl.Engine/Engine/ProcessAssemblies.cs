using System.Reflection;
using System.Runtime.Loader;

namespace IlRepl.Engine;

/// <summary>
/// Caches searchable process assemblies without retaining collectible contexts during repeated name lookup.
/// </summary>
/// <remarks>
/// The assemblies of the process a name search may look in: the ones that are not dynamic and
/// sit in a load context that cannot unload. The runtime's own list is walked once, and again
/// never again. Later searchable assemblies are appended from the runtime's load notification.
/// Each walk takes a passing reference on every collectible loader allocator, and an assembly
/// being unloaded stays alive for as long as any thread keeps walking. An assembly in a
/// collectible context the session did not create is left out for the same reason a cell could
/// never bind it: it can be gone at any moment.
/// </remarks>
internal static class ProcessAssemblies
{
    private static readonly Lock Gate = new();
    private static Assembly[] s_current = [];
    private static bool s_initialized;
    private static readonly Lock ChangeGate = new();
    private static TaskCompletionSource<long> s_changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static long s_version;

    static ProcessAssemblies()
    {
        AppDomain.CurrentDomain.AssemblyLoad += (_, args) => Loaded(args.LoadedAssembly);
    }

    private static void Loaded(Assembly assembly)
    {
        if (!IsSearchable(assembly))
        {
            return;
        }

        lock (Gate)
        {
            if (s_initialized && !s_current.Contains(assembly))
            {
                Volatile.Write(ref s_current, [.. s_current, assembly]);
            }
        }

        lock (ChangeGate)
        {
            var previous = s_changed;
            s_changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult(Interlocked.Increment(ref s_version));
        }
    }

    /// <summary>
    /// The searchable assembly-load version, readable without enumerating runtime assemblies.
    /// </summary>
    public static long Version => Interlocked.Read(ref s_version);

    /// <summary>
    /// Waits for a searchable assembly load without retaining an engine or polling the runtime.
    /// </summary>
    /// <param name="version">The last observed version.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The changed version.</returns>
    public static Task<long> WaitForChangeAsync(long version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (ChangeGate)
        {
            return version != Version ? Task.FromResult(Version) : s_changed.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Returns the cached runtime load order, including searchable assemblies appended after the first read.
    /// </summary>
    /// <remarks>
    /// The current list, in the runtime's load order. The same array comes back until a searchable
    /// assembly is loaded; callers never see an assembly that has been unloaded.
    /// </remarks>
    public static IReadOnlyList<Assembly> Current
    {
        get
        {
            if (Volatile.Read(ref s_initialized))
            {
                return Volatile.Read(ref s_current);
            }

            lock (Gate)
            {
                if (!s_initialized)
                {
                    Volatile.Write(ref s_current, [.. AppDomain.CurrentDomain.GetAssemblies().Where(IsSearchable)]);
                    Volatile.Write(ref s_initialized, true);
                }

                return Volatile.Read(ref s_current);
            }
        }
    }

    private static bool IsSearchable(Assembly assembly) =>
        !assembly.IsDynamic && AssemblyLoadContext.GetLoadContext(assembly) is not { IsCollectible: true };
}
