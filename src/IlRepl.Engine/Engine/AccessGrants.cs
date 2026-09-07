using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Puts <c>IgnoresAccessChecksTo</c> on a dynamic assembly for the session assemblies it
/// references, defining the attribute type in the assembly itself as the runtime expects.
/// </summary>
public static class AccessGrants
{
    /// <summary>
    /// Grants the assembly access to every member of the given session assemblies.
    /// </summary>
    /// <param name="assembly">The dynamic assembly.</param>
    /// <param name="module">Its module, where the attribute type is defined.</param>
    /// <param name="dependencies">The session assemblies.</param>
    public static void Grant(AssemblyBuilder assembly, ModuleBuilder module, IReadOnlyList<DefinitionAssembly> dependencies)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(dependencies);
        if (dependencies.Count == 0)
        {
            return;
        }

        var attribute = module.DefineType("System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed, typeof(Attribute));
        var ctor = attribute.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, CallingConventions.Standard, [typeof(string)]);
        var il = ctor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);
        attribute.CreateType();
        foreach (var dependency in dependencies.Distinct())
        {
            assembly.SetCustomAttribute(new CustomAttributeBuilder(ctor, [dependency.Name]));
        }
    }
}
