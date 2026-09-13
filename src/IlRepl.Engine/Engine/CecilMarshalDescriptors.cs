using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Mono.Cecil;
using TypeReference = Mono.Cecil.TypeReference;

namespace IlRepl.Engine;

/// <summary>
/// Restores complete native marshalling blobs after Cecil assigns field and parameter tokens.
/// </summary>
internal sealed class CecilMarshalDescriptors
{
    private readonly Dictionary<IMarshalInfoProvider, byte[]> _descriptors = [];

    internal MarshalInfo Reserve(IMarshalInfoProvider target, byte[] descriptor, TypeReference objectType)
    {
        _descriptors.Add(target, descriptor);
        return new CustomMarshalInfo
        {
            ManagedType = objectType,
            Cookie = _descriptors.Count.ToString(CultureInfo.InvariantCulture) + new string('x', descriptor.Length),
        };
    }

    internal void Apply(byte[] image)
    {
        if (_descriptors.Count == 0)
        {
            return;
        }

        using var pe = new PEReader(new MemoryStream(image, writable: false));
        var metadata = pe.GetMetadataReader();
        var heap = pe.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.Blob);
        foreach (var (target, descriptor) in _descriptors)
        {
            var handle = MetadataTokens.EntityHandle(target.MetadataToken.ToInt32());
            var blob = handle.Kind == HandleKind.FieldDefinition
                ? metadata.GetFieldDefinition((FieldDefinitionHandle)handle).GetMarshallingDescriptor()
                : metadata.GetParameter((ParameterHandle)handle).GetMarshallingDescriptor();
            var reserved = metadata.GetBlobBytes(blob).Length;
            var prefix = reserved < 0x80 ? 1 : reserved < 0x4000 ? 2 : 4;
            var replacement = new BlobBuilder();
            replacement.WriteCompressedInteger(descriptor.Length);
            replacement.WriteBytes(descriptor);
            if (replacement.Count > reserved + prefix)
            {
                throw new ReplException("the native marshalling descriptor exceeds its reserved metadata space");
            }

            var destination = image.AsSpan(heap + MetadataTokens.GetHeapOffset(blob), reserved + prefix);
            destination.Clear();
            replacement.ToArray().CopyTo(destination);
        }
    }
}
