namespace IlRepl.Engine.Binding;

/// <summary>
/// The kind of a type as a listing or a palette presents it. Eligibility decisions use the full
/// symbol facts; the kind only orders and labels.
/// </summary>
public enum TypeIndexKind
{
    /// <summary>
    /// A CIL primitive.
    /// </summary>
    Primitive,

    /// <summary>
    /// A class that is none of the more specific kinds.
    /// </summary>
    Class,

    /// <summary>
    /// A value type other than an enum.
    /// </summary>
    Struct,

    /// <summary>
    /// An interface.
    /// </summary>
    Interface,

    /// <summary>
    /// An enum.
    /// </summary>
    Enum,

    /// <summary>
    /// A delegate type.
    /// </summary>
    Delegate,

    /// <summary>
    /// A generic parameter in scope.
    /// </summary>
    GenericParameter,
}
