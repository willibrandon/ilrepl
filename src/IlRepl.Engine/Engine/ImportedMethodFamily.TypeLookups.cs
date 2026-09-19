using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilMethodAttributes = Mono.Cecil.MethodAttributes;
using CecilTypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Engine;

/// <summary>
/// Preserves string-based lookup of renamed types in live copies and standalone assemblies.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private static bool IsTypeLookup(MethodBase method) => method.Name == nameof(Type.GetType)
        && method.GetParameters() is { Length: > 0 } parameters && parameters[0].ParameterType == typeof(string)
        && (method.IsStatic ? method.DeclaringType == typeof(Type)
            : method.DeclaringType == typeof(Assembly) || method.DeclaringType == typeof(Module));

    private static bool IsAssemblyActivation(MethodBase method) => !method.IsStatic && method.DeclaringType == typeof(Assembly)
        && method.Name == nameof(Assembly.CreateInstance)
        && method.GetParameters() is { Length: > 0 } parameters && parameters[0].ParameterType == typeof(string);

    private void WriteTypeLookups(CecilWriter writer, Dictionary<MemberInfo, IMemberDefinition> definitions)
    {
        TypeDefinition? owner = null;
        var wrappers = new Dictionary<(Assembly Context, string Signature, Code Call), MethodDefinition>();
        foreach (var (source, body) in _methods)
        {
            if (body is null)
            {
                continue;
            }

            var method = (MethodDefinition)definitions[source];
            foreach (var instruction in method.Body.Instructions.ToArray())
            {
                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt) || instruction.Operand is not MethodReference target
                    || target.Parameters.Count == 0
                    || target.Parameters[0].ParameterType.FullName != typeof(string).FullName
                    || target.DeclaringType.Scope is not AssemblyNameReference assembly
                    || assembly.FullName != typeof(Type).Assembly.FullName)
                {
                    continue;
                }

                var activation = !target.HasThis && target.DeclaringType.FullName == typeof(Activator).FullName
                    && target.Name is nameof(Activator.CreateInstance) or nameof(Activator.CreateInstanceFrom)
                    && target.Parameters.Count >= 2 && target.Parameters[1].ParameterType.FullName == typeof(string).FullName;
                var assemblyActivation = target.HasThis && target.DeclaringType.FullName == typeof(Assembly).FullName
                    && target.Name == nameof(Assembly.CreateInstance);
                if (!activation && !assemblyActivation && (target.Name != nameof(Type.GetType)
                    || (target.HasThis ? target.DeclaringType.FullName != typeof(Assembly).FullName
                            && target.DeclaringType.FullName != typeof(Module).FullName
                        : target.DeclaringType.FullName != typeof(Type).FullName)))
                {
                    continue;
                }

                owner ??= CreateTypeLookupOwner(writer);
                var key = (source.Module.Assembly, target.FullName, instruction.OpCode.Code);
                if (!wrappers.TryGetValue(key, out var wrapper))
                {
                    wrapper = activation ? WriteActivation(writer, owner, source.Module.Assembly, target, wrappers.Count)
                        : target.HasThis ? WriteScopedTypeLookup(writer, owner, target, instruction.OpCode, wrappers.Count)
                        : WriteTypeLookup(writer, owner, source.Module.Assembly, target, wrappers.Count);
                    wrappers.Add(key, wrapper);
                }

                if (target.HasThis)
                {
                    LoadConstrainedLookupReceiver(method, instruction, target);
                }

                instruction.OpCode = OpCodes.Call;
                instruction.Operand = wrapper;
            }
        }
    }

    private TypeDefinition CreateTypeLookupOwner(CecilWriter writer)
    {
        var owner = new TypeDefinition("IlRepl.Edits." + Name, "<TypeLookups>",
            CecilTypeAttributes.NotPublic | CecilTypeAttributes.Abstract | CecilTypeAttributes.Sealed
                | CecilTypeAttributes.BeforeFieldInit, writer.Module.TypeSystem.Object);
        writer.Module.Types.Add(owner);
        return owner;
    }

    private MethodDefinition WriteTypeLookup(
        CecilWriter writer,
        TypeDefinition owner,
        Assembly context,
        MethodReference target,
        int index)
    {
        var wrapper = new MethodDefinition("GetType" + index, CecilMethodAttributes.Assembly | CecilMethodAttributes.Static,
            writer.Import(typeof(Type)));
        owner.Methods.Add(wrapper);
        foreach (var parameter in target.Parameters)
        {
            wrapper.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        }

        var il = wrapper.Body.GetILProcessor();
        var resolvers = target.Parameters.Count >= 3 && target.Parameters[1].ParameterType.FullName != typeof(bool).FullName;
        var unmodified = il.Create(OpCodes.Ldarg_0);
        if (resolvers)
        {
            // Explicit type resolvers own their input names and returned types.
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Brtrue, unmodified);
        }

        il.Emit(OpCodes.Ldarg_0);
        var ignoreCase = resolvers ? 4 : 2;
        if (target.Parameters.Count > ignoreCase)
        {
            il.Emit(OpCodes.Ldarg, wrapper.Parameters[ignoreCase]);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4_0);
        }

        il.Emit(OpCodes.Ldstr, context.FullName!);
        WriteTypeLookupNames(writer, il);

        if (resolvers)
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Cgt_Un);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4_0);
        }

        il.Emit(OpCodes.Call, writer.Import(typeof(CopiedTypeNames).GetMethod(nameof(CopiedTypeNames.Translate),
            BindingFlags.Static | BindingFlags.NonPublic)!));
        var arguments = il.Create(OpCodes.Nop);
        if (resolvers)
        {
            il.Emit(OpCodes.Br, arguments);
            il.Append(unmodified);
            il.Append(arguments);
        }

        for (var parameter = 1; parameter < wrapper.Parameters.Count; parameter++)
        {
            il.Emit(OpCodes.Ldarg, wrapper.Parameters[parameter]);
        }

        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
        return wrapper;
    }

    private void WriteTypeLookupNames(CecilWriter writer, ILProcessor il)
    {
        il.Emit(OpCodes.Ldc_I4, _types.Count * 3);
        il.Emit(OpCodes.Newarr, writer.Module.TypeSystem.String);
        var offset = 0;
        foreach (var original in _types.Keys)
        {
            foreach (var value in new[]
            {
                original.FullName!,
                original.Assembly.FullName!,
                CecilSerializedTypeName.Format(writer.Import(original)),
            })
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, offset++);
                il.Emit(OpCodes.Ldstr, value);
                il.Emit(OpCodes.Stelem_Ref);
            }
        }
    }
}
