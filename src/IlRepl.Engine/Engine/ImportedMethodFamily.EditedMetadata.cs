using System.Reflection;
using IlRepl.Protocol;
using Mono.Cecil;
using AttributeProvider = Mono.Cecil.ICustomAttributeProvider;

namespace IlRepl.Engine;

/// <summary>
/// Applies current edited attribute and override declarations over the pinned source metadata.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private void ScanEditedMetadata(MethodEditBody body, Type owner)
    {
        var location = MemberResolver.Describe(body.Method);
        foreach (var entry in body.State.Entries.Where(entry => entry.Custom is not null))
        {
            var attribute = entry.Custom!;
            var origin = location + ": attribute " + TypeNameFormatter.Pretty(attribute.AttributeType);
            ConsiderType(attribute.AttributeType, owner);
            var copied = _types.ContainsKey(DefinitionOf(attribute.AttributeType));
            if (copied)
            {
                AddMethod(attribute.Constructor);
            }

            _dependencies.Add(new EditDependency(MemberResolver.Describe(attribute.Constructor), attribute.AttributeType.Assembly.FullName!,
                origin, copied ? "copied" : "external") { Access = MemberAccess.AccessWord(attribute.Constructor.Attributes) });
            foreach (var (parameter, value) in attribute.Constructor.GetParameters().Zip(attribute.FixedArguments))
            {
                ScanEditedAttributeValue(parameter.ParameterType, value, owner, origin);
            }

            foreach (var (field, value) in attribute.NamedFields)
            {
                ConsiderType(field.DeclaringType!, owner);
                ScanEditedAttributeValue(field.FieldType, value, owner, origin);
            }

            foreach (var (property, value) in attribute.NamedProperties)
            {
                ConsiderType(property.DeclaringType!, owner);
                ScanEditedAttributeValue(property.PropertyType, value, owner, origin);
                if (copied)
                {
                    foreach (var accessor in ImportedMetadata.Accessors(property))
                    {
                        if (_types.ContainsKey(DefinitionOf(accessor.DeclaringType!)))
                        {
                            AddMethod(accessor);
                        }
                    }
                }
            }
        }

        foreach (var mapping in body.State.Overrides)
        {
            var target = mapping.Target;
            var declaring = target.DeclaringType!;
            var owners = owner.GetInterfaces().ToList();
            for (var parent = owner.BaseType; parent is not null; parent = parent.BaseType)
            {
                owners.Add(parent);
            }

            if (!owners.Any(candidate => SignatureIdentity.Equal(candidate, declaring)))
            {
                throw new ReplException(TypeNameFormatter.Pretty(owner) + " cannot .override " + MemberResolver.Describe(target)
                    + ": the target is not one of its bases or interfaces");
            }

            if (target.IsFinal)
            {
                throw new ReplException(MemberResolver.Describe(target) + " is final and cannot be overridden");
            }

            ConsiderType(declaring, owner);
            ScanMethodSignature(target, null);
            var copied = _types.ContainsKey(DefinitionOf(declaring));
            if (copied)
            {
                AddMethod(target, metadataOnly: true);
            }

            _dependencies.Add(new EditDependency(MemberResolver.Describe(target), declaring.Assembly.FullName!, location + ": .override",
                copied ? "copied" : "external") { Access = MemberAccess.AccessWord(target.Attributes) });
        }
    }

    private void ScanEditedAttributeValue(Type declared, object? value, Type owner, string location)
    {
        ConsiderType(declared, owner);
        if (value is Type type)
        {
            ConsiderType(type, owner);
            ReportType(type, location);
        }
        else if (value is Array array)
        {
            var element = array.GetType().GetElementType()!;
            ConsiderType(array.GetType(), owner);
            foreach (var item in array)
            {
                ScanEditedAttributeValue(element, item, owner, location);
            }
        }
        else if (value is not null && declared == typeof(object))
        {
            ConsiderType(value.GetType(), owner);
        }
    }

    private static void WriteEditedAttributes(MethodEditBody body, MethodDefinition definition, CecilWriter writer)
    {
        foreach (var entry in body.State.Entries.Where(entry => entry.Custom is not null))
        {
            AttributeProvider target = entry.ParamIndex switch
            {
                > 0 and { } index => definition.Parameters[index - 1],
                0 => definition.MethodReturnType,
                _ => definition,
            };
            target.CustomAttributes.Add(CecilCustomAttributes.Create(entry.Custom!, writer));
        }
    }

    private void WriteOverrides(Type original, Dictionary<MemberInfo, IMemberDefinition> definitions, CecilWriter writer)
    {
        var mappings = new Dictionary<string, (MethodDefinition Body, MethodReference Target)>(StringComparer.Ordinal);
        void Add(MethodBase body, MethodBase declaration)
        {
            if (!definitions.TryGetValue(IlAsmRenderer.DefinitionOf(body), out var method))
            {
                return;
            }

            var target = writer.Import(declaration, declaration.DeclaringType);
            var key = target.FullName + " [" + target.DeclaringType.Scope + "]";
            mappings[key] = ((MethodDefinition)method, target);
        }

        foreach (var (body, declaration) in ImportedMetadata.Overrides(original))
        {
            Add(body, declaration);
        }
        // Edited slots replace the pinned mapping; duplicates to the same slot remain idempotent.
        foreach (var (method, body) in _methods.Where(pair => pair.Value is not null
            && pair.Key.DeclaringType is { } owner && DefinitionOf(owner) == original))
        {
            foreach (var declaration in body!.State.Overrides)
            {
                Add(method, declaration.Target);
            }
        }

        foreach (var mapping in mappings.Values)
        {
            mapping.Body.Overrides.Add(mapping.Target);
        }
    }
}
