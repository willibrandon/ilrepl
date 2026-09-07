using System.Reflection;
using System.Runtime.CompilerServices;

namespace IlRepl.Engine;

/// <summary>
/// Asks the JIT to compile a method without running it, so IL the runtime would reject is
/// reported when a <c>.method</c> block closes rather than at the first call.
/// </summary>
/// <remarks>
/// The browser build runs on Mono's interpreter, where <c>RuntimeHelpers.PrepareMethod</c>
/// checks its arguments and returns without preparing anything (the icall in
/// <c>src/mono/mono/metadata/icall.c</c> ends in a FIXME). <see cref="IsSupported"/> is false
/// there, the emission check still runs, and a JIT-level rejection waits for the first call.
/// </remarks>
public static class MethodPreparation
{
    /// <summary>
    /// True when the runtime compiles a method on request, so preparation can catch invalid IL.
    /// </summary>
    public static bool IsSupported => !OperatingSystem.IsBrowser();

    /// <summary>
    /// Compiles the method. Never invokes it.
    /// </summary>
    /// <param name="method">A method on a created type.</param>
    /// <exception cref="InvalidProgramException">The JIT rejected the method's IL.</exception>
    public static void Prepare(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        RuntimeHelpers.PrepareMethod(method.MethodHandle);
    }
}
