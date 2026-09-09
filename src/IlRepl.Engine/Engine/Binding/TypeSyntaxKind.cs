namespace IlRepl.Engine.Binding;

/// <summary>
/// The shape a <see cref="TypeSyntax"/> node describes, before anything is looked up.
/// </summary>
public enum TypeSyntaxKind
{
    /// <summary>
    /// A primitive keyword: <c>int32</c>, <c>string</c>, <c>native int</c>, or a C# spelling of one.
    /// </summary>
    Primitive,

    /// <summary>
    /// A named type, with an optional <c>[assembly]</c> hint and optional generic arguments.
    /// </summary>
    Named,

    /// <summary>
    /// A type generic parameter, <c>!N</c> or <c>!Name</c>.
    /// </summary>
    TypeParameter,

    /// <summary>
    /// A method generic parameter, <c>!!N</c> or <c>!!Name</c>.
    /// </summary>
    MethodParameter,

    /// <summary>
    /// An array of its element: a vector <c>T[]</c> or a general array <c>T[,]</c>, <c>T[0...]</c>.
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
    /// A function pointer, <c>method RetType *(Params)</c>.
    /// </summary>
    FunctionPointer,

    /// <summary>
    /// A type with a custom modifier written after it: <c>T modreq(M)</c> or <c>T modopt(M)</c>.
    /// </summary>
    Modified,

    /// <summary>
    /// A type written with <c>pinned</c> after it.
    /// </summary>
    Pinned,
}
