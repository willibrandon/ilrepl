using System.Globalization;
using System.Reflection;
using Mono.Cecil;

namespace IlRepl.Engine;

/// <summary>
/// Tracks source identities and copied member bindings across edit revisions.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    /// <summary>
    /// Maps each runtime member of an earlier revision onto its replacement in this one.
    /// </summary>
    /// <param name="previous">The revision whose copied types, methods, and fields are being replaced.</param>
    /// <param name="map">The map that receives the pairs, generic parameters and the forwarding method included.</param>
    internal void MapPrevious(ImportedMethodFamily previous, EmitMap map)
    {
        if (previous._forwardingMethod is { } oldForwarding && _forwardingMethod is { } newForwarding)
        {
            map.Add(oldForwarding, newForwarding);
            map.Add(oldForwarding.DeclaringType!, newForwarding.DeclaringType!);
            foreach (var (before, after) in oldForwarding.DeclaringType!.GetGenericArguments()
                .Zip(newForwarding.DeclaringType!.GetGenericArguments()))
            {
                map.Add(before, after);
            }
        }

        foreach (var (source, runtime) in previous._runtime)
        {
            if (!_runtime.TryGetValue(source, out var replacement))
            {
                continue;
            }

            switch (runtime)
            {
                case Type type:
                {
                    var current = (Type)replacement;
                    map.Add(type, current);
                    if (type.IsGenericTypeDefinition)
                    {
                        foreach (var (before, after) in type.GetGenericArguments().Zip(current.GetGenericArguments()))
                        {
                            map.Add(before, after);
                        }
                    }

                    break;
                }

                case MethodBase method:
                    map.Add(method, (MethodBase)replacement);
                    break;
                case FieldInfo field:
                    map.Add(field, (FieldInfo)replacement);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Redirects references bound to a committed revision to the matching baseline definitions.
    /// </summary>
    /// <param name="writer">The comparison assembly writer.</param>
    /// <param name="revision">The revision whose identities callers currently reference.</param>
    /// <param name="definitions">The source-to-definition map written for this family.</param>
    internal static void DefineRevisionReferences(
        CecilWriter writer,
        ImportedMethodFamily revision,
        Dictionary<MemberInfo,
        IMemberDefinition> definitions)
    {
        if (revision._forwardingMethod is { } forwarding)
        {
            var selected = (MethodDefinition)definitions[revision.Selected.Method];
            DefineForwarding(writer, forwarding, CecilForwardingMethod.Find(selected, revision.ForwardingName));
        }

        foreach (var (source, runtime) in revision._runtime)
        {
            if (!definitions.TryGetValue(source, out var definition))
            {
                continue;
            }

            switch (runtime)
            {
                case Type type:
                    writer.Define(type, (TypeDefinition)definition);
                    break;
                case MethodBase method:
                    writer.Define(method, (MethodDefinition)definition);
                    break;
                case FieldInfo field:
                    writer.Define(field, (FieldDefinition)definition);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Finds the original metadata member behind a compiled copy for normalized comparisons.
    /// </summary>
    /// <param name="member">A compiled member or an ordinary external reference.</param>
    /// <returns>The corresponding source member, or the supplied external reference.</returns>
    internal MemberInfo OriginalMember(MemberInfo member)
    {
        foreach (var (source, runtime) in _runtime)
        {
            if (runtime.Module == member.Module && runtime.MetadataToken == member.MetadataToken)
            {
                return source;
            }
        }

        return member;
    }

    /// <summary>
    /// Identifies the captured compiled version behind a logical session-method call.
    /// </summary>
    /// <param name="name">The session method name.</param>
    /// <returns>The immutable module and method identity.</returns>
    internal string PinnedIdentity(string name)
    {
        var method = _pinned[name];
        return method.Module.ModuleVersionId + ":" + method.MetadataToken.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Restores known copied type names in display text without changing instruction operands.
    /// </summary>
    /// <param name="text">A signature or stack display.</param>
    /// <returns>The display using original type names.</returns>
    internal string NormalizeNames(string text)
    {
        foreach (var (source, runtime) in _runtime.Where(pair => pair.Key is Type).OrderByDescending(pair =>
            ((Type)pair.Value).FullName?.Length))
        {
            text = text.Replace(TypeNameFormatter.IlAsm((Type)runtime), TypeNameFormatter.IlAsm((Type)source), StringComparison.Ordinal)
                .Replace(TypeNameFormatter.Pretty((Type)runtime), TypeNameFormatter.Pretty((Type)source), StringComparison.Ordinal);
        }

        return text;
    }
}
