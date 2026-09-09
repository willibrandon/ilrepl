namespace IlRepl.Engine.Binding;

/// <summary>
/// Shares member instruction restrictions between accepted input, replay and candidate eligibility.
/// </summary>
internal static class InstructionMemberRules
{
    /// <summary>
    /// Checks whether a store may assign an initonly field in this initializer and through this receiver.
    /// </summary>
    /// <param name="field">The field being stored.</param>
    /// <param name="opcode">The instruction name.</param>
    /// <param name="signature">The enclosing method, or null for a cell.</param>
    /// <param name="owner">The enclosing type, or null.</param>
    /// <param name="throughThis">Whether an instance store uses the original receiver.</param>
    /// <param name="pretty">The scope's type spelling.</param>
    /// <returns>The existing submission diagnostic, or null when the store is allowed.</returns>
    public static string? InitOnlyStoreProblem(
        FieldSymbol field, string? opcode, MethodSymbol? signature, TypeSymbol? owner,
        bool throughThis, Func<TypeSymbol?, string> pretty)
    {
        if (!field.IsInitOnly || opcode is not ("stsfld" or "stfld"))
        {
            return null;
        }

        var sameOwner = SymbolIdentity.Equal(field.DeclaringType.DefinitionOrSelf, owner?.DefinitionOrSelf);
        if (opcode == "stsfld")
        {
            return sameOwner && signature?.Name == ".cctor" ? null
                : $"{field.Name} is a static initonly field; it can only be stored in "
                    + $"{pretty(field.DeclaringType)}'s .cctor (ECMA II.16.1.2)";
        }

        var initializer = signature is not null && (signature.Name == ".ctor" || signature.ReturnRequiredModifiers.Any(
            modifier => SymbolRenderer.IlPath(modifier) == "System.Runtime.CompilerServices.IsExternalInit"));
        return sameOwner && initializer && throughThis ? null
            : $"{field.Name} is initonly; it can only be stored through this in "
                + $"{pretty(field.DeclaringType)}'s constructors or init accessors (ECMA II.16.1.2)";
    }
}
