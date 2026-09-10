using System.Reflection;
using System.Runtime.Loader;

namespace Greeter;

/// <summary>
/// Loads a fixture independently of REPL submissions after a test-controlled file appears.
/// </summary>
public static class BackgroundAssemblyLoader
{
    /// <summary>
    /// Waits asynchronously for a release file before loading an assembly into the default context.
    /// </summary>
    /// <param name="path">The assembly to load.</param>
    /// <param name="release">The file whose creation permits loading.</param>
    /// <returns>The asynchronous load.</returns>
    public static async Task LoadAsync(string path, string release)
    {
        while (!File.Exists(release))
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        if (OperatingSystem.IsBrowser())
        {
            Assembly.Load(File.ReadAllBytes(path));
        }
        else
        {
            AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }

        File.WriteAllText(release + ".loaded", "loaded");
    }
}
