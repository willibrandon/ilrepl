using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Builds a real vararg method with a native binding that requires the external-original comparison path.
/// </summary>
internal static class ExternalVarArgFixture
{
    /// <summary>
    /// Counts optional arguments through a private delegate and retains an unreachable native binding in its metadata.
    /// </summary>
    /// <param name="module">The emitted assembly module.</param>
    /// <param name="owner">The public owner of the original vararg method.</param>
    internal static void Define(ModuleDefinition module, TypeDefinition owner)
    {
        VarArgEditAliasTests.DefineCounter(module, owner);
        var callback = new TypeDefinition("N", "CounterCallback", TypeAttributes.NotPublic | TypeAttributes.Sealed,
            module.ImportReference(typeof(MulticastDelegate)));
        module.Types.Add(callback);
        var constructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName
            | MethodAttributes.RTSpecialName, module.TypeSystem.Void) { ImplAttributes = MethodImplAttributes.Runtime };
        constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
        callback.Methods.Add(constructor);
        var invoke = new MethodDefinition("Invoke", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot,
            module.TypeSystem.Int32) { ImplAttributes = MethodImplAttributes.Runtime };
        invoke.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        callback.Methods.Add(invoke);
        var read = owner.Methods.Single();
        var result = new VariableDefinition(module.TypeSystem.Int32);
        read.Body.Variables.Add(result);
        var il = read.Body.GetILProcessor();
        var end = read.Body.Instructions.Last();
        il.InsertBefore(end, il.Create(OpCodes.Stloc, result));
        il.InsertBefore(end, il.Create(OpCodes.Ldnull));
        il.InsertBefore(end, il.Create(OpCodes.Ldftn, module.ImportReference(typeof(Math).GetMethod(nameof(Math.Abs), [typeof(int)])!)));
        il.InsertBefore(end, il.Create(OpCodes.Newobj, constructor));
        il.InsertBefore(end, il.Create(OpCodes.Ldloc, result));
        il.InsertBefore(end, il.Create(OpCodes.Callvirt, invoke));
        var native = new MethodDefinition("Native", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void)
        {
            ImplAttributes = MethodImplAttributes.InternalCall,
        };

        owner.Methods.Add(native);
        il.Emit(OpCodes.Call, native);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }
}
