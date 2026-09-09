using System.Globalization;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Describes the type and method arguments used when binding generic parameter references.
/// </summary>
/// <remarks>
/// The generic parameters that <c>!N</c> and <c>!!N</c> refer to while a reference is bound, as
/// symbols: the declaring type's arguments and the method's arguments, each of which may be a
/// parameter or a type an outer construction supplied.
/// </remarks>
/// <param name="TypeArguments">The declaring type's generic arguments, addressed by <c>!N</c>.</param>
/// <param name="MethodArguments">The method's generic arguments, addressed by <c>!!N</c>.</param>
public sealed record SymbolGenericContext(IReadOnlyList<TypeSymbol> TypeArguments, IReadOnlyList<TypeSymbol> MethodArguments)
{
    /// <summary>
    /// A context with nothing in scope.
    /// </summary>
    public static SymbolGenericContext Empty { get; } = new([], []);

    /// <summary>
    /// Specifies the placeholder count used before a member's declaring type is known.
    /// </summary>
    /// <remarks>
    /// How many placeholders a lenient context adds beyond what is in scope, for the first pass
    /// over a member reference whose declaring type is not yet known.
    /// </remarks>
    public const int LenientPlaceholders = 32;

    /// <summary>
    /// Extends both argument lists with placeholders for the first pass over a member reference.
    /// </summary>
    /// <remarks>
    /// A copy with placeholders appended to both lists, so a <c>!N</c> that belongs to the
    /// referenced type resolves to <c>object</c> until the declaring type is known.
    /// </remarks>
    /// <returns>The lenient context.</returns>
    public SymbolGenericContext Lenient()
    {
        var placeholders = Enumerable.Repeat(TypeSymbol.Object, LenientPlaceholders);
        return new SymbolGenericContext([.. TypeArguments, .. placeholders], [.. MethodArguments, .. placeholders]);
    }

    /// <summary>
    /// Resolves a <c>!N</c>, <c>!Name</c>, <c>!!N</c>, or <c>!!Name</c> reference.
    /// </summary>
    /// <param name="isMethod">True for <c>!!</c>, false for <c>!</c>.</param>
    /// <param name="reference">The index or the parameter name.</param>
    /// <returns>The type in that position.</returns>
    /// <exception cref="ReplException">The reference is out of range or the name is unknown.</exception>
    public TypeSymbol Resolve(bool isMethod, string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var list = isMethod ? MethodArguments : TypeArguments;
        var prefix = isMethod ? "!!" : "!";
        if (int.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            if (index < list.Count)
            {
                return list[index];
            }

            var scope = isMethod ? "method" : "type";
            throw new ReplException(list.Count == 0
                ? $"{prefix}{index} is out of range: no {scope} generic parameters are in scope"
                : $"{prefix}{index} is out of range: {list.Count} {scope} generic parameter(s) are in scope");
        }

        foreach (var t in list)
        {
            if (t.Name == reference)
            {
                return t;
            }
        }

        throw new ReplException($"no generic parameter named '{prefix}{reference}' is in scope");
    }
}
