using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Parses custom attributes through the shared binder and materializes their accepted runtime values.
/// </summary>
public static class CustomAttributeParser
{
    /// <summary>
    /// Parses the text after <c>.custom</c>.
    /// </summary>
    /// <param name="spec">The attribute text.</param>
    /// <param name="context">The parse context.</param>
    /// <param name="source">The line as typed.</param>
    /// <returns>The attribute.</returns>
    /// <exception cref="ReplException">The constructor cannot be resolved or the arguments do not decode.</exception>
    public static CustomAttributeDeclaration Parse(string spec, ParseContext context, string source)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        var scope = new RuntimeBindingScope(context);
        var attribute = CustomAttributeBinding.Parse(spec, scope);
        return RuntimeAttributeMaterializer.Materialize(attribute, new RuntimeBindingAdapter(scope), source);
    }
}
