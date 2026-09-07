namespace IlRepl.Engine;

/// <summary>
/// What kind of type a <c>.class</c> block declares, decided the way ILAsm decides it: by the
/// <c>interface</c> word or the base type.
/// </summary>
public enum TypeKind
{
    /// <summary>
    /// A reference type extending <c>System.Object</c> or another class.
    /// </summary>
    Class,

    /// <summary>
    /// A value type: <c>extends System.ValueType</c>, or the <c>value</c> word.
    /// </summary>
    Struct,

    /// <summary>
    /// An interface: the <c>interface</c> word, no base type.
    /// </summary>
    Interface,

    /// <summary>
    /// An enum: <c>extends System.Enum</c>, or the <c>enum</c> word.
    /// </summary>
    Enum,
}
