using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace IlRepl.Protocol;

/// <summary>
/// Creates private, uniquely owned directories for terminal host sockets.
/// </summary>
internal static class SocketDirectory
{
    /// <summary>
    /// Creates a directory whose contents are accessible only to the current user.
    /// </summary>
    internal static string Create()
    {
        var root = Path.GetTempPath();
        var name = "ilr-" + Guid.NewGuid().ToString("N")[..16];
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsDirectory(root, name);
        }

        if (Encoding.UTF8.GetByteCount(root) > 55)
        {
            root = "/tmp";
        }

        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>
    /// Restricts an existing Windows socket file before the listener accepts clients.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static void SecureWindowsSocket(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static string CreateWindowsDirectory(string temporary, string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        string[] roots =
        [
            temporary,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
        ];
        Exception? failure = null;
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(root))
            {
                continue;
            }

            var path = Path.Combine(root, name);
            // Windows AF_UNIX uses UTF-8 and reserves one of its 108 address bytes for the terminator.
            if (Encoding.UTF8.GetByteCount(Path.Combine(path, "host.sock")) > 107)
            {
                continue;
            }

            try
            {
                new DirectoryInfo(path).Create(security);
                return path;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure = exception;
            }
        }

        throw new IOException("No writable directory is short enough for the local execution socket.", failure);
    }
}
