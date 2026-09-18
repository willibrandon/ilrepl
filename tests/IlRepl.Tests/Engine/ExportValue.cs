using System.Text.Json;
using RuntimeType = System.Type;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Preserves a scalar argument or return value together with its actual runtime type.
/// </summary>
/// <param name="Type">The assembly-qualified type name, or null for a null value.</param>
/// <param name="Value">The serialized scalar value.</param>
internal sealed record ExportValue(string? Type, JsonElement Value)
{
    /// <summary>
    /// Captures a scalar without reducing it to a culture-sensitive display string.
    /// </summary>
    /// <param name="value">The argument or return value.</param>
    /// <returns>The typed JSON representation.</returns>
    internal static ExportValue From(object? value) => new(value?.GetType().AssemblyQualifiedName,
        JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object)));

    /// <summary>
    /// Reconstructs the scalar argument using its recorded runtime type.
    /// </summary>
    /// <returns>The deserialized argument.</returns>
    internal object? Materialize() => Type is null ? null : Value.Deserialize(RuntimeType.GetType(Type, throwOnError: true)!);
}
