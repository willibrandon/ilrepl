using System.Reflection;
using System.Runtime.CompilerServices;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Retains exact symbolic field types for dynamic builders whose reflection type is only a projection.
/// </summary>
internal static class RuntimeFieldSignatures
{
    private static readonly ConditionalWeakTable<FieldInfo, TypeSymbol> Types = [];

    /// <summary>
    /// Associates a dynamic field with the exact type accepted for its declaration.
    /// </summary>
    /// <param name="field">The field builder or constructed wrapper.</param>
    /// <param name="type">The exact field type.</param>
    public static void Record(FieldInfo field, TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(type);
        Types.GetValue(field, _ => type);
    }

    /// <summary>
    /// Returns the exact type retained for a dynamic field, or null.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <returns>The exact type, or null.</returns>
    public static TypeSymbol? TypeOf(FieldInfo field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Types.TryGetValue(field, out var type) ? type : null;
    }
}
