using System.Reflection;
using System.Reflection.Metadata;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Reads loaded member signatures without losing details erased by reflection's runtime types.
/// </summary>
internal static class RuntimeMetadataSignatures
{
    /// <summary>
    /// Reads an original metadata signature or constructs the signature of a runtime-provided array member.
    /// </summary>
    /// <param name="method">The loaded method.</param>
    /// <returns>The complete metadata signature.</returns>
    public static IlMethodSignature Read(MethodBase method)
    {
        if (method.DeclaringType?.IsArray == true)
        {
            // Array members are synthesized by the runtime and do not have MethodDef rows.
            var parameters = method.GetParameters().Select(parameter => IlSignature.FromType(parameter.ParameterType)).ToArray();
            return new IlMethodSignature(SignatureCallingConvention.Default, !method.IsStatic, false, 0,
                IlSignature.FromType(method is MethodInfo info ? info.ReturnType : typeof(void)), parameters, parameters.Length);
        }

        var source = Source(method);
        using var lease = source.Lease();
        var provider = new MetadataSignatureProvider(token => method.Module.ResolveType(token));
        return MetadataSignatures.MethodDefinition(source.Reader, method.MetadataToken, provider, GenericContext.Empty);
    }

    /// <summary>
    /// Reads a field's original signature with generic parameters identified by position.
    /// </summary>
    /// <param name="field">The loaded field.</param>
    /// <returns>The complete metadata signature.</returns>
    public static IlSignature Read(FieldInfo field)
    {
        var source = Source(field);
        using var lease = source.Lease();
        var provider = new MetadataSignatureProvider(token => field.Module.ResolveType(token));
        return MetadataSignatures.FieldOperand(source.Reader, field.MetadataToken, provider, GenericContext.Empty)!;
    }

    private static AssemblySymbolSource Source(MemberInfo member) => AssemblySymbolSource.For(member.Module.Assembly)
        ?? throw new ReplException($"metadata is unavailable for the function-pointer signature of {member.Name}");
}
