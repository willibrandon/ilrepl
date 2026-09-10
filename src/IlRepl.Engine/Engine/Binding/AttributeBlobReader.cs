using System.Text;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Decodes custom attribute blobs into symbolic values without constructing runtime arrays, enums or attributes.
/// </summary>
internal sealed class AttributeBlobReader
{
    private readonly byte[] _blob;
    private readonly IBindingScope _scope;
    private int _position;

    /// <summary>
    /// Initializes a reader over one attribute blob.
    /// </summary>
    /// <param name="blob">The serialized bytes.</param>
    /// <param name="scope">The scope used for serialized type references.</param>
    public AttributeBlobReader(byte[] blob, IBindingScope scope)
    {
        _blob = blob;
        _scope = scope;
    }

    /// <summary>
    /// Whether all bytes have been consumed.
    /// </summary>
    public bool AtEnd => _position >= _blob.Length;

    /// <summary>
    /// Reads one byte or refuses a truncated blob.
    /// </summary>
    /// <returns>The byte.</returns>
    public byte ReadByte() => _position < _blob.Length ? _blob[_position++] : throw new ReplException("attribute blob ends early");

    /// <summary>
    /// Reads an unsigned sixteen-bit value in little-endian order.
    /// </summary>
    /// <returns>The value.</returns>
    public ushort ReadUInt16() => (ushort)(ReadByte() | (ReadByte() << 8));

    /// <summary>
    /// Reads a nullable serialized UTF-8 string.
    /// </summary>
    /// <returns>The string or null.</returns>
    public string? ReadString()
    {
        var first = ReadByte();
        if (first == 0xFF)
        {
            return null;
        }

        var length = (first & 0x80) == 0 ? first : (first & 0xC0) == 0x80
            ? ((first & 0x3F) << 8) | ReadByte()
            : ((first & 0x1F) << 24) | (ReadByte() << 16) | (ReadByte() << 8) | ReadByte();
        if (length > _blob.Length - _position)
        {
            throw new ReplException("attribute blob ends inside a string");
        }

        var text = Encoding.UTF8.GetString(_blob, _position, length);
        _position += length;
        return text;
    }

    /// <summary>
    /// Decodes a field-or-property type tag.
    /// </summary>
    /// <returns>The serialized type.</returns>
    public TypeSymbol ReadType()
    {
        var tag = ReadByte();
        var keyword = tag switch
        {
            0x02 => "bool", 0x03 => "char", 0x04 => "int8", 0x05 => "uint8", 0x06 => "int16", 0x07 => "uint16",
            0x08 => "int32", 0x09 => "uint32", 0x0A => "int64", 0x0B => "uint64", 0x0C => "float32",
            0x0D => "float64", 0x0E => "string", 0x51 => "object", _ => null,
        };
        if (keyword is not null)
        {
            return TypeSymbol.Primitive(keyword);
        }

        return tag switch
        {
            0x1D => TypeSymbol.SzArray(ReadType()),
            0x50 => _scope.LookupType("System.Type", null, 0, false).Type,
            0x55 => SerializedTypeBinding.Parse(ReadString() ?? throw new ReplException("an enum argument names its type"), _scope),
            _ => throw new ReplException($"bad type tag {tag:X2} in attribute blob"),
        };
    }

    /// <summary>
    /// Reads a typed value into scalars and symbols.
    /// </summary>
    /// <param name="type">The serialized type.</param>
    /// <param name="what">The argument's diagnostic name.</param>
    /// <returns>The symbolic value.</returns>
    public AttributeValueSymbol ReadValue(TypeSymbol type, string what)
    {
        if (_scope.EnumUnderlyingType(type) is { } underlying)
        {
            return new AttributeValueSymbol(type, ReadValue(underlying, what).Value);
        }

        object? scalar = type.Keyword switch
        {
            "bool" => ReadByte() != 0, "char" => (char)ReadUInt16(), "int8" => unchecked((sbyte)ReadByte()),
            "uint8" => ReadByte(), "int16" => unchecked((short)ReadUInt16()), "uint16" => ReadUInt16(),
            "int32" => unchecked((int)ReadUInt32()), "uint32" => ReadUInt32(), "int64" => unchecked((long)ReadUInt64()),
            "uint64" => ReadUInt64(), "float32" => BitConverter.Int32BitsToSingle(unchecked((int)ReadUInt32())),
            "float64" => BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt64())), "string" => ReadString(), _ => null,
        };
        if (type.Keyword is "bool" or "char" or "int8" or "uint8" or "int16" or "uint16" or "int32"
            or "uint32" or "int64" or "uint64" or "float32" or "float64" or "string")
        {
            return new AttributeValueSymbol(type, scalar);
        }

        if (SymbolRenderer.IlPath(type) == "System.Type")
        {
            var text = ReadString();
            return new AttributeValueSymbol(type, text is null ? null : SerializedTypeBinding.Parse(text, _scope));
        }

        if (SymbolIdentity.Equal(type, TypeSymbol.Object))
        {
            return ReadValue(ReadType(), what);
        }

        if (type.IsArray)
        {
            var count = ReadUInt32();
            if (count == uint.MaxValue)
            {
                return new AttributeValueSymbol(type, null);
            }

            if (count > _blob.Length - _position)
            {
                throw new ReplException("attribute blob ends inside an array");
            }

            var elements = new List<AttributeValueSymbol>();
            for (var i = 0; i < count; i++)
            {
                elements.Add(ReadValue(type.Element!, what));
            }

            return new AttributeValueSymbol(type, elements);
        }

        throw new ReplException($"{what} has type {_scope.Pretty(type)}, which an attribute blob cannot carry");
    }

    private uint ReadUInt32() => (uint)(ReadUInt16() | (ReadUInt16() << 16));

    private ulong ReadUInt64() => ReadUInt32() | ((ulong)ReadUInt32() << 32);
}
