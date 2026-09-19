using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Mono.Cecil;
using PropertyDefinition = Mono.Cecil.PropertyDefinition;
using TypeReference = Mono.Cecil.TypeReference;

namespace IlRepl.Engine;

/// <summary>
/// Restores exact array shapes and native marshalling descriptors beyond Cecil's metadata model.
/// </summary>
internal sealed class CecilSignatureFixups
{
    private readonly Dictionary<string, byte[]> _shapes = new(StringComparer.Ordinal);
    private readonly CecilMarshalDescriptors _marshalling = new();
    private readonly HashSet<PropertyDefinition> _properties = [];

    /// <summary>
    /// Retains a property's independent index signature without modifying its accessor methods.
    /// </summary>
    /// <param name="property">The emitted property.</param>
    /// <param name="parameters">The exact imported index parameter types.</param>
    /// <param name="size">The original signature size used to reserve space for expanded destination tokens.</param>
    public void Property(PropertyDefinition property, IEnumerable<TypeReference> parameters, int size)
    {
        var signature = new FunctionPointerType { HasThis = property.HasThis, ReturnType = property.PropertyType };
        foreach (var parameter in parameters)
        {
            signature.Parameters.Add(new ParameterDefinition(parameter));
        }

        for (var index = 0; index < size * 2 + 16; index++)
        {
            property.PropertyType = new OptionalModifierType(signature, property.PropertyType);
        }

        _properties.Add(property);
    }

    /// <summary>
    /// Reserves enough metadata space to preserve a complete native marshalling descriptor.
    /// </summary>
    /// <param name="target">The field or parameter receiving the descriptor.</param>
    /// <param name="descriptor">The exact native signature bytes.</param>
    /// <param name="objectType">The placeholder marshaller type used only before the image is corrected.</param>
    /// <returns>The temporary descriptor to assign before writing the image.</returns>
    public MarshalInfo Marshal(IMarshalInfoProvider target, byte[] descriptor, TypeReference objectType) =>
        _marshalling.Reserve(target, descriptor, objectType);

    /// <summary>
    /// Creates an array reference with a distinct temporary shape when its exact bounds need restoring.
    /// </summary>
    /// <param name="element">The imported element type.</param>
    /// <param name="rank">The number of dimensions.</param>
    /// <param name="sizes">The encoded prefix of dimension sizes.</param>
    /// <param name="bounds">The encoded prefix of lower bounds.</param>
    /// <returns>The array reference to write before applying signature corrections.</returns>
    public ArrayType Array(TypeReference element, int rank, IReadOnlyList<int> sizes, IReadOnlyList<int> bounds)
    {
        var needsFixup = sizes.Count > bounds.Count || rank == 1 && sizes.Count == 0 && bounds.Count == 0;
        // Runtime array ranks stop at 32. A temporary rank of 33 cannot collide with a loaded signature.
        var encodedRank = needsFixup ? 33 : rank;
        var encodedBounds = needsFixup ? Enumerable.Repeat(-0x10000000 + _shapes.Count, encodedRank).ToArray() : bounds;
        var array = new ArrayType(element, encodedRank);
        for (var index = 0; index < encodedRank; index++)
        {
            var lower = index < encodedBounds.Count ? (int?)encodedBounds[index] : null;
            var upper = index < sizes.Count ? (lower ?? 0) + sizes[index] - 1 : (int?)null;
            array.Dimensions[index] = new ArrayDimension(lower, upper);
        }

        if (needsFixup)
        {
            _shapes.Add(Convert.ToHexString(Shape(encodedRank, sizes, encodedBounds)), Shape(rank, sizes, bounds));
        }

        return array;
    }

    /// <summary>
    /// Corrects signature blobs after Cecil assigns destination tokens, without moving metadata heap entries.
    /// </summary>
    /// <param name="image">The newly written assembly image.</param>
    public void Apply(byte[] image)
    {
        _marshalling.Apply(image);
        if (_shapes.Count == 0 && _properties.Count == 0)
        {
            return;
        }

        using var stream = new MemoryStream(image, writable: false);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var properties = _properties.Select(property => metadata.GetPropertyDefinition(
            MetadataTokens.PropertyDefinitionHandle((int)property.MetadataToken.RID)).Signature).ToHashSet();
        var heapOffset = pe.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.Blob);
        var visited = new HashSet<BlobHandle>();
        foreach (var handle in metadata.MemberReferences)
        {
            Rewrite(metadata.GetMemberReference(handle).Signature, false);
        }

        foreach (var handle in metadata.FieldDefinitions)
        {
            Rewrite(metadata.GetFieldDefinition(handle).Signature, false);
        }

        foreach (var handle in metadata.MethodDefinitions)
        {
            Rewrite(metadata.GetMethodDefinition(handle).Signature, false);
        }

        foreach (var handle in metadata.PropertyDefinitions)
        {
            Rewrite(metadata.GetPropertyDefinition(handle).Signature, false);
        }

        for (var row = 1; row <= metadata.GetTableRowCount(TableIndex.TypeSpec); row++)
        {
            Rewrite(metadata.GetTypeSpecification(MetadataTokens.TypeSpecificationHandle(row)).Signature, true);
        }

