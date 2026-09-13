using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using MethodDefinition = Mono.Cecil.MethodDefinition;

namespace IlRepl.Engine;

/// <summary>
/// Preserves graph-derived stack limits when Cecil's linear counter undercounts unreachable instructions.
/// </summary>
internal static class CecilStackHeaders
{
    /// <summary>
    /// Raises emitted fat-header stack limits to the validated maximum without changing IL or metadata tokens.
    /// </summary>
    public static void Apply(byte[] image, IReadOnlyDictionary<MethodDefinition, int> limits)
    {
        using var reader = new PEReader(new MemoryStream(image, writable: false));
        var metadata = reader.GetMetadataReader();
        foreach (var (method, limit) in limits)
        {
            var definition = metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle((int)method.MetadataToken.RID));
            var rva = definition.RelativeVirtualAddress;
            var section = reader.PEHeaders.SectionHeaders.First(section => rva >= section.VirtualAddress
                && rva < section.VirtualAddress + section.SizeOfRawData);
            var offset = rva - section.VirtualAddress + section.PointerToRawData;
            if ((image[offset] & 3) == 2)
            {
                if (limit > 8)
                {
                    throw new ReplException("a tiny method header cannot represent the required evaluation stack");
                }

                continue;
            }

            var written = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset + 2, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + 2, 2), checked((ushort)Math.Max(written, limit)));
        }
    }
}
