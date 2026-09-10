namespace IlRepl.Engine.Binding;

/// <summary>
/// Resolves explicit method mappings through symbols for runtime declarations and editing previews.
/// </summary>
internal static class OverrideBinding
{
    /// <summary>
    /// Resolves and validates an override declared inside its implementing method.
    /// </summary>
    /// <param name="text">The text after .override.</param>
    /// <param name="scope">The implementing method's scope.</param>
    /// <param name="method">The implementing signature.</param>
    /// <returns>The slot and implementing method.</returns>
    public static OverrideSymbol InBody(string text, IBindingScope scope, MethodSymbol method)
    {
        if (text.Contains(" with ", StringComparison.Ordinal))
        {
            throw new ReplException("the '.override X with Y' form belongs at class level; inside a method write .override T::M");
        }

        var target = Resolve(text.Trim(), scope, method);
        Check(target.Method, method);
        return new OverrideSymbol(target, method);
    }

    /// <summary>
    /// Resolves a class-level mapping, allowing its implementing method to be declared later.
    /// </summary>
    /// <param name="text">The text after .override.</param>
    /// <param name="scope">The open class's scope.</param>
    /// <returns>The slot and implementing method.</returns>
    public static OverrideSymbol AtClassLevel(string text, IBindingScope scope)
    {
        var separator = text.IndexOf(" with ", StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new ReplException(".override T::M belongs inside the method that implements it (or use 'with method' at class level)");
        }

        var bodyText = text[(separator + 6)..].Trim();
        bodyText = bodyText.StartsWith("method ", StringComparison.Ordinal) ? bodyText[7..].Trim() : bodyText;
        var syntax = CilSyntaxParser.ParseMethodReference(bodyText);
        if (syntax.DeclaringType is null)
        {
            throw new ReplException("the 'with' side names a method of this type");
        }

        var reference = PropertyEventBinding.ParseAccessor("override", bodyText, scope);
        var body = new MethodSymbol
        {
            Definition = DefinitionId.None,
            Source = MethodSymbolSource.Declared,
            DeclaringType = scope.Access.Type,
            Name = reference.Name,
            ReturnType = reference.ReturnType,
            Parameters = [.. reference.ParameterTypes.Select(type => new ParameterSymbol(type, null))],
            Attributes = reference.IsStatic ? System.Reflection.MethodAttributes.Static : System.Reflection.MethodAttributes.PrivateScope,
        };
        return new OverrideSymbol(Resolve(text[..separator].Trim(), scope, body), body);
    }

    private static BoundMethod Resolve(string text, IBindingScope scope, MethodSymbol method)
    {
        if (text.StartsWith("method ", StringComparison.Ordinal))
        {
            var target = SymbolBinder.BindMethodReference(CilSyntaxParser.ParseMethodReference(text[7..]), scope, false);
            return target.Method.DeclaringType is null
                ? throw new ReplException("an .override target must be a method of a base type or an interface, not a session method")
                : target;
        }

        var syntax = CilSyntaxParser.ParseMethodReference(text);
        if (syntax.DeclaringType is null)
        {
            throw new ReplException("usage: .override T::M  (or .override method instance RetType T::M(params))");
        }

        var owner = SymbolBinder.BindType(syntax.DeclaringType, scope).Type;
        var candidates = scope.TryGetDeclaration(owner, out var declaration)
            ? declaration.FindMethods(syntax.Name).Select(candidate => SymbolRelations.Instantiate(candidate, owner, []))
            : scope.Methods(owner, syntax.Name);
        var match = candidates.FirstOrDefault(candidate => SignatureSymbolIdentity.Equal(candidate, method));
        if (match is null)
        {
            throw new ReplException($"no overload {scope.Pretty(owner)}::{syntax.Name} matches {scope.Describe(method)}");
        }

        return new BoundMethod(match, match.Source == MethodSymbolSource.Declared ? match : null, null);
    }

    private static void Check(MethodSymbol target, MethodSymbol method)
    {
        if (!target.IsVirtual)
        {
            throw new ReplException($"{target} is not virtual, so nothing can override it");
        }

        if (!method.IsVirtual)
        {
            throw new ReplException($"{method.Name} must be virtual to .override {target}");
        }

        if (!SignatureSymbolIdentity.Equal(target, method))
        {
            throw new ReplException($".override target {target} does not match {method}");
        }
    }
}
