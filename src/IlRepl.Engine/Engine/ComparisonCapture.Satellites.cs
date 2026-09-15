using System.Reflection;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Retains loaded satellites that an original assembly resolves without a static assembly reference.
/// </summary>
public static partial class ComparisonCapture
{
    private static void CaptureLoadedSatellites(Session session, Dictionary<string, ComparisonAssembly> dependencies)
    {
        var parents = dependencies.Keys.Select(identity => new AssemblyName(identity)).ToArray();
        foreach (var assembly in session.Resolver.Assemblies.ToArray())
        {
            var name = assembly.GetName();
            if (string.IsNullOrEmpty(name.CultureName) || !parents.Any(parent =>
                string.Equals(name.Name, parent.Name + ".resources", StringComparison.OrdinalIgnoreCase)
                && name.GetPublicKeyToken().AsSpan().SequenceEqual(parent.GetPublicKeyToken()))) continue;
            CaptureDependency(name.FullName, session, dependencies);
        }
    }
}
