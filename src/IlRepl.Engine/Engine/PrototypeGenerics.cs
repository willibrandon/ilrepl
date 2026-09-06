using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Creates generic method parameters that stand in for a cell's <c>.typeparams</c> while lines
/// are validated. The real parameters are created again on the method that is compiled.
/// </summary>
public static class PrototypeGenerics
{
    private static int s_counter;

    /// <summary>
    /// Creates generic parameter types with the given names on a throwaway method.
    /// </summary>
    /// <param name="names">The parameter names, in order.</param>
    /// <returns>The generic parameter types, or an empty array when there are no names.</returns>
    public static Type[] Create(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            return [];
        }

        var id = Interlocked.Increment(ref s_counter);
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ilrepl.prototype" + id), OperatingSystem.IsBrowser() ? AssemblyBuilderAccess.Run : AssemblyBuilderAccess.RunAndCollect);
        var module = assembly.DefineDynamicModule("prototype");
        var type = module.DefineType("Prototype", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var method = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static);
        return method.DefineGenericParameters([.. names]);
    }
}
