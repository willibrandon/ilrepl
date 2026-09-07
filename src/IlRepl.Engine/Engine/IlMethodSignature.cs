using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;

namespace IlRepl.Engine;

/// <summary>
/// A method signature as metadata encodes it (ECMA-335 II.23.2.1 to II.23.2.3): calling
/// convention bits, generic arity, return type, parameters, and where the vararg sentinel sits.
/// Used for <c>calli</c> operands, function pointer types, member references, and method headers.
/// </summary>
/// <param name="Convention">The calling convention: default, vararg, or an unmanaged one.</param>
/// <param name="HasThis">True for an instance signature.</param>
/// <param name="ExplicitThis">True when <c>this</c> is spelled out as the first parameter.</param>
/// <param name="GenericParameterCount">The generic arity of a method definition.</param>
/// <param name="ReturnType">The return type, modifiers included.</param>
/// <param name="Parameters">Every parameter, the ones after the sentinel included.</param>
/// <param name="RequiredParameterCount">How many parameters come before the sentinel; equal to the count when there is none.</param>
public sealed record IlMethodSignature(
    SignatureCallingConvention Convention,
    bool HasThis,
    bool ExplicitThis,
    int GenericParameterCount,
    IlSignature ReturnType,
    IReadOnlyList<IlSignature> Parameters,
    int RequiredParameterCount)
{
    /// <summary>
    /// True for a vararg signature.
    /// </summary>
    public bool IsVarArg => Convention == SignatureCallingConvention.VarArgs;

    /// <summary>
    /// True for an unmanaged calling convention, whether a specific one or the modifier-based <c>unmanaged</c>.
    /// </summary>
    public bool IsUnmanaged => Convention is SignatureCallingConvention.CDecl or SignatureCallingConvention.StdCall
        or SignatureCallingConvention.ThisCall or SignatureCallingConvention.FastCall or SignatureCallingConvention.Unmanaged;

    /// <summary>
    /// The parameters before the sentinel.
    /// </summary>
    public IEnumerable<IlSignature> FixedParameters => Parameters.Take(RequiredParameterCount);

    /// <summary>
    /// The parameters after the sentinel, or null when there is none.
    /// </summary>
    public IReadOnlyList<IlSignature>? OptionalParameters => RequiredParameterCount < Parameters.Count ? Parameters.Skip(RequiredParameterCount).ToArray() : null;

    /// <summary>
    /// The signature the stack simulator works with: projected types, and <c>this</c> counted once
    /// whether it is implicit or spelled out.
    /// </summary>
    /// <returns>The calli signature, or null when a parameter or the return type did not resolve.</returns>
    public CalliSignature? ToCalliSignature()
    {
        var returnType = ReturnType.ToClrType();
        var fixedTypes = FixedParameters.Select(p => p.ToClrType()).ToArray();
        var optional = OptionalParameters?.Select(p => p.ToClrType()).ToArray();
        if (returnType is null || fixedTypes.Any(t => t is null) || (optional is not null && optional.Any(t => t is null)))
        {
            return null;
        }

        var managed = IsVarArg ? CallingConventions.VarArgs : CallingConventions.Standard;
        if (HasThis)
        {
            managed |= CallingConventions.HasThis;
        }

        if (ExplicitThis)
        {
            managed |= CallingConventions.ExplicitThis;
        }

        var unmanaged = Convention switch
        {
            SignatureCallingConvention.CDecl => CallingConvention.Cdecl,
            SignatureCallingConvention.StdCall => CallingConvention.StdCall,
            SignatureCallingConvention.ThisCall => CallingConvention.ThisCall,
            SignatureCallingConvention.FastCall => CallingConvention.FastCall,
            _ => CallingConvention.Winapi,
        };
        return new CalliSignature(IsUnmanaged, unmanaged, managed, returnType, fixedTypes!, optional!);
    }

    /// <summary>
    /// Builds the signature of a runtime function pointer type.
    /// </summary>
    /// <param name="type">A type for which <see cref="Type.IsFunctionPointer"/> is true.</param>
    /// <returns>The signature; unmanaged calling conventions appear as modifiers on the return type, as the runtime encodes them.</returns>
    public static IlMethodSignature FromFunctionPointer(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var returnType = IlSignature.FromType(type.GetFunctionPointerReturnType(), optionalModifiers: type.GetFunctionPointerCallingConventions());
        var parameters = type.GetFunctionPointerParameterTypes().Select(p => IlSignature.FromType(p)).ToArray();
        var convention = type.IsUnmanagedFunctionPointer ? SignatureCallingConvention.Unmanaged : SignatureCallingConvention.Default;
        return new IlMethodSignature(convention, false, false, 0, returnType, parameters, parameters.Length);
    }

    /// <inheritdoc/>
    public override string ToString() => IlSignatureRenderer.IlAsm(this);
}
