namespace IlRepl.Engine.Binding;

/// <summary>
/// Shared CLI return compatibility rules parameterized by runtime or symbolic type operations.
/// </summary>
internal static class StackReturnRules
{
    /// <summary>
    /// Checks an inferred return value while preserving the distinction between exact and imprecise references.
    /// </summary>
    /// <typeparam name="T">The type representation.</typeparam>
    /// <param name="actual">The inferred stack entry.</param>
    /// <param name="declared">The declared return type.</param>
    /// <param name="category">The stack-category classifier.</param>
    /// <param name="isReferenceMarker">Whether a value denotes null or an imprecise reference.</param>
    /// <param name="boxedType">The value type retained by a boxing marker.</param>
    /// <param name="assignable">The assignment relation, from actual to declared.</param>
    /// <param name="element">The pointee of a byref.</param>
    /// <param name="equal">The type-identity relation.</param>
    /// <returns>Whether the return is compatible with the model's information.</returns>
    public static bool Accepts<T>(
        T? actual,
        T declared,
        Func<T, StackCategory> category,
        Func<T, bool> isReferenceMarker,
        Func<T, T?> boxedType,
        Func<T, T, bool> assignable,
        Func<T, T> element,
        Func<T, T, bool> equal) where T : class
    {
        if (actual is null)
        {
            return true;
        }

        var expected = category(declared);
        if (isReferenceMarker(actual))
        {
            return expected == StackCategory.ObjectReference;
        }

        if (boxedType(actual) is { } boxed)
        {
            return expected == StackCategory.ObjectReference && assignable(boxed, declared);
        }

        var found = category(actual);
        return expected switch
        {
            StackCategory.Int32 or StackCategory.Int64 or StackCategory.Float => found == expected,
            StackCategory.NativeInt => found is StackCategory.NativeInt or StackCategory.Int32,
            StackCategory.ByRef => found == StackCategory.ByRef && equal(element(actual), element(declared)),
            StackCategory.ValueType => found == StackCategory.ValueType && equal(actual, declared),
            _ => found == StackCategory.ObjectReference && assignable(actual, declared),
        };
    }
}
