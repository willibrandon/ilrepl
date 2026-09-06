namespace IlRepl.Hosting;

/// <summary>
/// Finds the framework-dependent host next to the tool and the <c>dotnet</c> muxer that runs it.
/// </summary>
public static class HostLocator
{
    /// <summary>
    /// The environment variable that overrides where the host assembly is.
    /// </summary>
    public const string HostPathVariable = "ILREPL_HOST_PATH";

    /// <summary>
    /// The file name of the host assembly.
    /// </summary>
    public const string HostFileName = "ilrepl-host.dll";

    /// <summary>
    /// Finds the host assembly: <see cref="HostPathVariable"/> first, then <c>host/</c> beside
    /// the tool, then beside the tool itself.
    /// </summary>
    /// <param name="baseDirectory">The directory the tool runs from; defaults to <see cref="AppContext.BaseDirectory"/>.</param>
    /// <returns>The full path of the host assembly.</returns>
    /// <exception cref="HostProtocolException">No host assembly was found.</exception>
    public static string FindHost(string? baseDirectory = null)
    {
        var configured = Environment.GetEnvironmentVariable(HostPathVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }

            throw new HostProtocolException($"{HostPathVariable} points at '{configured}', which does not exist");
        }

        var directory = baseDirectory ?? AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(directory, "host", HostFileName),
            Path.Combine(directory, HostFileName),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new HostProtocolException(
            $"the ilrepl host was not found; expected {candidates[0]}. Reinstall the tool, or set {HostPathVariable}.");
    }

    /// <summary>
    /// Finds the <c>dotnet</c> muxer: <c>DOTNET_HOST_PATH</c> first, then the running muxer when the
    /// tool itself was started by one, then <c>dotnet</c> on the path.
    /// </summary>
    /// <returns>The command to start.</returns>
    public static string FindDotnet()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var processPath = Environment.ProcessPath;
        if (processPath is not null)
        {
            var name = Path.GetFileNameWithoutExtension(processPath);
            if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return processPath;
            }
        }

        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            var muxer = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(muxer))
            {
                return muxer;
            }
        }

        return "dotnet";
    }
}
