using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Mono.Cecil;
using ICustomAttributeProvider = Mono.Cecil.ICustomAttributeProvider;
using TypeReference = Mono.Cecil.TypeReference;
using TypeSpecification = Mono.Cecil.TypeSpecification;

namespace IlRepl.Engine;

/// <summary>
/// Renders executable signatures and metadata without losing custom modifiers.
/// </summary>
internal sealed partial class ExportIlAsmRenderer
{
    private string Signature(IlSignature signature) => IlSignatureRenderer.IlAsm(Normalize(signature));

    private IlSignature Normalize(IlSignature signature) => signature with
    {
        UnresolvedName = signature.UnresolvedName is { } name ? _names.GetValueOrDefault(name, name) : null,
        Element = signature.Element is { } element ? Normalize(element) : null,
        Modifier = signature.Modifier is { } modifier ? Normalize(modifier) : null,
        Arguments = signature.Arguments.Select(Normalize).ToArray(),
        Method = signature.Method is { } method ? Normalize(method) : null,
    };

    private IlMethodSignature Normalize(IlMethodSignature signature) => signature with
    {
        ReturnType = Normalize(signature.ReturnType),
        Parameters = signature.Parameters.Select(Normalize).ToArray(),
    };

    private string TypeText(TypeReference type)
    {
        var signature = MetadataSignatures.TypeOperand(_metadata, type.MetadataToken.ToInt32(), _signatures, GenericContext.Empty);
        return signature is null ? ScopedName(type)
            : IlSignatureRenderer.TypeOperand(Normalize(signature), type is TypeSpecification);
    }

    private string MethodText(MethodReference method)
    {
        var signature = MetadataSignatures.MethodOperand(_metadata, method.MetadataToken.ToInt32(), _signatures,
            GenericContext.Empty, out var arguments)!;
        return IlSignatureRenderer.MemberReference(Normalize(signature), TypeText(method.DeclaringType),
            IlAsmRenderer.MemberName(method.Name), arguments?.Select(Normalize).ToArray());
    }

    private string ScopedName(TypeReference type)
    {
        var root = type;
        while (root.DeclaringType is { } parent)
        {
            root = parent;
        }

        var scope = root.Scope is AssemblyNameReference reference && reference.Name != _assembly
            ? "[" + Identifier(reference.Name) + "]" : "";
        return scope + QualifiedName(type);
    }

    private static string QualifiedName(TypeReference type) => type.DeclaringType is { } parent
        ? QualifiedName(parent) + "/" + TypeNameFormatter.IlAsmTypeName(type.Name)
        : (type.Namespace.Length == 0 ? "" : string.Join(".", type.Namespace.Split('.').Select(Identifier)) + ".")
            + TypeNameFormatter.IlAsmTypeName(type.Name);

    private string Generics(IGenericParameterProvider owner) => !owner.HasGenericParameters ? ""
        : "<" + string.Join(", ", owner.GenericParameters.Select(parameter => "flags(" + Number((int)parameter.Attributes) + ") "
            + (parameter.HasConstraints ? "(" + string.Join(", ", parameter.Constraints.Select(item => TypeText(item.ConstraintType)))
                + ") " : "") + Identifier(parameter.Name))) + ">";

    private void GenericAttributes(IGenericParameterProvider owner)
    {
        foreach (var parameter in owner.GenericParameters)
        {
            if (parameter.HasCustomAttributes)
            {
                Line(".param type [" + Number(parameter.Position + 1) + "]");
                Attributes(parameter);
            }

            foreach (var constraint in parameter.Constraints.Where(constraint => constraint.HasCustomAttributes))
            {
                Line(".param constraint [" + Number(parameter.Position + 1) + "], " + TypeText(constraint.ConstraintType));
                Attributes(constraint);
            }
        }
    }

    private string Parameter(ParameterDefinition parameter, IlSignature signature) => ParameterFlags((int)parameter.Attributes)
        + Signature(signature) + " " + Marshal(parameter.MetadataToken) + Identifier(parameter.Name);

    private string Parameter(MethodReturnType parameter, IlSignature signature) => ParameterFlags((int)parameter.Attributes)
        + Signature(signature) + " " + Marshal(parameter.MetadataToken);

    private static string ParameterFlags(int flags) => (flags & ~19) != 0 ? "[" + Number(flags - 1) + "] "
        : ((flags & 1) != 0 ? "[in] " : "") + ((flags & 2) != 0 ? "[out] " : "") + ((flags & 16) != 0 ? "[opt] " : "");

    private string Marshal(MetadataToken token)
    {
        if (token.RID == 0)
        {
            return "";
        }

        var handle = MetadataTokens.EntityHandle(token.ToInt32());
        var blob = handle.Kind == HandleKind.FieldDefinition
            ? _metadata.GetFieldDefinition((FieldDefinitionHandle)handle).GetMarshallingDescriptor()
            : _metadata.GetParameter((ParameterHandle)handle).GetMarshallingDescriptor();
        return blob.IsNil ? "" : "marshal({ " + Bytes(_metadata.GetBlobBytes(blob)) + " }) ";
    }

    private void Attributes(ICustomAttributeProvider provider)
    {
        foreach (var attribute in provider.CustomAttributes)
        {
            Line(".custom " + MethodText(attribute.Constructor) + " = (" + Bytes(attribute.GetBlob()) + ")");
        }
    }

    private void ParameterAttributes(IConstantProvider parameter, int index)
    {
        var attributes = (ICustomAttributeProvider)parameter;
        if (parameter.HasConstant || attributes.HasCustomAttributes)
        {
            Line(".param [" + Number(index) + "]" + Constant(parameter));
            Attributes(attributes);
        }
    }

    private static string Constant(IConstantProvider provider) => !provider.HasConstant ? "" : " = " + (provider.Constant switch
    {
        null => "nullref",
        string value => LiteralParser.Escape(value),
        bool value => "bool(" + (value ? "true" : "false") + ")",
        char value => "char(" + Number((int)value) + ")",
        sbyte value => "int8(" + Number(value) + ")",
        byte value => "uint8(" + Number(value) + ")",
        short value => "int16(" + Number(value) + ")",
        ushort value => "uint16(" + Number(value) + ")",
        int value => "int32(" + Number(value) + ")",
        uint value => "uint32(" + Number(value) + ")",
        long value => "int64(" + Number(value) + ")",
        ulong value => "uint64(" + Number(value) + ")",
        float value => "float32(0x" + BitConverter.SingleToUInt32Bits(value).ToString("x8") + ")",
        double value => "float64(0x" + BitConverter.DoubleToUInt64Bits(value).ToString("x16") + ")",
        _ => throw new ReplException("the metadata contains an invalid constant"),
    });

    private static string Convention(IlMethodSignature signature) => (signature.HasThis ? "instance " : "")
        + (signature.ExplicitThis ? "explicit " : "") + (signature.Convention switch
        {
            SignatureCallingConvention.VarArgs => "vararg ",
            SignatureCallingConvention.CDecl => "unmanaged cdecl ",
            SignatureCallingConvention.StdCall => "unmanaged stdcall ",
            SignatureCallingConvention.ThisCall => "unmanaged thiscall ",
            SignatureCallingConvention.FastCall => "unmanaged fastcall ",
            SignatureCallingConvention.Unmanaged => "unmanaged ",
            _ => "",
        });
}
