using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace IlRepl.Engine;

/// <summary>
/// Connects a comparison scenario to an accessible original when its runtime-owned context cannot be copied.
/// </summary>
internal static class CecilOriginalCall
{
    internal static void Replace(MethodEdit edit, MethodDefinition selected, CecilWriter writer)
    {
        var original = edit.Original.Method;
        if (!original.IsPublic || !original.IsStatic || !original.DeclaringType!.IsVisible)
        {
            throw new ReplException("the original context cannot be reproduced for this scenario: "
                + string.Join("; ", edit.Baseline.Problems));
        }

        var parameters = original.GetParameters();
        var current = edit.Method!.GetParameters();
        if (parameters.Length != current.Length || original is not MethodInfo info || edit.Method is not MethodInfo edited
            || !Same(info.ReturnType, edited.ReturnType)
            || parameters.Where((parameter, index) => !Same(parameter.ParameterType, current[index].ParameterType)).Any())
        {
            throw new ReplException("the original and edited signatures must match to compare this method through a scenario");
        }

        selected.Body = new MethodBody(selected);
        var il = selected.Body.GetILProcessor();
        foreach (var parameter in selected.Parameters)
        {
            il.Emit(OpCodes.Ldarg, parameter);
        }

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

        bool Same(Type before, Type after) => TypeNameFormatter.IlAsm(before)
            == edit.Current!.NormalizeNames(TypeNameFormatter.IlAsm(after));
    }
}
