using System.Text;

namespace IlRepl.Host;

/// <summary>
/// Owns a short private diagnostics endpoint that also fits Darwin's Unix socket bound.
/// </summary>
internal sealed class NativeDiagnosticPort : IDisposable
{
    private readonly string? _directory;

    /// <summary>
    /// Creates a unique endpoint without changing the caller's temporary-directory environment.
    /// </summary>
    internal NativeDiagnosticPort()
    {
        var name = "ijn-" + Guid.NewGuid().ToString("N")[..16];
        if (OperatingSystem.IsWindows())
        {
            Address = name;
            return;
        }

        var root = Path.GetTempPath();
        if (Encoding.UTF8.GetByteCount(Path.Join(root, name, "p")) + 1 > 100)
        {
            root = "/tmp";
        }

        _directory = Path.Join(root, name);
        Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Address = Path.Join(_directory, "p");
    }

    /// <summary>
    /// The filesystem socket path or Windows named pipe.
    /// </summary>
    internal string Address { get; }

    /// <summary>
    /// Removes the endpoint after the diagnostic server has closed it.
    /// </summary>
    public void Dispose()
    {
        if (_directory is not null && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
