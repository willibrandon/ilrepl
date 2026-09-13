using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Materializes property, event and accessor headers parsed through the shared symbol binder.
/// </summary>
public static class PropertyEventParser
{
    /// <summary>
    /// Parses a property header in the current runtime context.
    /// </summary>
    /// <param name="spec">The text after .property.</param>
    /// <param name="context">The open type's context.</param>
    /// <returns>The property header.</returns>
    public static PropertyHeader ParseProperty(string spec, ParseContext context)
    {
        var scope = new RuntimeBindingScope(context);
        var adapter = new RuntimeBindingAdapter(scope);
        var header = PropertyEventBinding.ParseProperty(spec, scope);
        return new PropertyHeader(header.Name, adapter.ToType(header.Type),
            [.. header.ParameterTypes.Select(adapter.ToType)], header.IsStatic, header.Attributes, header.OpensBlock)
        {
            ExactType = header.ExactType,
            ExactParameterTypes = header.ExactParameterTypes,
        };
    }

    /// <summary>
    /// Parses an event header in the current runtime context.
    /// </summary>
    /// <param name="spec">The text after .event.</param>
    /// <param name="context">The open type's context.</param>
    /// <returns>The event header.</returns>
    public static EventHeader ParseEvent(string spec, ParseContext context)
    {
        var scope = new RuntimeBindingScope(context);
        var adapter = new RuntimeBindingAdapter(scope);
        var header = PropertyEventBinding.ParseEvent(spec, scope);
        return new EventHeader(header.Name, adapter.ToType(header.HandlerType), header.Attributes, header.OpensBlock)
        {
            ExactHandlerType = header.ExactHandlerType,
        };
    }

    /// <summary>
    /// Parses a reference to a property's or event's accessor method.
    /// </summary>
    /// <param name="kind">The accessor directive without its dot.</param>
    /// <param name="spec">The text after the directive.</param>
    /// <param name="context">The open type's context.</param>
    /// <returns>The accessor reference.</returns>
    public static AccessorReference ParseAccessor(string kind, string spec, ParseContext context)
    {
        var scope = new RuntimeBindingScope(context);
        var adapter = new RuntimeBindingAdapter(scope);
        var accessor = PropertyEventBinding.ParseAccessor(kind, spec, scope);
        return new AccessorReference(accessor.Kind, accessor.Name, adapter.ToType(accessor.ReturnType),
            [.. accessor.ParameterTypes.Select(adapter.ToType)], accessor.IsStatic)
        {
            ExactReturnType = accessor.ExactReturnType,
            ExactParameterTypes = accessor.ExactParameterTypes,
        };
    }
}
