namespace IlRepl.Engine.Binding;

/// <summary>
/// Validates cell generic declarations and assignments without creating runtime generic parameters or types.
/// </summary>
internal static class CellGenericBinding
{
    /// <summary>
    /// Parses new parameter names against the cell's existing declaration and structural state.
    /// </summary>
    /// <param name="text">The declaration after the directive.</param>
    /// <param name="existing">The names already declared.</param>
    /// <param name="bodyEmpty">Whether declarations may still be changed.</param>
    /// <returns>The newly declared names.</returns>
    public static string[] Parameters(string text, IReadOnlyList<string> existing, bool bodyEmpty)
    {
        if (!bodyEmpty)
        {
            throw new ReplException("declare .typeparams before the first instruction of the cell (or .clear first)");
        }

        var names = Unwrap(text).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (names.Length == 0)
        {
            throw new ReplException("usage: .typeparams (T, U)");
        }

        foreach (var name in names)
        {
            if (!InstructionParser.IsIdentifier(name))
            {
                throw new ReplException($"bad type parameter name '{name}'");
            }

            if (existing.Contains(name) || names.Count(candidate => candidate == name) > 1)
            {
                throw new ReplException($"type parameter '{name}' is already declared");
            }
        }

        return names;
    }

    /// <summary>
    /// Binds a complete assignment in a scope without the cell's open generic parameters.
    /// </summary>
    /// <param name="text">The assignment after the directive.</param>
    /// <param name="parameters">The names being assigned.</param>
    /// <param name="scope">The concrete binding scope.</param>
    /// <returns>The assigned types.</returns>
    public static TypeSymbol[] Arguments(string text, IReadOnlyList<string> parameters, IBindingScope scope)
    {
        if (parameters.Count == 0)
        {
            throw new ReplException("the cell has no type parameters; declare them with .typeparams first");
        }

        var types = CilSyntaxParser.SplitTopLevel(Unwrap(text))
            .Select(part => SymbolBinder.BindType(CilSyntaxParser.ParseType(part), scope).Type).ToArray();
        if (types.Length != parameters.Count)
        {
            throw new ReplException(
                $"expected {parameters.Count} type argument(s) for ({string.Join(", ", parameters)}), got {types.Length}");
        }

        return types;
    }

    private static string Unwrap(string text)
    {
        var trimmed = text.Trim();
        return trimmed.StartsWith('(') && trimmed.EndsWith(')') ? trimmed[1..^1] : trimmed;
    }
}
