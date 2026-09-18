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
        if (!OperatingSystem.IsWindows() && Encoding.UTF8.GetByteCount(root) > 55) root = "/tmp";
        var path = Path.Combine(root, "ilr-" + Guid.NewGuid().ToString("N")[..16]);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            SecureWindowsDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
    private static void SecureWindowsDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
