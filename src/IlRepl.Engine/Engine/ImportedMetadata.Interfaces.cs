using System.Reflection.Metadata.Ecma335;

namespace IlRepl.Engine;

/// <summary>
/// Reads interface declarations without adding contracts inherited from a base type or another interface.
/// </summary>
internal static partial class ImportedMetadata
{
    /// <summary>
    /// Resolves only the source type's declared interface implementations, preserving their metadata order.
    /// </summary>
    /// <param name="owner">The declaring type and its generic context.</param>
    /// <returns>The interfaces named by the type's InterfaceImpl rows.</returns>
    internal static IEnumerable<Type> Interfaces(Type owner)
    {
        var metadata = ModuleMetadata.TryOpen(owner.Module)
            ?? throw new ReplException($"interface metadata for {owner.Name} is unavailable");
        var handle = MetadataTokens.TypeDefinitionHandle(owner.MetadataToken & 0x00ffffff);
        var arguments = owner.GetGenericArguments();
        foreach (var implementation in metadata.GetTypeDefinition(handle).GetInterfaceImplementations())
        {
            var contract = metadata.GetInterfaceImplementation(implementation).Interface;
            yield return owner.Module.ResolveType(MetadataTokens.GetToken(contract), arguments, null);
        }
    }
}
