using System.Reflection;
using System.Runtime.Loader;

namespace IlRepl.Engine;

/// <summary>
/// Caches searchable process assemblies without retaining collectible contexts during repeated name lookup.
/// </summary>
/// <remarks>
/// The assemblies of the process a name search may look in: the ones that are not dynamic and
/// sit in a load context that cannot unload. The runtime's own list is walked once, and again
/// only after an assembly load, because each walk takes a passing reference on every collectible
/// loader allocator, and an assembly being unloaded stays alive for as long as any thread keeps
/// walking. A resolver miss used to walk it up to seventeen times, so a session's dropped
/// definition never collected while another thread resolved names. An assembly in a collectible
/// context the session did not create is left out for the same reason a cell could never bind
/// it: it can be gone at any moment.
/// </remarks>
internal static class ProcessAssemblies
{
    private static readonly Lock Gate = new();
    private static Assembly[] s_current = [];
    private static int s_subscribed;
    private static int s_loads;
    private static int s_builtAt = -1;

    /// <summary>
    /// Returns the cached runtime load order, refreshing it after an assembly load.
    /// </summary>
    /// <remarks>
    /// The current list, in the runtime's load order. The same array comes back until an
    /// assembly is loaded; callers never see an assembly that has been unloaded.
    /// </remarks>
    public static IReadOnlyList<Assembly> Current
    {
        get
        {
            if (Volatile.Read(ref s_subscribed) == 0 && Interlocked.Exchange(ref s_subscribed, 1) == 0)
            {
                AppDomain.CurrentDomain.AssemblyLoad += (_, _) => Interlocked.Increment(ref s_loads);
            }

            var loads = Volatile.Read(ref s_loads);
            if (Volatile.Read(ref s_builtAt) == loads)
            {
                return s_current;
            }

            lock (Gate)
            {
                if (s_builtAt != loads)
                {
                    s_current = [.. AppDomain.CurrentDomain.GetAssemblies().Where(IsSearchable)];
                    Volatile.Write(ref s_builtAt, loads);
                }

                return s_current;
            }
        }
    }

    private static bool IsSearchable(Assembly assembly) =>
        !assembly.IsDynamic && AssemblyLoadContext.GetLoadContext(assembly) is not { IsCollectible: true };
}
