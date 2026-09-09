using System.Reflection;
using System.Runtime.InteropServices;

namespace IlRepl.Engine.Binding;

/// <summary>
/// A standalone method signature: what a function pointer type carries and what <c>calli</c> takes.
/// </summary>
/// <param name="ManagedConvention">The managed calling convention: <c>instance</c>, <c>explicit</c>, <c>vararg</c>.</param>
/// <param name="IsUnmanaged">True for an unmanaged signature.</param>
/// <param name="UnmanagedConvention">The unmanaged calling convention when <paramref name="IsUnmanaged"/> is set.</param>
/// <param name="ReturnType">The return type.</param>
/// <param name="Parameters">The parameter types, the vararg sentinel excluded.</param>
/// <param name="SentinelIndex">The index in <paramref name="Parameters"/> before which <c>...</c> stands, or null.</param>
public sealed record MethodSignatureSymbol(
    CallingConventions ManagedConvention,
    bool IsUnmanaged,
    CallingConvention UnmanagedConvention,
    TypeSymbol ReturnType,
    IReadOnlyList<TypeSymbol> Parameters,
    int? SentinelIndex)
{
    /// <summary>
    /// The parameters before the sentinel, or all of them.
    /// </summary>
    public IReadOnlyList<TypeSymbol> FixedParameters => SentinelIndex is int s ? [.. Parameters.Take(s)] : Parameters;

    /// <summary>
    /// The parameters after the sentinel, or null when there is none.
    /// </summary>
    public IReadOnlyList<TypeSymbol>? OptionalParameters => SentinelIndex is int s ? [.. Parameters.Skip(s)] : null;

    /// <summary>
    /// True when the signature takes a <c>this</c>.
    /// </summary>
    public bool HasThis => (ManagedConvention & CallingConventions.HasThis) != 0;

    /// <summary>
    /// True when the <c>this</c> is written as the first parameter.
    /// </summary>
    public bool ExplicitThis => (ManagedConvention & CallingConventions.ExplicitThis) != 0;

    /// <summary>
    /// True for a vararg signature.
    /// </summary>
    public bool IsVarArg => (ManagedConvention & CallingConventions.VarArgs) != 0;

    /// <summary>
    /// Returns the argument count consumed by a call, including an implicit instance receiver.
    /// </summary>
    /// <remarks>
    /// How many values a call through this signature pops for its arguments: the parameters, and
    /// the receiver of an instance signature that does not name it as a parameter.
    /// </remarks>
    public int ArgumentPopCount => Parameters.Count + (HasThis && !ExplicitThis ? 1 : 0);
}
