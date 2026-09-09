using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// A bound field declaration with its constant retained as text until validation or emission needs it.
/// </summary>
/// <param name="Name">The field name.</param>
/// <param name="Type">The field type and custom modifiers.</param>
/// <param name="Attributes">The declared field flags.</param>
/// <param name="Offset">The explicit offset, or null.</param>
/// <param name="ConstantText">The constant text, or null.</param>
/// <param name="Source">The original declaration line.</param>
public sealed record FieldDeclarationSyntax(
    string Name,
    BoundType Type,
    FieldAttributes Attributes,
    int? Offset,
    string? ConstantText,
    string Source);
