using System.Globalization;
using Mono.Cecil;

namespace IlRepl.Engine;

/// <summary>
/// Encodes semantic attribute declarations and preserves attributes on executable forwarding methods.
/// </summary>
internal static class CecilCustomAttributes
{
    /// <summary>
    /// Creates metadata for an accepted declaration without executing an attribute constructor or setter.
    /// </summary>
    /// <param name="declaration">The parsed semantic declaration.</param>
    /// <param name="writer">The writer that remaps referenced types and members.</param>
    /// <returns>The remapped custom attribute.</returns>
    internal static CustomAttribute Create(CustomAttributeDeclaration declaration, CecilWriter writer)
    {
        var attribute = new CustomAttribute(writer.Import(declaration.Constructor));
        var parameters = declaration.Constructor.GetParameters();
        for (var index = 0; index < declaration.FixedArguments.Count; index++)
            attribute.ConstructorArguments.Add(Argument(parameters[index].ParameterType, declaration.FixedArguments[index], writer));
        foreach (var (field, value) in declaration.NamedFields)
            attribute.Fields.Add(new CustomAttributeNamedArgument(field.Name, Argument(field.FieldType, value, writer)));
        foreach (var (property, value) in declaration.NamedProperties)
            attribute.Properties.Add(new CustomAttributeNamedArgument(property.Name, Argument(property.PropertyType, value, writer)));
        return attribute;
    }

    /// <summary>
    /// Copies method, return and parameter attributes to the method users or scenarios can call.
    /// </summary>
    /// <param name="source">The selected method with complete metadata.</param>
    /// <param name="target">The forwarder or observation wrapper.</param>
    /// <param name="parameterOffset">The added receiver parameter count.</param>
    internal static void CopyMethod(MethodDefinition source, MethodDefinition target, int parameterOffset = 0)
    {
        Copy(source, target);
        Copy(source.MethodReturnType, target.MethodReturnType);
        foreach (var (parameter, index) in source.Parameters.Select((parameter, index) => (parameter, index)))
            Copy(parameter, target.Parameters[index + parameterOffset]);
    }

    private static void Copy(ICustomAttributeProvider source, ICustomAttributeProvider target)
    {
        foreach (var original in source.CustomAttributes)
        {
            var copy = new CustomAttribute(original.Constructor);
            foreach (var argument in original.ConstructorArguments) copy.ConstructorArguments.Add(argument);
            foreach (var argument in original.Fields) copy.Fields.Add(argument);
            foreach (var argument in original.Properties) copy.Properties.Add(argument);
            target.CustomAttributes.Add(copy);
        }
    }

    private static CustomAttributeArgument Argument(Type declared, object? value, CecilWriter writer)
    {
        if (declared == typeof(object))
        {
            var actual = value switch { null => typeof(object), Type => typeof(Type), _ => value.GetType() };
            return new CustomAttributeArgument(writer.Object, value is null ? null : Argument(actual, value, writer));
        }
        if (declared == typeof(Type))
            return new CustomAttributeArgument(writer.Import(declared), value is Type type ? writer.Import(type) : null);
        if (declared.IsArray)
        {
            var items = value is Array array ? array.Cast<object?>()
                .Select(item => Argument(declared.GetElementType()!, item, writer)).ToArray() : null;
            return new CustomAttributeArgument(writer.Import(declared), items);
        }
        if (declared.IsEnum && value is not null)
            value = Convert.ChangeType(value, Enum.GetUnderlyingType(declared), CultureInfo.InvariantCulture);
        return new CustomAttributeArgument(writer.Import(declared), value);
    }
}
