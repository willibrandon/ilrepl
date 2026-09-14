using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Engine;

/// <summary>
/// Creates accessible calls through enclosing owners without changing the copied members' accessibility.
/// </summary>
internal static class CecilForwardingMethod
{
    internal static MethodDefinition Create(MethodDefinition target, string name)
        => Shell(target, target, name);

    internal static MethodDefinition Find(MethodDefinition selected, string name)
    {
        var root = selected.DeclaringType;
        while (root.IsNested)
        {
            root = root.DeclaringType;
        }

        var shell = root.Module.Types.FirstOrDefault(type => type.Namespace == root.Namespace && type.Name == name + "_Entry");
        return (shell ?? root).Methods.Single(method => method.Name == name);
    }

    private static MethodDefinition Shell(MethodDefinition selected, MethodDefinition target, string name)
    {
        var root = target.DeclaringType;
        while (root.IsNested)
        {
            root = root.DeclaringType;
        }

        var owner = new TypeDefinition(root.Namespace, name + "_Entry",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, target.Module.TypeSystem.Object);
        target.Module.Types.Add(owner);
        var map = new Dictionary<GenericParameter, TypeReference>();
        foreach (var parameter in selected.DeclaringType.GenericParameters)
        {
            map.Add(parameter, Copy(parameter, owner));
        }

        var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            target.Module.TypeSystem.Void);
        owner.Methods.Add(method);
        foreach (var parameter in selected.GenericParameters)
        {
            map.Add(parameter, Copy(parameter, method));
        }

        CompleteGenerics(map);
        method.ReturnType = CecilGenericSubstitution.Apply(selected.ReturnType, map);
        if (selected.HasThis)
        {
            var receiver = Construct(selected.DeclaringType, map);
            method.Parameters.Add(new ParameterDefinition("receiver", ParameterAttributes.None,
                selected.DeclaringType.IsValueType ? new ByReferenceType(receiver) : receiver));
        }

        foreach (var parameter in selected.Parameters)
        {
            method.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes,
                CecilGenericSubstitution.Apply(parameter.ParameterType, map)));
        }

        var ownerMap = target.DeclaringType.GenericParameters.Select((parameter, index) =>
            (parameter, value: (TypeReference)owner.GenericParameters[index])).ToDictionary(pair => pair.parameter, pair => pair.value);
        var arguments = owner.GenericParameters.Skip(target.DeclaringType.GenericParameters.Count).Cast<TypeReference>()
            .Concat(method.GenericParameters);
        Emit(method, Reference(target, Construct(target.DeclaringType, ownerMap), arguments));
        return method;
    }

    private static GenericParameter Copy(GenericParameter parameter, IGenericParameterProvider owner)
    {
        var copy = new GenericParameter(parameter.Name, owner) { Attributes = parameter.Attributes };
        owner.GenericParameters.Add(copy);
        return copy;
    }

    private static void CompleteGenerics(Dictionary<GenericParameter, TypeReference> map)
    {
        foreach (var (source, target) in map)
        {
            if (target is not GenericParameter parameter || source.Owner == parameter.Owner)
            {
                continue;
            }

            foreach (var constraint in source.Constraints)
            {
                parameter.Constraints.Add(new GenericParameterConstraint(CecilGenericSubstitution.Apply(constraint.ConstraintType, map)));
            }
        }
    }

    private static TypeReference Construct(TypeDefinition owner, Dictionary<GenericParameter, TypeReference> map)
    {
        if (!owner.HasGenericParameters)
        {
            return owner;
        }

        var type = new GenericInstanceType(owner);
        foreach (var parameter in owner.GenericParameters)
        {
            type.GenericArguments.Add(map[parameter]);
        }

        return type;
    }

    private static MethodReference Reference(MethodDefinition target, TypeReference owner, IEnumerable<TypeReference> arguments)
    {
        var call = new MethodReference(target.Name, target.ReturnType, owner)
        {
            HasThis = target.HasThis,
            ExplicitThis = target.ExplicitThis,
            CallingConvention = target.CallingConvention,
        };
        foreach (var parameter in target.Parameters)
        {
            call.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        }

        foreach (var parameter in target.GenericParameters)
        {
            call.GenericParameters.Add(new GenericParameter(parameter.Name, call));
        }

        if (!target.HasGenericParameters)
        {
            return call;
        }

        var constructed = new GenericInstanceMethod(call);
        foreach (var argument in arguments)
        {
            constructed.GenericArguments.Add(argument);
        }

        return constructed;
    }

    private static void Emit(MethodDefinition method, MethodReference target)
    {
        var il = method.Body.GetILProcessor();
        if (method.HasThis)
        {
            il.Emit(OpCodes.Ldarg_0);
        }

        foreach (var parameter in method.Parameters)
        {
            il.Emit(OpCodes.Ldarg, parameter);
        }

        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
    }

    internal static void Redirect(MethodDefinition selected, string name, MethodDefinition target)
    {
        var forwarding = Find(selected, name);
        foreach (var instruction in forwarding.Body.Instructions.Where(instruction => instruction.OpCode.Code == Code.Call))
        {
            var reference = (MethodReference)instruction.Operand;
            if (reference is GenericInstanceMethod generic)
            {
                generic.ElementMethod.Name = target.Name;
            }
            else
            {
                reference.Name = target.Name;
            }
        }
    }
}
