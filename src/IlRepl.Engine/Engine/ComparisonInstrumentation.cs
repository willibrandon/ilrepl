using Mono.Cecil;
using Mono.Cecil.Cil;
using CilInstruction = Mono.Cecil.Cil.Instruction;
using Assembly = System.Reflection.Assembly;
using AssemblyName = System.Reflection.AssemblyName;
using RuntimeGenericAttributes = System.Reflection.GenericParameterAttributes;

namespace IlRepl.Engine;

/// <summary>
/// Adds an observation wrapper while preserving every instruction and recursive call in the selected body.
/// </summary>
internal static partial class ComparisonInstrumentation
{
    internal static MethodDefinition Wrap(CecilWriter writer, MethodDefinition target, MethodReference? externalVarArg = null)
        => Wrap(writer, target, [], "__ilrepl_observe_" + target.Name, externalVarArg);

    private static MethodDefinition Wrap(CecilWriter writer, MethodDefinition target, TypeReference[] optionalParameters, string name,
        MethodReference? externalVarArg = null, GenericParameter[]? callerParameters = null)
    {
        var owner = target.DeclaringType;
        while (owner.Methods.Any(method => method.Name == name))
        {
            name += "_";
        }

        var wrapper = new MethodDefinition(name,
            MethodAttributes.Public | MethodAttributes.HideBySig | (target.IsStatic ? MethodAttributes.Static : 0), target.ReturnType)
        {
            HasThis = target.HasThis,
            ExplicitThis = target.ExplicitThis,
            CallingConvention = target.CallingConvention == MethodCallingConvention.VarArg
                ? MethodCallingConvention.Default : target.CallingConvention,
        };
        owner.Methods.Add(wrapper);
        foreach (var parameter in target.GenericParameters)
        {
            wrapper.GenericParameters.Add(new GenericParameter(parameter.Name, wrapper) { Attributes = parameter.Attributes });
        }

        var map = new Dictionary<GenericParameter, TypeReference>();
        foreach (var parameter in callerParameters ?? [])
        {
            var copy = new GenericParameter(parameter.Name, wrapper)
            {
                Attributes = parameter.Attributes & ~GenericParameterAttributes.VarianceMask,
            };
            wrapper.GenericParameters.Add(copy);
            map.Add(parameter, copy);
        }

        foreach (var parameter in callerParameters ?? [])
        {
            var copy = (GenericParameter)map[parameter];
            foreach (var constraint in parameter.Constraints)
            {
                copy.Constraints.Add(new GenericParameterConstraint(CecilGenericSubstitution.Apply(constraint.ConstraintType, map)));
            }
        }

        optionalParameters = optionalParameters.Select(parameter => CecilGenericSubstitution.Apply(parameter, map)).ToArray();
        foreach (var parameter in target.Parameters)
        {
            var copy = new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType);
            if (parameter.HasConstant) copy.Constant = parameter.Constant;
            wrapper.Parameters.Add(copy);
        }

        foreach (var parameter in optionalParameters)
        {
            wrapper.Parameters.Add(new ParameterDefinition(parameter));
        }
        CecilCustomAttributes.CopyMethod(target, wrapper);

        for (var index = 0; index < target.GenericParameters.Count; index++)
        {
            foreach (var constraint in target.GenericParameters[index].Constraints)
            {
                wrapper.GenericParameters[index].Constraints.Add(new GenericParameterConstraint(constraint.ConstraintType));
            }
        }

        wrapper.Body.InitLocals = true;
        var il = wrapper.Body.GetILProcessor();
        var identity = new VariableDefinition(writer.Module.TypeSystem.Int32);
        var exception = new VariableDefinition(writer.Import(typeof(Exception)));
        wrapper.Body.Variables.Add(identity);
        wrapper.Body.Variables.Add(exception);
        var returnsVoid = Unmodified(target.ReturnType).MetadataType == MetadataType.Void;
        var result = returnsVoid ? null : new VariableDefinition(target.ReturnType);
        if (result is not null)
        {
            wrapper.Body.Variables.Add(result);
        }

