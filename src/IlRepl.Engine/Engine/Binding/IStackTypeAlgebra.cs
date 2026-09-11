namespace IlRepl.Engine.Binding;

/// <summary>
/// Supplies type operations and markers to the shared stack-transfer rules.
/// </summary>
/// <remarks>
/// The questions the stack transfer asks of a type representation, so the same per-opcode rules
/// run over runtime types for an actual line and over symbols for a preview of one. Markers stand
/// for what the model cannot type exactly: a null reference, an object reference it lost track of,
/// and a boxed value that remembers what it holds.
/// </remarks>
/// <typeparam name="T">The type representation.</typeparam>
public interface IStackTypeAlgebra<T> where T : class
{
    /// <summary>
    /// The type a primitive keyword names: <c>int32</c>, <c>native int</c>, <c>string</c>, and the rest.
    /// </summary>
    /// <param name="keyword">The canonical keyword.</param>
    /// <returns>The type.</returns>
    T Primitive(string keyword);

    /// <summary>
    /// A CoreLib type by its namespace-qualified name: the runtime handles, <c>System.TypedReference</c>.
    /// </summary>
    /// <param name="fullName">The name, such as <c>System.RuntimeTypeHandle</c>.</param>
    /// <returns>The type.</returns>
    T CoreLib(string fullName);

    /// <summary>
    /// The marker for a null reference.
    /// </summary>
    T NullReference { get; }

    /// <summary>
    /// The marker for an object reference the model could not type.
    /// </summary>
    T UnknownReference { get; }

    /// <summary>
    /// A boxed value that remembers its value type.
    /// </summary>
    /// <param name="valueType">The value type, nullable already unwrapped.</param>
    /// <returns>The boxed marker.</returns>
    T Boxed(T valueType);

    /// <summary>
    /// The value type under a nullable, or null when the type is not <c>Nullable&lt;T&gt;</c>.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The underlying type, or null.</returns>
    T? NullableUnderlying(T type);

    /// <summary>
    /// A managed pointer to the type.
    /// </summary>
    /// <param name="type">The pointee.</param>
    /// <returns>The byref.</returns>
    T MakeByRef(T type);

    /// <summary>
    /// An unmanaged pointer to the type.
    /// </summary>
    /// <param name="type">The pointee.</param>
    /// <returns>The pointer.</returns>
    T MakePointer(T type);

    /// <summary>
    /// A vector of the type.
    /// </summary>
    /// <param name="type">The element.</param>
    /// <returns>The array.</returns>
    T MakeArray(T type);

    /// <summary>
    /// The element of a byref, pointer, or array, or null for anything else.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The element, or null.</returns>
    T? ElementOf(T type);

    /// <summary>
    /// True for a managed pointer.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>True for a byref.</returns>
    bool IsByRef(T type);

    /// <summary>
    /// True for an unmanaged pointer.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>True for a pointer.</returns>
    bool IsPointer(T type);

    /// <summary>
    /// True for an array of any rank.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>True for an array.</returns>
    bool IsArray(T type);

    /// <summary>
    /// True for a value type.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>True for a value type.</returns>
    bool IsValueType(T type);

    /// <summary>
    /// True for a type that cannot be boxed because it may contain managed stack references.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>True for a byref-like type or parameter.</returns>
    bool IsByRefLike(T type);

    /// <summary>
    /// True for a generic parameter.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>True for a parameter.</returns>
    bool IsGenericParameter(T type);

    /// <summary>
    /// True when two representations are the same type.
    /// </summary>
    /// <param name="a">The first.</param>
    /// <param name="b">The second.</param>
    /// <returns>True when they match.</returns>
    bool Same(T? a, T? b);
}
