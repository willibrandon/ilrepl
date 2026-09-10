namespace IlRepl.Engine.Binding;

/// <summary>
/// The shape of a <see cref="TypeSymbol"/>.
/// </summary>
public enum TypeSymbolKind
{
    /// <summary>
    /// A CIL primitive named by its keyword: <c>int32</c>, <c>string</c>, <c>object</c>, <c>void</c>.
    /// </summary>
    Primitive,

    /// <summary>
    /// A type definition, generic or not, named by its <see cref="DefinitionId"/>.
    /// </summary>
    Named,

    /// <summary>
    /// A generic type definition instantiated with arguments.
    /// </summary>
    Constructed,

    /// <summary>
    /// A generic parameter of a type, <c>!N</c>.
    /// </summary>
    TypeParameter,

    /// <summary>
    /// A generic parameter of a method, <c>!!N</c>.
    /// </summary>
    MethodParameter,

    /// <summary>
    /// A vector, <c>T[]</c>.
    /// </summary>
    SzArray,

    /// <summary>
    /// A general array with a rank, <c>T[,]</c> or <c>T[0...]</c>.
    /// </summary>
    Array,

    /// <summary>
    /// A managed pointer, <c>T&amp;</c>.
    /// </summary>
    ByRef,

    /// <summary>
    /// An unmanaged pointer, <c>T*</c>.
    /// </summary>
    Pointer,

    /// <summary>
    /// A function pointer with a signature.
    /// </summary>
    FunctionPointer,

    /// <summary>
    /// A type carrying a custom modifier.
    /// </summary>
    Modified,

    /// <summary>
    /// A pinned local type.
    /// </summary>
    Pinned,

    /// <summary>
    /// Represents an unresolved metadata type reference while preserving its original spelling.
    /// </summary>
    Unresolved,
}
