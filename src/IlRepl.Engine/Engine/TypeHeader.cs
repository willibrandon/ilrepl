using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// Holds a class header's syntax before its type references are bound.
/// </summary>
/// <param name="Attributes">The type attributes as declared, with the layout and kind words folded in.</param>
/// <param name="Kind">The kind decided by the header words alone; <see cref="TypeKind.Class"/> until the base type says otherwise.</param>
/// <param name="KindFromWord">True when <c>interface</c>, <c>value</c>, or <c>enum</c> decided the kind.</param>
/// <param name="Layout">The field layout.</param>
/// <param name="Namespace">The namespace, or empty.</param>
/// <param name="Name">The name with its arity suffix.</param>
/// <param name="GenericParameters">The generic parameters, redeclared ones first.</param>
/// <param name="BaseTypeText">The text after <c>extends</c>, or null.</param>
/// <param name="InterfaceTexts">The texts after <c>implements</c>.</param>
/// <param name="OpensBlock">True when the header ended with <c>{</c>.</param>
/// <param name="ClosesBlock">True when the header ended with <c>{ }</c>, an empty type.</param>
public sealed record TypeHeader(
    TypeAttributes Attributes,
    TypeKind Kind,
    bool KindFromWord,
    TypeLayoutKind Layout,
    string Namespace,
    string Name,
    IReadOnlyList<GenericParameterSpec> GenericParameters,
    string? BaseTypeText,
    IReadOnlyList<string> InterfaceTexts,
    bool OpensBlock,
    bool ClosesBlock)
{
    /// <summary>
    /// True when the arity suffix was written on the header rather than added from the parameter count.
    /// </summary>
    public bool ArityWritten { get; init; }
}
