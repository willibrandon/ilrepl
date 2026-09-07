using System.Reflection;
using System.Runtime.InteropServices;

namespace IlRepl.Engine;

/// <summary>
/// The signature written after <c>calli</c>: managed or unmanaged calling convention, return
/// type, parameter types, and any vararg call-site parameters.
/// </summary>
/// <param name="IsUnmanaged">True for an unmanaged calling convention.</param>
/// <param name="UnmanagedConvention">The unmanaged convention when <paramref name="IsUnmanaged"/> is true.</param>
/// <param name="ManagedConvention">The managed convention flags, including <c>HasThis</c> and <c>VarArgs</c>.</param>
/// <param name="ReturnType">The return type.</param>
/// <param name="ParameterTypes">The fixed parameter types.</param>
/// <param name="OptionalParameterTypes">The parameter types after <c>...</c>, or null.</param>
public sealed record CalliSignature(
    bool IsUnmanaged,
    CallingConvention UnmanagedConvention,
    CallingConventions ManagedConvention,
    Type ReturnType,
    Type[] ParameterTypes,
    Type[]? OptionalParameterTypes)
{
    /// <summary>
    /// The number of values popped for arguments, including <c>this</c> for instance signatures.
    /// Under <c>instance explicit</c> the receiver is already the first parameter (ECMA-335
    /// II.15.3), so it is not counted twice. The function pointer itself is popped in addition.
    /// </summary>
    public int ArgumentPopCount =>
        ParameterTypes.Length + (OptionalParameterTypes?.Length ?? 0)
        + ((ManagedConvention & CallingConventions.HasThis) != 0 && (ManagedConvention & CallingConventions.ExplicitThis) == 0 ? 1 : 0);
}
