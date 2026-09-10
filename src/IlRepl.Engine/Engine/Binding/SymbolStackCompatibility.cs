namespace IlRepl.Engine.Binding;

/// <summary>
/// Applies shared return compatibility to metadata and declaration symbols without reflection.
/// </summary>
internal static class SymbolStackCompatibility
{
    /// <summary>
    /// Classifies a symbolic type using the CLI evaluation-stack categories.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="scope">The scope supplying enum and declaration facts.</param>
    /// <returns>The stack category.</returns>
    public static StackCategory Category(TypeSymbol type, IBindingScope scope)
    {
        if (type.Kind == TypeSymbolKind.ByRef)
        {
            return StackCategory.ByRef;
        }

        if (type.IsGenericParameter)
        {
            return StackCategory.ObjectReference;
        }

        if (type.Kind is TypeSymbolKind.Pointer or TypeSymbolKind.FunctionPointer)
        {
            return StackCategory.NativeInt;
        }

        type = scope.EnumUnderlyingType(type) ?? type;
        return type.Keyword switch
        {
            "bool" or "char" or "int8" or "uint8" or "int16" or "uint16" or "int32" or "uint32" => StackCategory.Int32,
            "int64" or "uint64" => StackCategory.Int64,
            "native int" or "native uint" => StackCategory.NativeInt,
            "float32" or "float64" => StackCategory.Float,
            _ => type.IsValueTypeShape ? StackCategory.ValueType : StackCategory.ObjectReference,
        };
    }

    /// <summary>
    /// Checks a symbolic return value using the same category and assignment rules as live methods.
    /// </summary>
    /// <param name="actual">The inferred stack type.</param>
    /// <param name="declared">The declared return type.</param>
    /// <param name="scope">The binding scope.</param>
    /// <returns>Whether the return is compatible.</returns>
    public static bool CanReturn(TypeSymbol? actual, TypeSymbol declared, IBindingScope scope) => StackReturnRules.Accepts(
        actual,
        declared,
        type => Category(type, scope),
        type => SymbolIdentity.Equal(type, SymbolStackAlgebra.Instance.NullReference)
            || SymbolIdentity.Equal(type, SymbolStackAlgebra.Instance.UnknownReference),
        SymbolStackAlgebra.BoxedType,
        (from, to) => from.IsGenericParameter || to.IsGenericParameter || SymbolRelations.IsAssignable(from, to, scope),
        type => type.Element!,
        SymbolIdentity.Equal);
}
