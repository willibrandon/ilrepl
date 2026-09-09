using System.Reflection;
using System.Runtime.Loader;

namespace Greeter;

/// <summary>
/// Measures runtime loading and retained memory between browser editor interactions without exposing a product test API.
/// </summary>
public static class CompletionProbe
{
    private static readonly HashSet<Assembly> s_loaded = [];
    private static readonly HashSet<Module> s_initialModules = [];
    private static readonly List<string> s_resolutionDetails = [];
    private static AssemblyLoadContext[] s_contexts = [];
    private static long s_initialMemory;
    private static int s_resolutions;

    /// <summary>
    /// Starts observing every existing load context and records the live runtime modules before editing begins.
    /// </summary>
    public static void Begin()
    {
        Stop();
        s_loaded.Clear();
        s_initialModules.Clear();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var module in assembly.GetModules())
            {
                s_initialModules.Add(module);
            }
        }

        s_contexts = [.. AssemblyLoadContext.All];
        foreach (var context in s_contexts)
        {
            context.Resolving += Resolve;
        }

        s_resolutions = 0;
        s_resolutionDetails.Clear();
        s_initialMemory = GC.GetTotalMemory(forceFullCollection: true);
        AppDomain.CurrentDomain.AssemblyLoad += Loaded;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        AppDomain.CurrentDomain.TypeResolve += Resolve;
    }

    /// <summary>
    /// Reports preview activity while excluding the one actual cell used to request this report.
    /// </summary>
    /// <param name="reportingAssembly">The executing assembly captured by the reporting cell.</param>
    public static string Report(Assembly reportingAssembly)
    {
        ArgumentNullException.ThrowIfNull(reportingAssembly);
        Stop();
        var loads = s_loaded.Count(assembly => assembly != reportingAssembly);
        var modules = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly != reportingAssembly)
            .SelectMany(assembly => assembly.GetModules()).Count(module => !s_initialModules.Contains(module));
        var retained = GC.GetTotalMemory(forceFullCollection: true) - s_initialMemory;
        var result = $"preview loads={loads}; modules={modules}; callbacks={s_resolutions}; retained={retained}";
        if (s_resolutionDetails.Count > 0)
        {
            Console.WriteLine(string.Join("\n", s_resolutionDetails));
        }

        if (loads > 0)
        {
            Console.WriteLine(string.Join(", ", s_loaded.Where(assembly => assembly != reportingAssembly)
                .Select(assembly => assembly.FullName)));
        }
        s_loaded.Clear();
        s_initialModules.Clear();
        return result;
    }

    private static void Stop()
    {
        AppDomain.CurrentDomain.AssemblyLoad -= Loaded;
        AppDomain.CurrentDomain.AssemblyResolve -= Resolve;
        AppDomain.CurrentDomain.TypeResolve -= Resolve;
        foreach (var context in s_contexts)
        {
            context.Resolving -= Resolve;
        }

        s_contexts = [];
    }

    private static void Loaded(object? sender, AssemblyLoadEventArgs args) => s_loaded.Add(args.LoadedAssembly);

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        s_resolutions++;
        s_resolutionDetails.Add(args.Name);
        return null;
    }

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        s_resolutions++;
        s_resolutionDetails.Add(name.FullName);
        return null;
    }
}
