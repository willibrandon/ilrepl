namespace IlRepl.Engine;

/// <summary>
/// Parses <c>.override</c> lines. Inside a method body, <c>.override T::M</c> names the slot the
/// method implements, matched by the method's own signature, and <c>.override method callConv
/// Ret T::M(params)</c> names it in full. At class level, <c>.override T::M with method ...</c>
/// pairs a slot with an implementing method that may be declared later in the block.
/// </summary>
public static class OverrideParser
{
    /// <summary>
    /// Parses an <c>.override</c> written inside a method body.
    /// </summary>
    /// <param name="spec">The text after <c>.override</c>.</param>
    /// <param name="context">The parse context of the method.</param>
    /// <param name="method">The signature of the method being written.</param>
    /// <param name="source">The line as typed.</param>
    /// <returns>The override.</returns>
    /// <exception cref="ReplException">The target is not a virtual method the type can implement, or its signature differs.</exception>
    public static OverrideDeclaration ParseInBody(string spec, ParseContext context, MethodSignature method, string source)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(source);
        var scope = new Binding.RuntimeBindingScope(context);
        var adapter = new Binding.RuntimeBindingAdapter(scope);
        var implementing = Binding.RuntimeSymbolImporter.Import(
            method, context.Scope?.Type is { } owner ? scope.ImportType(owner) : null,
            Binding.RuntimeDefinitions.OfDeclaration(method, 0), Binding.MethodSymbolSource.Declared, true);
        var bound = Binding.OverrideBinding.InBody(spec, scope, implementing);
        var target = adapter.ToResolvedMethod(bound.Target);
        return new OverrideDeclaration(target.Method!, Describe(target), source);
    }

    /// <summary>
    /// Parses a class-level <c>.override T::M with method callConv Ret This::Name(params)</c>.
    /// </summary>
    /// <param name="spec">The text after <c>.override</c>.</param>
    /// <param name="context">The parse context of the open type.</param>
    /// <param name="source">The line as typed.</param>
    /// <returns>The override, with the implementing method named for resolution at close.</returns>
    /// <exception cref="ReplException">The line is malformed.</exception>
    public static ClassOverrideDeclaration ParseAtClassLevel(string spec, ParseContext context, string source)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        var scope = new Binding.RuntimeBindingScope(context);
        var adapter = new Binding.RuntimeBindingAdapter(scope);
        var bound = Binding.OverrideBinding.AtClassLevel(spec, scope);
        var target = adapter.ToResolvedMethod(bound.Target);
        return new ClassOverrideDeclaration(
            target.Method!, Describe(target), bound.Body.Name, adapter.ToType(bound.Body.ReturnType),
            adapter.ToTypes(bound.Body.ParameterTypes), bound.Body.IsStatic, source);
    }

    /// <summary>
    /// Describes a resolved override target without asking a builder for its parameters.
    /// </summary>
    /// <param name="target">The target.</param>
    /// <returns>The description.</returns>
    public static string Describe(ResolvedMethod target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Declared is { } declared ? $"{declared.DescribeMember()} on {TypeNameFormatter.Pretty(target.DeclaringType)}" : MemberResolver.Describe(target.Method!);
    }

}
