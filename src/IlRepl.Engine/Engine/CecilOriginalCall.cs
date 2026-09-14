using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using MethodBody = Mono.Cecil.Cil.MethodBody;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Engine;

/// <summary>
/// Connects comparisons to original methods whose runtime-owned contexts cannot be copied.
/// </summary>
internal static class CecilOriginalCall
{
    /// <summary>
    /// Observes a direct original call through the same typed boundaries as an edited method.
    /// </summary>
    /// <param name="original">The original method with its closed type and method arguments.</param>
    /// <param name="writer">The writer for the standalone observation assembly.</param>
    /// <returns>The instrumented entry point.</returns>
    internal static MethodDefinition Wrap(MethodBase original, CecilWriter writer)
    {
        var owner = writer.DefineType("IlRepl", "Original", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
            writer.Object);
        var returnType = original is MethodInfo method ? writer.Import(method.ReturnType) : writer.Module.TypeSystem.Void;
        var target = new MethodDefinition("Invoke", MethodAttributes.Public | MethodAttributes.Static, returnType);
        owner.Methods.Add(target);
        foreach (var parameter in original.GetParameters())
        {
            target.Parameters.Add(new ParameterDefinition(parameter.Name, (ParameterAttributes)parameter.Attributes,
                writer.Import(parameter.ParameterType)));
        }

        var il = target.Body.GetILProcessor();
        foreach (var parameter in target.Parameters)
        {
            il.Emit(OpCodes.Ldarg, parameter);
        }

        il.Emit(OpCodes.Call, writer.Import(original));
        il.Emit(OpCodes.Ret);
        writer.GrantAccessTo(original.Module.Assembly.GetName().Name!);
        return ComparisonInstrumentation.Wrap(writer, target);
    }

    internal static void Replace(MethodEdit edit, MethodDefinition selected, CecilWriter writer)
    {
        var original = edit.Original.Method;
        if (!original.IsPublic || !original.IsStatic || !original.DeclaringType!.IsVisible)
        {
            throw new ReplException("the original context cannot be reproduced for this scenario: "
                + string.Join("; ", edit.Baseline.Problems));
        }

        edit.RequireScenarioSignature();
        selected.Body = new MethodBody(selected);
        var il = selected.Body.GetILProcessor();
        foreach (var parameter in selected.Parameters)
        {
            il.Emit(OpCodes.Ldarg, parameter);
        }

        // Cecil imports a generic declaring type as an instance over !0, !1, etc., which bind to the copied owner's parameters.
        var call = writer.Module.ImportReference(original);
        if (call.HasGenericParameters)
        {
            var constructed = new GenericInstanceMethod(call);
            foreach (var parameter in selected.GenericParameters)
            {
                constructed.GenericArguments.Add(parameter);
            }

            call = constructed;
        }

        il.Emit(OpCodes.Call, call);
        il.Emit(OpCodes.Ret);
    }
}
