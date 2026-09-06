using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// A method reference after resolution: the method itself and, for vararg call sites, the
/// types written after the <c>...</c> sentinel that the caller pushes as extra arguments.
/// </summary>
/// <param name="Method">The resolved method or constructor.</param>
/// <param name="OptionalParameterTypes">The vararg call-site parameter types, or null for a normal call.</param>
public sealed record ResolvedMethod(MethodBase Method, Type[]? OptionalParameterTypes)
{
    /// <summary>
    /// The number of values the call site pops for arguments: declared parameters, extra vararg
    /// parameters, and the <c>this</c> reference for instance methods (constructors excluded).
    /// </summary>
    /// <param name="isNewObj">True when the call site is <c>newobj</c>, which does not pop <c>this</c>.</param>
    /// <returns>The pop count.</returns>
    public int ArgumentPopCount(bool isNewObj)
    {
        var count = Method.GetParameters().Length + (OptionalParameterTypes?.Length ?? 0);
        if (!isNewObj && !Method.IsStatic)
        {
            count++;
        }

        return count;
    }
}
