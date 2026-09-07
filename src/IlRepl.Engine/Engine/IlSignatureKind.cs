namespace IlRepl.Engine;

/// <summary>
/// The shapes a type takes inside a metadata signature (ECMA-335 II.23.2).
/// </summary>
public enum IlSignatureKind
{
    /// <summary>
    /// A built-in type with a keyword: <c>int32</c>, <c>string</c>, <c>object</c>, <c>native int</c>, <c>void</c>.
    /// </summary>
    Primitive,

    /// <summary>
    /// A class or value type named by a TypeDef or TypeRef.
    /// </summary>
    Named,

    /// <summary>
    /// A generic type instantiated with arguments.
    /// </summary>
    GenericInstance,

    /// <summary>
    /// A vector: <c>T[]</c>.
    /// </summary>
    SzArray,

    /// <summary>
    /// A general array with a rank, sizes, and lower bounds: <c>T[0...,0...]</c>, <c>T[3]</c>.
    /// </summary>
    Array,

    /// <summary>
    /// A managed pointer: <c>T&amp;</c>.
    /// </summary>
    ByRef,

    /// <summary>
    /// An unmanaged pointer: <c>T*</c>.
    /// </summary>
    Pointer,

    /// <summary>
    /// A function pointer with a full method signature: <c>method int32 *(int32)</c>.
    /// </summary>
    FunctionPointer,

    /// <summary>
    /// A type with a required or optional custom modifier: <c>int32 modreq(IsVolatile)</c>.
    /// </summary>
    Modified,

    /// <summary>
    /// A pinned local: <c>int32&amp; pinned</c>.
    /// </summary>
    Pinned,

    /// <summary>
    /// A generic parameter of the declaring type: <c>!0</c>.
    /// </summary>
    TypeParameter,

    /// <summary>
    /// A generic parameter of the method: <c>!!0</c>.
    /// </summary>
    MethodParameter,

    /// <summary>
    /// The vararg sentinel: <c>...</c>.
    /// </summary>
    Sentinel,
}