        for (var row = 1; row <= metadata.GetTableRowCount(TableIndex.MethodSpec); row++)
        {
            Rewrite(metadata.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(row)).Signature, false);
        }

        for (var row = 1; row <= metadata.GetTableRowCount(TableIndex.StandAloneSig); row++)
        {
            Rewrite(metadata.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(row)).Signature, false);
        }

        void Rewrite(BlobHandle handle, bool typeOnly)
        {
            if (!visited.Add(handle))
            {
                return;
            }

            var reader = metadata.GetBlobReader(handle);
            var output = new BlobBuilder();
            byte? propertyHeader = null;
            if (properties.Contains(handle))
            {
                propertyHeader = reader.ReadByte();
                reader.ReadCompressedInteger();
                if (reader.ReadByte() != 0x20 || reader.ReadTypeHandle() is not { Kind: HandleKind.TypeSpecification } type)
                {
                    throw new ReplException("the reserved property signature is malformed");
                }

                var signature = metadata.GetTypeSpecification((TypeSpecificationHandle)type).Signature;
                var exact = metadata.GetBlobReader(signature);
                if (exact.ReadByte() != 0x1b)
                {
                    throw new ReplException("the reserved property signature has no method signature");
                }

                CopySignature(ref exact, output);
            }
            else if (typeOnly)
            {
                CopyType(ref reader, output);
            }
            else
            {
                CopySignature(ref reader, output);
            }

            var bytes = output.ToArray();
            if (propertyHeader is { } header)
            {
                bytes[0] = header;
            }

            var original = metadata.GetBlobBytes(handle);
            if (bytes.AsSpan().SequenceEqual(original))
            {
                return;
            }

            var replacement = new BlobBuilder();
            replacement.WriteCompressedInteger(bytes.Length);
            replacement.WriteBytes(bytes);
            var oldPrefix = original.Length < 0x80 ? 1 : original.Length < 0x4000 ? 2 : 4;
            var destination = image.AsSpan(heapOffset + MetadataTokens.GetHeapOffset(handle), oldPrefix + original.Length);
            if (replacement.Count > destination.Length)
            {
                throw new ReplException("the exact array signature exceeds its reserved metadata space");
            }

            destination.Clear();
            replacement.ToArray().CopyTo(destination);
        }
    }

    private static byte[] Shape(int rank, IReadOnlyList<int> sizes, IReadOnlyList<int> bounds)
    {
        var blob = new BlobBuilder();
        blob.WriteCompressedInteger(rank);
        blob.WriteCompressedInteger(sizes.Count);
        foreach (var size in sizes)
        {
            blob.WriteCompressedInteger(size);
        }

        blob.WriteCompressedInteger(bounds.Count);
        foreach (var bound in bounds)
        {
            blob.WriteCompressedSignedInteger(bound);
        }

        return blob.ToArray();
    }

    private void CopySignature(ref BlobReader reader, BlobBuilder output)
    {
        var header = reader.ReadByte();
        output.WriteByte(header);
        var kind = header & 0x0f;
        if (kind == 0x06)
        {
            CopyType(ref reader, output);
            return;
        }

        if ((header & 0x10) != 0)
        {
            CopyInteger(ref reader, output);
        }

        var count = CopyInteger(ref reader, output);
        if (kind is not (0x07 or 0x0a))
        {
            CopyType(ref reader, output);
        }

        for (var index = 0; index < count; index++)
        {
            CopyType(ref reader, output);
        }
    }

    private void CopyType(ref BlobReader reader, BlobBuilder output)
    {
        var kind = reader.ReadByte();
        output.WriteByte(kind);
        switch (kind)
        {
            case 0x0f: // Pointer.
            case 0x10: // Managed pointer.
            case 0x1d: // Vector.
            case 0x41: // Optional-parameter boundary.
            case 0x45: // Pinned local.
                CopyType(ref reader, output);
                break;
            case 0x11: // Value type.
            case 0x12: // Class.
            case 0x13: // Type parameter.
            case 0x1e: // Method parameter.
                CopyInteger(ref reader, output);
                break;
            case 0x1f: // Required modifier.
            case 0x20: // Optional modifier.
                CopyInteger(ref reader, output);
                CopyType(ref reader, output);
                break;
            case 0x14: // Multidimensional array.
            {
                CopyType(ref reader, output);
                var rank = reader.ReadCompressedInteger();
                var sizes = new int[reader.ReadCompressedInteger()];
                for (var index = 0; index < sizes.Length; index++)
                {
                    sizes[index] = reader.ReadCompressedInteger();
                }

                var bounds = new int[reader.ReadCompressedInteger()];
                for (var index = 0; index < bounds.Length; index++)
                {
                    bounds[index] = reader.ReadCompressedSignedInteger();
                }

                var shape = Shape(rank, sizes, bounds);
                output.WriteBytes(_shapes.GetValueOrDefault(Convert.ToHexString(shape), shape));
                break;
            }
            case 0x15: // Constructed generic type.
            {
                CopyType(ref reader, output);
                var count = CopyInteger(ref reader, output);
                for (var index = 0; index < count; index++)
                {
                    CopyType(ref reader, output);
                }

                break;
            }
            case 0x1b: // Function pointer.
                CopySignature(ref reader, output);
                break;
        }
    }

    private static int CopyInteger(ref BlobReader reader, BlobBuilder output)
    {
        var value = reader.ReadCompressedInteger();
        output.WriteCompressedInteger(value);
        return value;
    }
}
