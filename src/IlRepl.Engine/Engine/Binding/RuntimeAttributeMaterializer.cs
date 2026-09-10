using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Creates runtime values for accepted custom attributes after their signatures and values have been bound.
/// </summary>
internal static class RuntimeAttributeMaterializer
{
    /// <summary>
    /// Projects a symbolic attribute onto runtime metadata without invoking its constructor or named setters.
    /// </summary>
    /// <param name="attribute">The bound attribute.</param>
    /// <param name="adapter">The runtime scope's identity adapter.</param>
    /// <param name="source">The accepted source line.</param>
    /// <returns>The runtime declaration.</returns>
    public static CustomAttributeDeclaration Materialize(
        CustomAttributeSymbol attribute, RuntimeBindingAdapter adapter, string source)
    {
        var resolved = adapter.ToResolvedMethod(attribute.Constructor);
        if (resolved.Method is not ConstructorInfo constructor)
        {
            throw new ReplException("a custom attribute names a constructor: .custom instance void Attr::.ctor(...) = ...");
        }

        var fields = new List<(FieldInfo, object?)>();
        var properties = new List<(PropertyInfo, object?)>();
        foreach (var named in attribute.NamedArguments)
        {
            var value = Value(named.Value, adapter);
            if (named.Field is { } field)
            {
                fields.Add((adapter.ToField(field), value));
            }
            else if (named.Property is { } property)
            {
                var owner = adapter.ToType(property.DeclaringType);
                var runtime = owner.GetProperties(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Single(candidate => candidate.MetadataToken == property.Definition.Token);
                properties.Add((runtime, value));
            }
        }

        return new CustomAttributeDeclaration(
            constructor, attribute.Arguments.Select(value => Value(value, adapter)).ToArray(), fields, properties, source);
    }

    private static object? Value(AttributeValueSymbol value, RuntimeBindingAdapter adapter)
    {
        if (value.Value is null)
        {
            return null;
        }

        if (value.Value is TypeSymbol type)
        {
            return adapter.ToType(type);
        }

        if (value.Value is IReadOnlyList<AttributeValueSymbol> elements)
        {
            var array = Array.CreateInstance(adapter.ToType(value.Type.Element!), elements.Count);
            for (var index = 0; index < elements.Count; index++)
            {
                array.SetValue(Value(elements[index], adapter), index);
            }

            return array;
        }

        var runtime = adapter.ToType(value.Type);
        return runtime.IsEnum ? Enum.ToObject(runtime, value.Value) : value.Value;
    }
}
