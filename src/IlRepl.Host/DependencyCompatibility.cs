using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace IlRepl.Host;

/// <summary>
/// Checks executable image platforms and CPU headers without loading dependency code.
/// </summary>
internal static class DependencyCompatibility
{
    /// <summary>
    /// Rejects a managed image that requires another process architecture.
    /// </summary>
    /// <param name="headers">The verified portable executable headers.</param>
    /// <param name="path">The dependency path used in diagnostics.</param>
    internal static void Managed(PEHeaders headers, string path)
    {
        if (headers.CoffHeader.Machine == Machine.I386 && headers.CorHeader is { } cor
            && cor.Flags.HasFlag(CorFlags.ILOnly) && !cor.Flags.HasFlag(CorFlags.Requires32Bit))
        {
            return;
        }

        var machine = (ushort)headers.CoffHeader.Machine;
        var architecture = MachineArchitecture(machine);
        if (architecture is null && headers.CorHeader?.ManagedNativeHeaderDirectory.Size > 0)
        {
            var operatingSystem = OperatingSystem.IsMacOS() ? 0x4644 : OperatingSystem.IsLinux() ? 0x7b79
                : OperatingSystem.IsFreeBSD() ? 0xadc4 : 0;
            architecture = MachineArchitecture((ushort)(machine ^ operatingSystem));
        }

        Check(architecture, path);
    }

    /// <summary>
    /// Rejects recognized native binaries for a different operating system or CPU while retaining non-executable package data.
    /// </summary>
    /// <param name="image">The exact dependency bytes.</param>
    /// <param name="path">The dependency path used in diagnostics.</param>
    internal static void Native(ReadOnlySpan<byte> image, string path)
    {
        if (image.Length >= 64 && image[0] == 'M' && image[1] == 'Z')
        {
            Platform(OperatingSystem.IsWindows(), path);
            using var pe = new PEReader(new MemoryStream(image.ToArray(), writable: false));
            Check(MachineArchitecture((ushort)pe.PEHeaders.CoffHeader.Machine), path);
            return;
        }

        if (image.Length >= 20 && image[..4].SequenceEqual("\u007fELF"u8))
        {
            Platform(OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD(), path);
            var machine = image[5] == 2 ? BinaryPrimitives.ReadUInt16BigEndian(image[18..])
                : BinaryPrimitives.ReadUInt16LittleEndian(image[18..]);
            Check(machine switch
            {
                3 => Architecture.X86,
                40 => Architecture.Arm,
                62 => Architecture.X64,
                183 => Architecture.Arm64,
                243 => Architecture.RiscV64,
                _ => null,
            }, path);

            return;
        }

        if (image.Length < 8)
        {
            return;
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(image);
        if (magic is 0xfeedface or 0xfeedfacf or 0xcefaedfe or 0xcffaedfe)
        {
            Platform(OperatingSystem.IsMacOS(), path);
            Check(MachArchitecture(Read32(image[4..], magic is 0xcefaedfe or 0xcffaedfe)), path);
        }
        else if (magic is 0xcafebabe or 0xcafebabf or 0xbebafeca or 0xbfbafeca)
        {
            Platform(OperatingSystem.IsMacOS(), path);
            var little = magic is 0xbebafeca or 0xbfbafeca;
            var count = Read32(image[4..], little);
            var size = magic is 0xcafebabf or 0xbfbafeca ? 32 : 20;
            if (count > (image.Length - 8) / size)
            {
                throw new InvalidDataException("invalid universal native image: " + path);
            }

            for (var index = 0; index < count; index++)
            {
                if (MachArchitecture(Read32(image[(8 + index * size)..], little)) == RuntimeInformation.ProcessArchitecture)
                {
                    return;
                }
            }

            Check(null, path);
        }
    }

    private static uint Read32(ReadOnlySpan<byte> image, bool little) => little
        ? BinaryPrimitives.ReadUInt32LittleEndian(image) : BinaryPrimitives.ReadUInt32BigEndian(image);

    private static Architecture? MachArchitecture(uint machine) => machine switch
    {
        7 => Architecture.X86, 12 => Architecture.Arm, 0x01000007 => Architecture.X64, 0x0100000c => Architecture.Arm64, _ => null,
    };

    private static Architecture? MachineArchitecture(ushort machine) => machine switch
    {
        0x014c => Architecture.X86, 0x01c4 => Architecture.Arm, 0x8664 => Architecture.X64, 0xaa64 => Architecture.Arm64, _ => null,
    };

    private static void Check(Architecture? architecture, string path)
    {
        if (architecture != RuntimeInformation.ProcessArchitecture)
        {
            throw new InvalidDataException($"dependency '{path}' architecture {architecture?.ToString().ToLowerInvariant() ?? "unknown"}"
                + $" is incompatible with the {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()} host");
        }
    }

    private static void Platform(bool compatible, string path)
    {
        if (!compatible)
        {
            throw new InvalidDataException($"native dependency '{path}' is incompatible with {RuntimeInformation.RuntimeIdentifier}");
        }
    }
}
