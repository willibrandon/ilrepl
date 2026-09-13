namespace IlRepl.Engine.Binding;

/// <summary>
/// Enumerates the runtime types named inside a symbolic signature, including function pointers.
/// </summary>
internal static class RuntimeSymbolTypes
{
    /// <summary>
    /// Returns whether projecting the symbol to a runtime type would lose metadata shape.
    /// </summary>
    /// <param name="type">The symbolic type.</param>
    /// <returns>Whether the exact symbol must be retained.</returns>
    public static bool RequiresExact(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Kind is TypeSymbolKind.FunctionPointer or TypeSymbolKind.Array or TypeSymbolKind.Modified
            || type.Element is not null && RequiresExact(type.Element)
            || type.Modifier is not null && RequiresExact(type.Modifier)
            || type.Arguments.Any(RequiresExact);
    }

    /// <summary>
    /// Returns whether projecting a call-site signature would lose metadata shape.
    /// </summary>
    /// <param name="signature">The symbolic signature.</param>
    /// <returns>Whether the exact signature must be retained.</returns>
    public static bool RequiresExact(MethodSignatureSymbol signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        return RequiresExact(signature.ReturnType) || signature.Parameters.Any(RequiresExact);
    }

    /// <summary>
    /// Reapplies an existing top-level annotation chain to a replacement core type.
    /// </summary>
    internal static TypeSymbol? RebaseExact(TypeSymbol previous, TypeSymbol? exact, TypeSymbol replacement)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(replacement);
        if (exact is null)
        {
            return RequiresExact(replacement) ? replacement : null;
        }

        var annotations = new List<(TypeSymbol Modifier, bool Required)>();
        var core = exact;
        while (core.Kind == TypeSymbolKind.Modified)
        {
            annotations.Add((core.Modifier!, core.IsRequired));
            core = core.Element!;
        }

        if (!SymbolIdentity.Equal(core, previous))
        {
            return RequiresExact(replacement) ? replacement : null;
        }

        var result = replacement;
        for (var index = annotations.Count - 1; index >= 0; index--)
        {
            var annotation = annotations[index];
            result = TypeSymbol.Modified(result, annotation.Modifier, annotation.Required);
        }

        return RequiresExact(result) ? result : null;
    }

    /// <summary>
    /// Returns the materialized types contained in a symbolic type.
    /// </summary>
    /// <param name="type">The symbolic type.</param>
    /// <returns>The runtime types it names, with the outer type first when one exists.</returns>
    public static IEnumerable<Type> Materialized(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.Kind == TypeSymbolKind.FunctionPointer)
        {
            foreach (var nested in Materialized(type.Signature!.ReturnType))
            {
                yield return nested;
            }

            foreach (var parameter in type.Signature.Parameters)
            {
                foreach (var nested in Materialized(parameter))
                {
                    yield return nested;
                }
            }

            yield break;
        }

        if (type.Kind == TypeSymbolKind.Modified)
        {
            foreach (var modifier in Materialized(type.Modifier!))
            {
                yield return modifier;
            }
        }

        var runtime = Materialize(type);
        if (runtime is not null)
        {
            yield return runtime;
        }

        if (type.Element is not null)
        {
            foreach (var nested in Materialized(type.Element))
            {
                yield return nested;
            }
        }

        foreach (var argument in type.Arguments)
        {
            foreach (var nested in Materialized(argument))
            {
                yield return nested;
            }
        }
    }

    private static Type? Materialize(TypeSymbol type)
    {
        try
        {
            return RuntimeBindingAdapter.Materialize(type);
        }
        catch (InvalidOperationException)
        {
            // An unresolved reference contributes no runtime dependency.
            return null;
        }
    }
}
