using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// The stack type algebra over runtime types, with <see cref="NullReferenceMarker"/>,
/// <see cref="UnknownReferenceMarker"/>, and <see cref="Boxed{T}"/> as its markers.
/// </summary>
public sealed class RuntimeStackAlgebra : IStackTypeAlgebra<Type>
{
    /// <summary>
    /// The one instance.
    /// </summary>
    public static RuntimeStackAlgebra Instance { get; } = new();

    /// <summary>
    /// The transfer over this algebra.
    /// </summary>
    public static StackTransfer<Type> Transfer { get; } = new(Instance);

    /// <inheritdoc/>
    public Type Primitive(string keyword) => CilPrimitives.TypeOf(keyword);

    /// <inheritdoc/>
    public Type CoreLib(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        return fullName switch
        {
            "System.RuntimeTypeHandle" => typeof(RuntimeTypeHandle),
            "System.RuntimeFieldHandle" => typeof(RuntimeFieldHandle),
            "System.RuntimeMethodHandle" => typeof(RuntimeMethodHandle),
            "System.RuntimeArgumentHandle" => typeof(RuntimeArgumentHandle),
            _ => throw new ArgumentException($"'{fullName}' is not a type the stack model names", nameof(fullName)),
        };
    }

    /// <inheritdoc/>
    public Type NullReference => typeof(NullReferenceMarker);

    /// <inheritdoc/>
    public Type UnknownReference => typeof(UnknownReferenceMarker);

    /// <inheritdoc/>
    public Type Boxed(Type valueType) => typeof(Boxed<>).MakeGenericType(valueType);

    /// <inheritdoc/>
    public Type? NullableUnderlying(Type type) => Nullable.GetUnderlyingType(type);

    /// <inheritdoc/>
    public Type MakeByRef(Type type) => type.MakeByRefType();

    /// <inheritdoc/>
    public Type MakeArray(Type type) => type.MakeArrayType();

    /// <inheritdoc/>
    public Type? ElementOf(Type type) => type.HasElementType ? type.GetElementType() : null;

    /// <inheritdoc/>
    public bool IsByRef(Type type) => type.IsByRef;

    /// <inheritdoc/>
    public bool IsPointer(Type type) => type.IsPointer;

    /// <inheritdoc/>
    public bool IsArray(Type type) => type.IsArray;

    /// <inheritdoc/>
    public bool IsValueType(Type type) => type.IsValueType;

    /// <inheritdoc/>
    public bool IsGenericParameter(Type type) => type.IsGenericParameter;

    /// <inheritdoc/>
    public bool Same(Type? a, Type? b) => a == b;
}
