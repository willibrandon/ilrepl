using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// A method reference resolved at a call site: either a framework method or constructor, with
/// the optional parameter types of a vararg call, or a method defined in the session with
/// <c>.method</c>, which is bound to a builder only when the cell is emitted. Nothing here
/// reflects over a builder, because a <see cref="System.Reflection.Emit.MethodBuilder"/> cannot
/// describe its parameters before its type is created.
/// </summary>
public sealed record ResolvedMethod
{
    /// <summary>
    /// Initializes a reference to a framework method or constructor.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <param name="optionalParameterTypes">The types after <c>...</c> in a vararg call, or null.</param>
    public ResolvedMethod(MethodBase method, Type[]? optionalParameterTypes)
    {
        ArgumentNullException.ThrowIfNull(method);
        Method = method;
        OptionalParameterTypes = optionalParameterTypes;
    }

    /// <summary>
    /// Initializes a reference to a session method.
    /// </summary>
    /// <param name="definition">The signature the session holds for the method.</param>
    public ResolvedMethod(MethodSignature definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
    }

    /// <summary>
    /// The framework method, or null for a session method.
    /// </summary>
    public MethodBase? Method { get; }

    /// <summary>
    /// The session method's signature, or null for a framework method.
    /// </summary>
    public MethodSignature? Definition { get; }

    /// <summary>
    /// The types after <c>...</c> in a vararg call site, or null when the call is not vararg.
    /// </summary>
    public Type[]? OptionalParameterTypes { get; }

    /// <summary>
    /// True when the reference names a method defined with <c>.method</c>.
    /// </summary>
    public bool IsSessionMethod => Definition is not null;

    /// <summary>
    /// True when the reference names a constructor.
    /// </summary>
    public bool IsConstructor => Method is ConstructorInfo;

    /// <summary>
    /// True when the method takes no <c>this</c>. Session methods are always static.
    /// </summary>
    public bool IsStatic => Definition is not null || Method!.IsStatic;

    /// <summary>
    /// True when the method uses the vararg calling convention.
    /// </summary>
    public bool IsVarArg => Method is not null && Method.CallingConvention.HasFlag(CallingConventions.VarArgs);

    /// <summary>
    /// The method name; <c>.ctor</c> or <c>.cctor</c> for constructors.
    /// </summary>
    public string Name => Definition?.Name ?? (Method is ConstructorInfo ? (Method.IsStatic ? ".cctor" : ".ctor") : Method!.Name);

    /// <summary>
    /// The return type; <c>void</c> for constructors.
    /// </summary>
    public Type ReturnType => Definition?.ReturnType ?? (Method is MethodInfo mi ? mi.ReturnType : typeof(void));

    /// <summary>
    /// The fixed parameter types in order.
    /// </summary>
    public IReadOnlyList<Type> ParameterTypes => Definition?.ParameterTypes ?? Method!.GetParameters().Select(p => p.ParameterType).ToArray();

    /// <summary>
    /// The declaring type of a framework method, or null for a session method.
    /// </summary>
    public Type? DeclaringType => Method?.DeclaringType;

    /// <summary>
    /// The declaring type's display name; <c>IlRepl.Cell</c> for a session method.
    /// </summary>
    public string DeclaringTypeName => Definition is not null ? "IlRepl.Cell" : TypeNameFormatter.Pretty(Method!.DeclaringType);

    /// <summary>
    /// How many values a call pops: the fixed and optional parameters, plus the receiver for an
    /// instance call that is not <c>newobj</c>.
    /// </summary>
    /// <param name="isNewObj">True when the call site is <c>newobj</c>, which pushes the receiver itself.</param>
    /// <returns>The pop count.</returns>
    public int ArgumentPopCount(bool isNewObj)
    {
        var count = ParameterTypes.Count + (OptionalParameterTypes?.Length ?? 0);
        if (!isNewObj && !IsStatic)
        {
            count++;
        }

        return count;
    }
}
