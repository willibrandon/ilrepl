using System.Reflection;
using System.Runtime.InteropServices;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Discovers attribute constructors, named members, and typed values before copied metadata is emitted.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private void ScanAttributes()
    {
        foreach (var type in _types.Keys.ToArray())
        {
            ScanAttributes(type.GetCustomAttributesData, type, TypeNameFormatter.Pretty(type));
            foreach (var parameter in type.GetGenericArguments())
            {
                ScanAttributes(parameter.GetCustomAttributesData, type, TypeNameFormatter.Pretty(type) + ": " + parameter.Name);
            }

            foreach (var field in type.GetFields(Declared))
            {
                ScanAttributes(field.GetCustomAttributesData, type, field.ToString()!);
            }

            foreach (var property in type.GetProperties(Declared).Where(property => ImportedMetadata.Accessors(property)
                .Any(_methods.ContainsKey)))
            {
                ScanAttributes(property.GetCustomAttributesData, type, TypeNameFormatter.Pretty(type) + "::" + property.Name);
            }

            foreach (var entry in type.GetEvents(Declared).Where(entry => ImportedMetadata.Accessors(entry).Any(_methods.ContainsKey)))
            {
                ScanAttributes(entry.GetCustomAttributesData, type, TypeNameFormatter.Pretty(type) + "::" + entry.Name);
            }
        }

        foreach (var method in _methods.Keys.ToArray())
        {
            var location = MemberResolver.Describe(method);
            var owner = ContextOf(method);
            ScanAttributes(method.GetCustomAttributesData, owner, location);
            foreach (var parameter in method.GetParameters())
            {
                ScanAttributes(parameter.GetCustomAttributesData, owner, location + ": parameter " + parameter.Position);
            }

            if (method is MethodInfo info)
            {
                ScanAttributes(info.ReturnParameter.GetCustomAttributesData, owner, location + ": return");
            }

            foreach (var parameter in method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes)
            {
                ScanAttributes(parameter.GetCustomAttributesData, owner, location + ": " + parameter.Name);
            }
        }
    }

    private void ScanAttributes(Func<IList<CustomAttributeData>> read, Type owner, string location)
    {
        IList<CustomAttributeData> attributes;
        try
        {
            attributes = read();
        }
        catch (BadImageFormatException exception)
        {
            throw new ReplException($"invalid attribute or marshalling metadata on {location}: {exception.Message}", exception);
        }
        catch (Exception exception) when (exception is CustomAttributeFormatException or TypeLoadException or FileNotFoundException)
        {
            throw new ReplException($"cannot read custom attributes on {location}: {exception.Message}", exception);
        }

        foreach (var attribute in attributes.Where(attribute => !IsProjectedAttribute(attribute.AttributeType)))
        {
            var origin = location + ": attribute " + TypeNameFormatter.Pretty(attribute.AttributeType);
            ConsiderType(attribute.AttributeType, owner);
            var copied = _types.ContainsKey(DefinitionOf(attribute.AttributeType));
            if (copied)
            {
                AddMethod(attribute.Constructor);
            }

            _dependencies.Add(new EditDependency(MemberResolver.Describe(attribute.Constructor), attribute.AttributeType.Assembly.FullName!,
                origin, copied ? "copied" : "external") { Access = MemberAccess.AccessWord(attribute.Constructor.Attributes) });
            foreach (var parameter in attribute.Constructor.GetParameters())
            {
                ConsiderType(parameter.ParameterType, owner);
            }

            foreach (var argument in attribute.ConstructorArguments)
            {
                ScanAttributeArgument(argument, owner, origin);
            }

            foreach (var argument in attribute.NamedArguments)
            {
                ScanAttributeArgument(argument.TypedValue, owner, origin);
                if (copied && argument.MemberInfo is PropertyInfo property)
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
    }

    private void ScanAttributeArgument(CustomAttributeTypedArgument argument, Type owner, string location)
    {
        ConsiderType(argument.ArgumentType, owner);
        if (argument.Value is Type type)
        {
            ConsiderType(type, owner);
            ReportType(type, location);
        }
        else if (argument.Value is IList<CustomAttributeTypedArgument> array)
        {
            foreach (var item in array)
            {
                ScanAttributeArgument(item, owner, location);
            }
        }
        else if (argument.Value is CustomAttributeTypedArgument nested)
        {
            ScanAttributeArgument(nested, owner, location);
        }
    }

    private static bool IsProjectedAttribute(Type type) => type == typeof(FieldOffsetAttribute) || type == typeof(StructLayoutAttribute)
        || type == typeof(InAttribute) || type == typeof(OutAttribute) || type == typeof(OptionalAttribute)
        || type == typeof(MarshalAsAttribute) || type == typeof(DllImportAttribute) || type == typeof(PreserveSigAttribute);
}
