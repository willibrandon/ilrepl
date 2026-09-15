using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Loads captured dependencies while preserving the file context required by an external original.
/// </summary>
public static partial class ComparisonWorker
{
    private static Assembly LoadDependency(AssemblyLoadContext context, ComparisonAssembly dependency, bool preserveContext)
    {
        if (preserveContext && !dependency.IsCollectible) context = AssemblyLoadContext.Default;
        if (preserveContext && dependency.OriginalLocation is { } location)
        {
            VerifyOriginalFile(dependency, location);
            var assembly = context.LoadFromAssemblyPath(location);
            VerifyOriginalFile(dependency, location);
            if (!string.Equals(assembly.Location, location, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ReplException("the original assembly file context could not be restored: " + location);
            return assembly;
        }

        using var stream = new MemoryStream(dependency.Image, writable: false);
        return context.LoadFromStream(stream);
    }

    private static void VerifyOriginalFile(ComparisonAssembly dependency, string location)
    {
        try
        {
            if (File.ReadAllBytes(location).AsSpan().SequenceEqual(dependency.Image)) return;
        }
        catch (IOException exception)
        {
            throw new ReplException("the original assembly file is unavailable: " + location, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ReplException("the original assembly file is unavailable: " + location, exception);
        }
        throw new ReplException("the original assembly file changed after comparison capture: " + location);
    }
}
