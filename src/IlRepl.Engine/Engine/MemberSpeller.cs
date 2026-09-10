using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Spells complete member references and confirms their declaring construction, signature and definition identity.
/// </summary>
public sealed class MemberSpeller
{
    private readonly SnapshotBindingScope _scope;
    private readonly TypeSpeller _types;

    /// <summary>
    /// Initializes a speller over an isolated editing context.
    /// </summary>
    /// <param name="scope">The captured context.</param>
    public MemberSpeller(SnapshotBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        _scope = scope.ForConfirmation();
        _types = new TypeSpeller(_scope);
    }

    /// <summary>
    /// Finds the shortest complete method reference that selects the intended member.
    /// </summary>
    /// <param name="method">The intended method, constructor or generic definition.</param>
    /// <param name="site">The reference being edited.</param>
    /// <returns>The confirmed reference, or null when this context cannot name it.</returns>
    public string? TrySpell(MethodSymbol method, CompletionSite site)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(site);
        try
        {
            foreach (var candidate in MethodCandidates(method, site).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    var bound = SymbolBinder.BindMethodReference(CilSyntaxParser.ParseMethodReference(candidate), _scope,
                        site.Owner is "newobj" or ".custom");
                    if (SameMethod(bound.Method, method)
                        && (bound.OptionalParameterTypes is null || bound.OptionalParameterTypes.Count == 0))
                    {
                        return candidate;
                    }
                }
                catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
                {
                    // A longer spelling can disambiguate a constructor, overload or return signature.
                }
            }
        }
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            // A dependency without a confirmed spelling makes only this candidate unavailable.
        }

        return null;
    }

    /// <summary>
    /// Renders every component of a method signature in the context that binds its generic parameters.
    /// </summary>
    /// <param name="method">The method represented by a completion row.</param>
    /// <returns>The full signature with calling convention and custom modifiers.</returns>
    public string FullSignature(MethodSymbol method) =>
        (method.IsStatic ? "static " : "") + MethodCandidates(method, CompletionSite.None).Last();

    /// <summary>
    /// Renders a field's declaring type, declared type and complete custom modifiers.
    /// </summary>
    /// <param name="field">The field represented by a completion row.</param>
    /// <returns>The full field signature.</returns>
    public string FullSignature(FieldSymbol field) => (field.IsStatic ? "static " : "")
        + _types.Spell(WithModifiers(field.FieldType, field.RequiredModifiers, field.OptionalModifiers)) + " "
        + _types.Spell(field.DeclaringType) + "::" + TypeNameFormatter.IlAsmIdentifier(field.Name);

    /// <summary>
    /// Finds a field reference that binds to the intended definition and declaring construction.
    /// </summary>
    /// <param name="field">The intended field.</param>
    /// <param name="site">The reference being edited.</param>
    /// <returns>The confirmed reference, or null.</returns>
    public string? TrySpell(FieldSymbol field, CompletionSite site)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(site);
        if (_types.TrySpell(field.DeclaringType) is not { } owner)
        {
            return null;
        }

        var reference = owner + "::" + TypeNameFormatter.IlAsmIdentifier(field.Name);
        var prefix = site.ReturnTypeText is { } written && MatchesType(written, field.FieldType) ? written.Trim() + " " : "";
        try
        {
            var candidate = prefix + reference;
            var bound = SymbolBinder.BindFieldReference(CilSyntaxParser.ParseFieldReference(candidate), _scope);
            return SymbolIdentity.Equal(bound, field) && SymbolIdentity.Equal(bound.FieldType, field.FieldType)
                && SymbolIdentity.SequenceEqual(bound.RequiredModifiers, field.RequiredModifiers)
                && SymbolIdentity.SequenceEqual(bound.OptionalModifiers, field.OptionalModifiers) ? candidate : null;
        }
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Confirms every signature component as well as the method's retained definition and instantiation.
    /// </summary>
    /// <param name="actual">The result of binding an insertion.</param>
    /// <param name="expected">The selected candidate.</param>
    /// <returns>Whether both references describe the same member.</returns>
    public static bool SameMethod(MethodSymbol actual, MethodSymbol expected)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(expected);
        return SymbolIdentity.Equal(actual, expected)
            && actual.Arity == expected.Arity && actual.CallingConvention == expected.CallingConvention
            && SymbolIdentity.Equal(actual.ReturnType, expected.ReturnType)
            && SymbolIdentity.SequenceEqual(actual.ReturnRequiredModifiers, expected.ReturnRequiredModifiers)
            && SymbolIdentity.SequenceEqual(actual.ReturnOptionalModifiers, expected.ReturnOptionalModifiers)
            && actual.Parameters.Count == expected.Parameters.Count
            && actual.Parameters.Zip(expected.Parameters).All(pair =>
                SymbolIdentity.Equal(pair.First.Type, pair.Second.Type)
                && SymbolIdentity.SequenceEqual(pair.First.RequiredModifiers, pair.Second.RequiredModifiers)
                && SymbolIdentity.SequenceEqual(pair.First.OptionalModifiers, pair.Second.OptionalModifiers));
    }

    private IEnumerable<string> MethodCandidates(MethodSymbol method, CompletionSite site)
    {
        if (SymbolReferences.Method(method).Any(type => type.HasUnresolved))
        {
            yield break;
        }

        var owner = method.DeclaringType is null ? "" : _types.Spell(method.DeclaringType) + "::";
        var typeParameters = method.DeclaringType is null
            ? _scope.Generics.TypeArguments : _scope.GenericArgumentsOf(method.DeclaringType);
        var methodParameters = method.IsGenericDefinition
            ? method.GenericParameters.Select(parameter => parameter.AsType).ToArray() : _scope.Generics.MethodArguments;
        var signatureScope = (SnapshotBindingScope)_scope.WithGenerics(new SymbolGenericContext(typeParameters, methodParameters));
        var signatureTypes = new TypeSpeller(signatureScope);
        var name = method.IsConstructor ? method.Name : TypeNameFormatter.IlAsmIdentifier(method.Name);
        if (method.GenericArguments.Count > 0)
        {
            name += "<" + string.Join(", ", method.GenericArguments.Select(_types.Spell)) + ">";
        }
        else if (method.IsGenericDefinition)
        {
            name += "<[" + SymbolRenderer.Number(method.Arity) + "]>";
        }

        var parameters = method.Parameters.Select(parameter => signatureTypes.Spell(
            WithModifiers(parameter.Type, parameter.RequiredModifiers, parameter.OptionalModifiers))).ToList();
        if (method.IsVarArg)
        {
            parameters.Add("...");
        }

        var reference = owner + name + "(" + string.Join(", ", parameters) + ")";
        var instance = !method.IsStatic && site.ExplicitInstance ? "instance " : "";
        var convention = method.IsVarArg ? "vararg " : "";
        var returnType = WithModifiers(method.ReturnType, method.ReturnRequiredModifiers, method.ReturnOptionalModifiers);
        var typedReturn = site.ReturnTypeText is { } written && MatchesType(written, method.ReturnType)
            ? written.Trim() + " " : "";
        yield return instance + convention + typedReturn + reference;
        if (!method.IsStatic)
        {
            instance = "instance ";
            yield return instance + convention + typedReturn + reference;
        }

        yield return instance + convention + signatureTypes.Spell(returnType) + " " + reference;
    }

    private bool MatchesType(string text, TypeSymbol type)
    {
        try
        {
            return SymbolIdentity.Equal(SymbolBinder.BindType(CilSyntaxParser.ParseType(text), _scope).Type, type);
        }
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            return false;
        }
    }

    private static TypeSymbol WithModifiers(
        TypeSymbol type, IReadOnlyList<TypeSymbol> required, IReadOnlyList<TypeSymbol> optional)
    {
        foreach (var modifier in required)
        {
            type = TypeSymbol.Modified(type, modifier, true);
        }

        foreach (var modifier in optional)
        {
            type = TypeSymbol.Modified(type, modifier, false);
        }

        return type;
    }
}
