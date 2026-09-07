namespace IlRepl.Engine;

/// <summary>
/// The kinds of value the evaluation stack distinguishes, following ECMA-335 III.1.1. Smaller
/// integers widen to <see cref="Int32"/> on the stack, and every floating type is <see cref="Float"/>.
/// </summary>
public enum StackCategory
{
    /// <summary>
    /// int32 and everything that widens to it: bool, char, and the 8- and 16-bit integers.
    /// </summary>
    Int32,

    /// <summary>
    /// int64 and uint64.
    /// </summary>
    Int64,

    /// <summary>
    /// native int, native unsigned int, and unmanaged pointers.
    /// </summary>
    NativeInt,

    /// <summary>
    /// float32 and float64.
    /// </summary>
    Float,

    /// <summary>
    /// A managed pointer.
    /// </summary>
    ByRef,

    /// <summary>
    /// An object reference.
    /// </summary>
    ObjectReference,

    /// <summary>
    /// A value type that is none of the above.
    /// </summary>
    ValueType,
}
