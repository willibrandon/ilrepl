using System.Reflection;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// A field declared with <c>.field</c> inside a <c>.class</c> block.
/// </summary>
/// <param name="Name">The field name.</param>
/// <param name="Type">The field type.</param>
/// <param name="Attributes">Access, <c>static</c>, <c>initonly</c>, <c>literal</c>, and the special-name flags.</param>
/// <param name="Offset">The explicit-layout offset written before the type, or null.</param>
/// <param name="DefaultValue">The constant written after <c>=</c>, or null when there is none.</param>
/// <param name="HasDefault">True when a constant was written; the runtime does not use it to initialize the field.</param>
/// <param name="RequiredModifiers">The <c>modreq</c> types on the field type.</param>
/// <param name="OptionalModifiers">The <c>modopt</c> types on the field type.</param>
/// <param name="CustomAttributes">The custom attributes declared on the field.</param>
/// <param name="Source">The line as typed.</param>
public sealed record FieldDeclaration(
    string Name,
    Type Type,
    FieldAttributes Attributes,
    int? Offset,
    object? DefaultValue,
    bool HasDefault,
    IReadOnlyList<Type> RequiredModifiers,
    IReadOnlyList<Type> OptionalModifiers,
    IReadOnlyList<CustomAttributeDeclaration> CustomAttributes,
    string Source)
{
    /// <summary>
    /// The exact type retained when its runtime projection cannot represent its complete shape.
    /// </summary>
    internal TypeSymbol? ExactType { get; init; }

    /// <summary>
    /// True for a static field.
    /// </summary>
    public bool IsStatic => Attributes.HasFlag(FieldAttributes.Static);

    /// <summary>
    /// True for a literal, which has no storage.
    /// </summary>
    public bool IsLiteral => Attributes.HasFlag(FieldAttributes.Literal);

    /// <summary>
    /// True for a field that may only be stored from a constructor.
    /// </summary>
    public bool IsInitOnly => Attributes.HasFlag(FieldAttributes.InitOnly);

    /// <summary>
    /// Renders the field the way a listing shows it, for example <c>static literal int32 Max = 5</c>.
    /// </summary>
    /// <returns>The description.</returns>
    public string Describe()
    {
        var words = new List<string>();
        if (Offset is { } offset)
        {
            words.Add("[" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]");
        }

        words.Add(MemberAccess.AccessWord(Attributes));
        if (IsStatic)
        {
            words.Add("static");
        }

        if (IsInitOnly)
        {
            words.Add("initonly");
        }

        if (IsLiteral)
        {
            words.Add("literal");
        }

        words.Add(ExactType is null ? TypeNameFormatter.Pretty(Type) : SymbolRenderer.Annotated(ExactType));
        words.Add(Name);
        var text = string.Join(" ", words);
        return HasDefault ? text + " = " + ConstantText.Describe(DefaultValue) : text;
    }
}
