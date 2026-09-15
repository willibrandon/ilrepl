using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace IlRepl.Engine;

/// <summary>
/// Reads property and event associations without relying on runtime-specific accessor projections.
/// </summary>
internal static partial class ImportedMetadata
{
    /// <summary>
    /// Resolves the declared accessor methods, including associations that reflection omits on some runtimes.
    /// </summary>
    /// <param name="member">The property or event.</param>
    /// <param name="otherOnly">Whether to return only methods with Other semantics.</param>
    /// <returns>The associated methods.</returns>
    internal static MethodInfo[] Accessors(MemberInfo member, bool otherOnly = false)
    {
        var metadata = ModuleMetadata.TryOpen(member.Module)
            ?? throw new ReplException($"accessor metadata for {member.Name} is unavailable");
        var handle = MetadataTokens.EntityHandle(member.MetadataToken);
        MethodDefinitionHandle[] handles;
        if (handle.Kind == HandleKind.PropertyDefinition)
        {
            var accessors = metadata.GetPropertyDefinition((PropertyDefinitionHandle)handle).GetAccessors();
            handles = otherOnly ? [.. accessors.Others] : [accessors.Getter, accessors.Setter, .. accessors.Others];
        }
        else
        {
            var accessors = metadata.GetEventDefinition((EventDefinitionHandle)handle).GetAccessors();
            handles = otherOnly ? [.. accessors.Others] : [accessors.Adder, accessors.Remover, accessors.Raiser, .. accessors.Others];
        }

        var arguments = member.DeclaringType!.GetGenericArguments();
        return handles.Where(handle => !handle.IsNil).Select(handle =>
            (MethodInfo)member.Module.ResolveMethod(MetadataTokens.GetToken(handle), arguments, null)!).ToArray();
    }

    /// <summary>
    /// Reads the event type even when the retained event has no add accessor.
    /// </summary>
    /// <param name="entry">The event whose metadata supplies the handler type.</param>
    /// <returns>The complete event type signature.</returns>
    internal static IlSignature EventSignature(EventInfo entry)
    {
        var metadata = ModuleMetadata.TryOpen(entry.Module)
            ?? throw new ReplException($"event metadata for {entry.Name} is unavailable");
        var handle = MetadataTokens.EventDefinitionHandle(entry.MetadataToken & 0x00ffffff);
        var arguments = entry.DeclaringType!.GetGenericArguments();
        var provider = new MetadataSignatureProvider(token => entry.Module.ResolveType(token, arguments, null));
        return MetadataSignatures.TypeOperand(metadata, MetadataTokens.GetToken(metadata.GetEventDefinition(handle).Type),
            provider, new GenericContext(arguments, []))!;
    }
}
