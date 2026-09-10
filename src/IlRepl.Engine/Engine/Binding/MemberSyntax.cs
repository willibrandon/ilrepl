namespace IlRepl.Engine.Binding;

/// <summary>
/// Represents a source-positioned method, field, or session-member reference.
/// </summary>
/// <remarks>
/// A member reference as written in IL: <c>[instance] [vararg] [RetType] Declaring::Name[&lt;Args&gt;][(Params)]</c>,
/// the field form <c>[FieldType] Declaring::Name</c>, or the session form <c>[RetType] Name(Params)</c>
/// that names a method defined with <c>.method</c>. Every part keeps its position.
/// </remarks>
public sealed record MemberSyntax
{
    /// <summary>
    /// The index of the first character of the reference.
    /// </summary>
    public int Start { get; init; }

    /// <summary>
    /// The index after the last character of the reference.
    /// </summary>
    public int End { get; init; }

    /// <summary>
    /// True when the reference was written with <c>instance</c>.
    /// </summary>
    public bool ExplicitInstance { get; init; }

    /// <summary>
    /// True when the reference was written with <c>vararg</c>, or its parameter list has a <c>...</c>.
    /// </summary>
    public bool IsVarArg { get; init; }

    /// <summary>
    /// The index after the calling convention words, where the types begin.
    /// </summary>
    public int TypesStart { get; init; }

    /// <summary>
    /// The return type, or the field type; null when only the declaring type was written before <c>::</c>.
    /// </summary>
    public TypeSyntax? ReturnType { get; init; }

    /// <summary>
    /// The declaring type; null for the session form, which has no <c>::</c>.
    /// </summary>
    public TypeSyntax? DeclaringType { get; init; }

    /// <summary>
    /// The index of the <c>::</c>, or -1 for the session form.
    /// </summary>
    public int SeparatorIndex { get; init; } = -1;

    /// <summary>
    /// The member name, decoded when it was quoted.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The index of the first character of the name, its quote included.
    /// </summary>
    public int NameStart { get; init; }

    /// <summary>
    /// The index after the last character of the name, its quote included.
    /// </summary>
    public int NameEnd { get; init; }

    /// <summary>
    /// True when the name was written between quotes.
    /// </summary>
    public bool NameQuoted { get; init; }

    /// <summary>
    /// The generic arguments written after the name, or null when no <c>&lt;...&gt;</c> follows it.
    /// </summary>
    public IReadOnlyList<TypeSyntax>? GenericArguments { get; init; }

    /// <summary>
    /// The arity written as <c>&lt;[N]&gt;</c>, which names a generic method definition without instantiating it; null otherwise.
    /// </summary>
    public int? GenericArity { get; init; }

    /// <summary>
    /// The index of the <c>&lt;</c> after the name, or -1.
    /// </summary>
    public int GenericStart { get; init; } = -1;

    /// <summary>
    /// The index after the <c>&gt;</c> that closes the generic arguments, or -1.
    /// </summary>
    public int GenericEnd { get; init; } = -1;

    /// <summary>
    /// The parameter types, or null when no parameter list was written.
    /// </summary>
    public IReadOnlyList<TypeSyntax>? Parameters { get; init; }

    /// <summary>
    /// The index in <see cref="Parameters"/> before which <c>...</c> was written, or null.
    /// </summary>
    public int? SentinelIndex { get; init; }

    /// <summary>
    /// The index of the <c>(</c> of the parameter list, or -1.
    /// </summary>
    public int ParametersStart { get; init; } = -1;

    /// <summary>
    /// The index after the <c>)</c> of the parameter list, or -1.
    /// </summary>
    public int ParametersEnd { get; init; } = -1;

    /// <summary>
    /// True for the session form, <c>[RetType] Name(Params)</c>, which has no declaring type.
    /// </summary>
    public bool IsSessionForm => DeclaringType is null;

    /// <summary>
    /// The fixed parameters: those before the <c>...</c>, or all of them when there is none.
    /// </summary>
    public IReadOnlyList<TypeSyntax> FixedParameters => Parameters is null ? [] : SentinelIndex is int s ? [.. Parameters.Take(
        s)] : Parameters;

    /// <summary>
    /// The parameters after the <c>...</c>, or null when there is none.
    /// </summary>
    public IReadOnlyList<TypeSyntax>? OptionalParameters => Parameters is not null && SentinelIndex is int s ? [.. Parameters.Skip(
        s)] : null;
}
