using System.Globalization;
using System.Reflection;
using System.Runtime.Remoting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilMethodAttributes = Mono.Cecil.MethodAttributes;

namespace IlRepl.Engine;

/// <summary>
/// Preserves string-based activation of renamed types while retaining constructor binding and runtime errors.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private static bool IsActivation(MethodBase method) => method.DeclaringType == typeof(Activator)
        && method.Name is nameof(Activator.CreateInstance) or nameof(Activator.CreateInstanceFrom)
        && method.GetParameters() is { Length: >= 2 } parameters
        && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(string);

    private MethodDefinition WriteActivation(CecilWriter writer, TypeDefinition owner, Assembly context,
        MethodReference target, int index)
    {
        var wrapper = new MethodDefinition("Activate" + index, CecilMethodAttributes.Assembly | CecilMethodAttributes.Static,
            target.ReturnType);
        owner.Methods.Add(wrapper);
        foreach (var parameter in target.Parameters) wrapper.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        var translated = new VariableDefinition(writer.Module.TypeSystem.String);
        wrapper.Body.Variables.Add(translated);
        var il = wrapper.Body.GetILProcessor();
        var fromFile = target.Name == nameof(Activator.CreateInstanceFrom);
        il.Emit(OpCodes.Ldarg_0);
        if (fromFile)
        {
            il.Emit(OpCodes.Call, writer.Import(typeof(Assembly).GetMethod(nameof(Assembly.LoadFrom), [typeof(string)])!));
            il.Emit(OpCodes.Callvirt, writer.Import(typeof(Assembly).GetProperty(nameof(Assembly.FullName))!.GetMethod!));
        }

        il.Emit(OpCodes.Ldarg_1);
        if (target.Parameters.Count == 8) il.Emit(OpCodes.Ldarg_2);
        else il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldstr, context.FullName!);
        WriteTypeLookupNames(writer, il);
        il.Emit(OpCodes.Call, writer.Import(typeof(CopiedTypeNames).GetMethod(nameof(CopiedTypeNames.TranslateActivation),
            BindingFlags.Static | BindingFlags.NonPublic)!));
        il.Emit(OpCodes.Stloc, translated);
        il.Emit(OpCodes.Ldloc, translated);
        var original = il.Create(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, original);
        il.Emit(OpCodes.Ldloc, translated);
        il.Emit(OpCodes.Ldc_I4_1);
        if (target.Parameters.Count == 8) il.Emit(OpCodes.Ldarg_2);
        else il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Call, writer.Import(typeof(Type).GetMethod(nameof(Type.GetType), [typeof(string), typeof(bool), typeof(bool)])!));
        if (target.Parameters.Count == 8)
        {
            foreach (var parameter in wrapper.Parameters.Skip(3)) il.Emit(OpCodes.Ldarg, parameter);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.Instance | BindingFlags.Public | BindingFlags.CreateInstance));
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldnull);
            if (target.Parameters.Count == 3) il.Emit(OpCodes.Ldarg_2);
            else il.Emit(OpCodes.Ldnull);
        }

        il.Emit(OpCodes.Call, writer.Import(typeof(Activator).GetMethod(nameof(Activator.CreateInstance),
            [typeof(Type), typeof(BindingFlags), typeof(Binder), typeof(object[]), typeof(CultureInfo), typeof(object[])])!));
        il.Emit(OpCodes.Dup);
        var wrap = il.Create(OpCodes.Newobj, writer.Import(typeof(ObjectHandle).GetConstructor([typeof(object)])!));
        il.Emit(OpCodes.Brtrue, wrap);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        il.Append(wrap);
        il.Emit(OpCodes.Ret);
        il.Append(original);
        if (!fromFile)
        {
            var suppliedAssembly = il.Create(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brtrue, suppliedAssembly);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldstr, context.FullName!);
            il.Append(suppliedAssembly);
        }
        else il.Emit(OpCodes.Ldarg_1);

        foreach (var parameter in wrapper.Parameters.Skip(2)) il.Emit(OpCodes.Ldarg, parameter);
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
        return wrapper;
    }
}
