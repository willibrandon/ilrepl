using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IlRepl.Host;

/// <summary>
/// Publishes complete session files on Windows without unlinking the destination or invalidating existing readers.
/// </summary>
public sealed partial class SessionFileStore
{
    private static void ReplaceWindowsFile(string source, string destination)
    {
        const uint deleteAccess = 0x00010000;
        const int fileRenameInfoEx = 22;
        const uint replaceExistingWithPosixSemantics = 3;
        using var handle = CreateFileW(ExtendedWindowsPath(source), deleteAccess, FileShare.ReadWrite | FileShare.Delete,
            nint.Zero, FileMode.Open, 0, nint.Zero);
        if (handle.IsInvalid) throw ReplacementError(Marshal.GetLastPInvokeError(), destination);

        // FILE_RENAME_INFO aligns RootDirectory to pointer size; FileName follows its DWORD byte length.
        var lengthOffset = 2 * nint.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var name = ExtendedWindowsPath(destination);
        var nameBytes = Encoding.Unicode.GetByteCount(name);
        var information = new byte[nameOffset + nameBytes + nint.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(information, replaceExistingWithPosixSemantics);
        BinaryPrimitives.WriteUInt32LittleEndian(information.AsSpan(lengthOffset), (uint)nameBytes);
        Encoding.Unicode.GetBytes(name, information.AsSpan(nameOffset));
        if (SetFileInformationByHandle(handle, fileRenameInfoEx, information, (uint)information.Length) != 0) return;

        var error = Marshal.GetLastPInvokeError();
        if (error is 1 or 50 or 87)
        {
            // Filesystems without extended rename support still get a complete-file replacement or a reported failure.
            handle.Dispose();
            File.Move(source, destination, overwrite: true);
            return;
        }

        throw ReplacementError(error, destination);
    }

    private static IOException ReplacementError(int error, string destination) =>
        new("could not replace '" + destination + "': " + new Win32Exception(error).Message, unchecked((int)0x80070000) | error);

    private static string ExtendedWindowsPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string path, uint access, FileShare share,
        nint securityAttributes, FileMode creationDisposition, uint flags, nint template);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ReadOnlySpan<byte> information, uint size);
}
