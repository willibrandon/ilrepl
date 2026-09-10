using System.Reflection;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Reads loaded member signatures without losing details erased by reflection's runtime types.
/// </summary>
internal static class RuntimeMetadataSignatures
{
    /// <summary>
    /// Reads a method definition's original signature with generic parameters identified by position.
    /// </summary>
    /// <param name="method">The loaded method.</param>
    /// <returns>The complete metadata signature.</returns>
    public static IlMethodSignature Read(MethodBase method)
    {
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