        var enter = writer.Import(typeof(ComparisonProbe).GetMethod(nameof(ComparisonProbe.Enter))!);
        var leave = writer.Import(typeof(ComparisonProbe).GetMethod(nameof(ComparisonProbe.Leave))!);
        var unavailable = writer.Import(typeof(ComparisonProbe).GetMethod(nameof(ComparisonProbe.Unavailable))!);
        var nullReference = writer.Import(typeof(ComparisonProbe).GetMethod(nameof(ComparisonProbe.NullReference))!);
        var nullTask = writer.Import(typeof(ComparisonProbe).GetMethod(nameof(ComparisonProbe.NullTask))!);
        var self = Self(owner);

        void Box(TypeReference type)
        {
            type = Unmodified(type);
            CilInstruction? done = null;
            if (type is ByReferenceType reference)
            {
                var read = il.Create(OpCodes.Nop);
                done = il.Create(OpCodes.Nop);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Brtrue, read);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Call, nullReference);
                il.Emit(OpCodes.Br, done);
                il.Append(read);
                type = Unmodified(reference.ElementType);
                il.Emit(OpCodes.Ldobj, type);
            }

            if (type.IsValueType || type.IsGenericParameter)
            {
                il.Emit(OpCodes.Box, type);
            }

            if (done is not null)
            {
                il.Append(done);
            }
        }

        void Missing(string reason)
        {
            il.Emit(OpCodes.Ldstr, reason);
            il.Emit(OpCodes.Call, unavailable);
        }

        void Receiver()
        {
            if (target.IsStatic)
            {
                il.Emit(OpCodes.Ldnull);
            }
            else if (CannotBox(self))
            {
                Missing("the receiver is byref-like; return an explicit scenario observation");
            }
            else
            {
                il.Emit(OpCodes.Ldarg_0);
                if (owner.IsValueType)
                {
                    Box(new ByReferenceType(self));
                }
            }
        }

        void Arguments(bool before)
        {
            il.Emit(OpCodes.Ldc_I4, wrapper.Parameters.Count);
            il.Emit(OpCodes.Newarr, writer.Object);
            for (var index = 0; index < wrapper.Parameters.Count; index++)
            {
                var parameter = wrapper.Parameters[index];
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, index);
                if (before && parameter.IsOut && !parameter.IsIn)
                {
                    // An out slot has no caller-supplied input value, so do not dereference it.
                    il.Emit(OpCodes.Ldnull);
                }
                else if (CannotBox(parameter.ParameterType))
                {
                    Missing("argument " + index + " cannot be boxed; return an explicit scenario observation");
                }
                else
                {
                    il.Emit(OpCodes.Ldarg, parameter);
                    Box(parameter.ParameterType);
                }

                il.Emit(OpCodes.Stelem_Ref);
            }
        }

        void Completed(bool failed)
        {
            il.Emit(OpCodes.Ldloc, identity);
            Receiver();
            Arguments(before: false);
            if (!failed && result is not null && AwaitableTracker(target.ReturnType, writer) is { } tracker)
            {
                var synchronous = tracker.ReturnType.IsValueType ? null : il.Create(OpCodes.Nop);
                if (synchronous is not null)
                {
                    il.Emit(OpCodes.Ldloc, result);
                    il.Emit(OpCodes.Brfalse, synchronous);
                }

                il.Emit(OpCodes.Ldloc, result);
                Aliases(after: true, failed: false);
                il.Emit(OpCodes.Call, tracker);
                il.Emit(OpCodes.Stloc, result);
                if (synchronous is null)
                {
                    return;
                }

                var complete = il.Create(OpCodes.Nop);
                il.Emit(OpCodes.Br, complete);
                il.Append(synchronous);
                il.Emit(OpCodes.Call, nullTask);
                il.Emit(OpCodes.Ldnull);
                Aliases(after: true, failed: false);
                il.Emit(OpCodes.Call, leave);
                il.Append(complete);
                return;
            }

            if (failed || result is null)
            {
                il.Emit(OpCodes.Ldnull);
            }
            else if (CannotBox(target.ReturnType))
            {
                Missing("the returned value cannot be boxed; return an explicit scenario observation");
            }
            else
            {
                il.Emit(OpCodes.Ldloc, result);
                Box(target.ReturnType);
            }

            if (failed)
            {
                il.Emit(OpCodes.Ldloc, exception);
            }
            else
            {
                il.Emit(OpCodes.Ldnull);
            }

            Aliases(after: true, failed);
            il.Emit(OpCodes.Call, leave);
        }

        void Aliases(bool after, bool failed)
        {
            var count = wrapper.Parameters.Count + (after ? 2 : 1);
            var references = new List<int>();
            if (target.HasThis && owner.IsValueType)
            {
                references.Add(0);
            }

            references.AddRange(wrapper.Parameters.Where(parameter => Unmodified(parameter.ParameterType).IsByReference)
                .Select(parameter => parameter.Index + 1));
            if (after && !failed && Unmodified(target.ReturnType).IsByReference)
            {
                references.Add(count - 1);
            }

            void LoadReference(int index)
            {
                if (index == 0)
                {
                    il.Emit(OpCodes.Ldarg_0);
                }
                else if (index <= wrapper.Parameters.Count)
                {
                    il.Emit(OpCodes.Ldarg, wrapper.Parameters[index - 1]);
                }
                else
                {
                    il.Emit(OpCodes.Ldloc, result!);
                }
            }

            il.Emit(OpCodes.Ldc_I4, count);
            il.Emit(OpCodes.Newarr, writer.Module.TypeSystem.Int32);
            for (var index = 0; index < count; index++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, index);
                il.Emit(OpCodes.Ldc_I4, references.Contains(index) ? index : -1);
                il.Emit(OpCodes.Stelem_I4);
            }

            // Compare fresh managed pointers directly; no address survives an allocation or call.
            foreach (var index in references)
            {
                var done = il.Create(OpCodes.Nop);
                foreach (var earlier in references.Where(earlier => earlier < index))
                {
                    var next = il.Create(OpCodes.Nop);
                    LoadReference(index);
                    LoadReference(earlier);
                    il.Emit(OpCodes.Ceq);
                    il.Emit(OpCodes.Brfalse, next);
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldc_I4, index);
                    il.Emit(OpCodes.Ldc_I4, earlier);
                    il.Emit(OpCodes.Stelem_I4);
                    il.Emit(OpCodes.Br, done);
                    il.Append(next);
                }

                il.Append(done);
            }
        }

        Receiver();
        Arguments(before: true);
        Aliases(after: false, failed: false);
        il.Emit(OpCodes.Call, enter);
        il.Emit(OpCodes.Stloc, identity);
        var start = il.Create(OpCodes.Nop);
        il.Append(start);
        if (target.HasThis)
        {
            il.Emit(OpCodes.Ldarg_0);
        }

        foreach (var parameter in wrapper.Parameters)
        {
            il.Emit(OpCodes.Ldarg, parameter);
        }

        var destination = externalVarArg ?? target;
        var called = destination;
        if (owner.HasGenericParameters || optionalParameters.Length > 0)
        {
            called = new MethodReference(destination.Name, destination.ReturnType, externalVarArg?.DeclaringType ?? self)
            {
                HasThis = destination.HasThis,
                ExplicitThis = destination.ExplicitThis,
                CallingConvention = destination.CallingConvention,
            };
            foreach (var parameter in destination.Parameters)
            {
                called.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
            }

            foreach (var parameter in destination.GenericParameters)
            {
                called.GenericParameters.Add(new GenericParameter(parameter.Name, called));
            }

            for (var index = 0; index < optionalParameters.Length; index++)
            {
                called.Parameters.Add(new ParameterDefinition(index == 0
                    ? new SentinelType(optionalParameters[index]) : optionalParameters[index]));
            }
        }

        if (target.HasGenericParameters)
        {
            var instance = new GenericInstanceMethod(called);
            foreach (var parameter in wrapper.GenericParameters.Take(target.GenericParameters.Count))
            {
                instance.GenericArguments.Add(parameter);
            }

            called = instance;
        }

        il.Emit(OpCodes.Call, called);
        if (result is not null)
        {
            il.Emit(OpCodes.Stloc, result);
        }

        var end = il.Create(OpCodes.Nop);
        il.Emit(OpCodes.Leave, end);
        var handler = il.Create(OpCodes.Stloc, exception);
        il.Append(handler);
        Completed(failed: true);
        il.Emit(OpCodes.Rethrow);
        il.Append(end);
        Completed(failed: false);
        if (result is not null)
        {
            il.Emit(OpCodes.Ldloc, result);
        }

        il.Emit(OpCodes.Ret);
        wrapper.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = start,
            TryEnd = handler,
            HandlerStart = handler,
            HandlerEnd = end,
            CatchType = writer.Import(typeof(Exception)),
        });
        return wrapper;
    }

    private static TypeReference Self(TypeDefinition type)
    {
        if (!type.HasGenericParameters)
        {
            return type;
        }

        var instance = new GenericInstanceType(type);
        foreach (var parameter in type.GenericParameters)
        {
            instance.GenericArguments.Add(parameter);
        }

        return instance;
    }

    private static MethodReference? AwaitableTracker(TypeReference type, CecilWriter writer)
    {
        type = Unmodified(type);
        var definition = type is GenericInstanceType constructed ? constructed.ElementType.FullName : type.FullName;
        var name = definition == typeof(Task).FullName || definition == typeof(Task<>).FullName
            ? nameof(ComparisonProbe.TrackTask)
            : definition == typeof(ValueTask).FullName || definition == typeof(ValueTask<>).FullName
                ? nameof(ComparisonProbe.TrackValueTask) : null;
        if (name is null)
        {
            if (!IsTask(type, []))
            {
                return null;
            }

            var derivedMethod = typeof(ComparisonProbe).GetMethod(nameof(ComparisonProbe.TrackDerivedTask))!;
            var derived = new GenericInstanceMethod(writer.Import(derivedMethod));
            derived.GenericArguments.Add(type);
            return derived;
        }

        var generic = type is GenericInstanceType;
        var method = typeof(ComparisonProbe).GetMethods().Single(method => method.Name == name && method.IsGenericMethod == generic);
        var reference = writer.Import(method);
        if (type is GenericInstanceType instance)
        {
            var closed = new GenericInstanceMethod(reference);
            closed.GenericArguments.Add(instance.GenericArguments[0]);
            return closed;
        }

        return reference;
    }

    private static bool IsTask(TypeReference type, HashSet<TypeReference> visited)
    {
        type = Unmodified(type);
        if (type is TypeSpecification and not GenericInstanceType || type.IsValueType || !visited.Add(type))
        {
            return false;
        }

        if (type is GenericParameter parameter)
        {
            return parameter.Constraints.Any(constraint => IsTask(constraint.ConstraintType, visited));
        }

        var definition = type.GetElementType();
        if (definition is TypeDefinition local)
        {
            return local.BaseType is { } parent && IsTask(parent, visited);
        }

        if (definition.Scope is not AssemblyNameReference assembly)
        {
            return false;
        }

        return typeof(Task).IsAssignableFrom(RuntimeType(definition, assembly));
    }

    private static bool CannotBox(TypeReference type)
    {
        type = Unmodified(type);
        if (type is ByReferenceType reference)
        {
            return CannotBox(reference.ElementType);
        }

        if (type.IsPointer || type.IsFunctionPointer || type.MetadataType == MetadataType.TypedByReference)
        {
            return true;
        }

        if (type is GenericParameter parameter)
        {
            return ((RuntimeGenericAttributes)parameter.Attributes).HasFlag(RuntimeGenericAttributes.AllowByRefLike);
        }

        if (!type.IsValueType)
        {
            return false;
        }

        var definition = type.GetElementType();
        if (definition is TypeDefinition local)
        {
            return local.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute");
        }

        // Imported signatures already have loaded runtime types, including assemblies available only as images in the browser.
        var assembly = (AssemblyNameReference)definition.Scope;
        return RuntimeType(definition, assembly).IsByRefLike;
    }

    private static Type RuntimeType(TypeReference definition, AssemblyNameReference assembly)
    {
        var name = new AssemblyName(assembly.FullName);
        var runtime = ReferenceLoadScope.Current?.Resolve(name) ?? SessionAssemblies.Resolve(name) ?? Assembly.Load(name);
        return runtime.GetType(TypeResolver.ReflectionName(definition.FullName), throwOnError: true)!;
    }

    private static TypeReference Unmodified(TypeReference type)
    {
        while (type is IModifierType modifier)
        {
            type = modifier.ElementType;
        }

        return type;
    }
}
