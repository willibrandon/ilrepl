using System.Reflection;
using IlRepl.Protocol;
using Mono.Cecil;

namespace IlRepl.Engine;

/// <summary>
/// Rebinds scenario helpers to a comparison side while retaining the inspected implementation's original assembly graph.
/// </summary>
internal static class NativeScenarioCapture
{
    /// <summary>
    /// Copies only workload helpers and redirects their selected calls and receiver types.
    /// </summary>
    /// <param name="session">The captured declaration context.</param>
    /// <param name="scenario">The parameterless scenario implementation.</param>
    /// <param name="selected">The untouched implementation selected for this side.</param>
    /// <param name="options">The shared selector and explicit workload options.</param>
    /// <param name="capture">Retains an original method and its dependencies.</param>
    /// <returns>The scenario identity and its separately owned helper images.</returns>
    internal static (NativeMethodIdentity Scenario, NativeAssembly[] Images) Create(
        Session session,
        MethodInfo scenario,
        MethodBase selected,
        NativeOptions options,
        Func<MethodBase, NativeMethodIdentity> capture)
    {
        var edit = session.Edits.FirstOrDefault(item => item.Name == options.Selector);
        edit?.RequireScenarioSignature();
        var source = NativeCapture.Resolve(session, options.Selector);
        var replacements = new Dictionary<MethodBase, MethodBase> { [source] = selected };
        if (edit?.Current?.CallableEntryPoint is { } callable)
        {
            replacements[callable] = selected;
        }

        foreach (var method in session.Methods.Where(method => method.Version.Body == source))
        {
            replacements[method.Trampoline.Method] = selected;
        }

        if (!Compatible(source, selected, edit))
        {
            throw new ReplException("native comparison scenarios require compatible left and right method signatures");
        }

        var helpers = new Dictionary<MethodBase, (ModuleDefinition Module, string Name, MemoryStream Image)>();
        var pending = new Queue<MethodInfo>();
        pending.Enqueue(scenario);
        try
        {
            while (pending.TryDequeue(out var helper))
            {
                if (helpers.ContainsKey(helper))
                {
                    continue;
                }

                capture(helper);
                if (!SessionAssemblies.TryGetDefinition(helper.Module.Assembly, out var definition) || definition.Image is null)
                {
                    throw new ReplException("the native scenario helper has no retained image");
                }

                // Cecil reads a module on demand, so its stream stays open for as long as the module is in use.
                var stream = new MemoryStream(definition.Image, writable: false);
                var module = ModuleDefinition.ReadModule(stream);
                var name = "ilrepl.native.scenario." + Guid.NewGuid().ToString("N");
                module.Assembly.Name.Name = name;
                module.Name = name + ".dll";
                module.Mvid = Guid.NewGuid();
                helpers.Add(helper, (module, name, stream));
                var body = (MethodDefinition)module.LookupToken(helper.MetadataToken);
                foreach (var instruction in body.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference reference)
                    {
                        continue;
                    }

                    var method = helper.Module.ResolveMethod(reference.MetadataToken.ToInt32());
                    if (method is null || replacements.ContainsKey(method))
                    {
                        continue;
                    }

                    var nested = session.Methods.FirstOrDefault(item => item.Trampoline.Method == method || item.Version.Body == method);
                    if (nested is not null)
                    {
                        pending.Enqueue(nested.Version.Body);
                    }
                }
            }

            foreach (var (helper, image) in helpers)
            {
                var module = image.Module;
                var body = (MethodDefinition)module.LookupToken(helper.MetadataToken);
                foreach (var variable in body.Body.Variables)
                {
                    variable.VariableType = MapType(variable.VariableType);
                }

                foreach (var handler in body.Body.ExceptionHandlers.Where(handler => handler.CatchType is not null))
                {
                    handler.CatchType = MapType(handler.CatchType);
                }

                foreach (var instruction in body.Body.Instructions)
                {
                    if (instruction.Operand is TypeReference type)
                    {
                        instruction.Operand = MapType(type);
                    }
                    else if (instruction.Operand is FieldReference field)
                    {
                        var original = helper.Module.ResolveField(field.MetadataToken.ToInt32())!;
                        var mapped = MapMember(original);
                        if (mapped != original)
                        {
                            instruction.Operand = module.ImportReference((FieldInfo)mapped);
                        }
                    }
                    else if (instruction.Operand is MethodReference reference)
                    {
                        var original = helper.Module.ResolveMethod(reference.MetadataToken.ToInt32())!;
                        if (replacements.TryGetValue(original, out var replacement))
                        {
                            instruction.Operand = module.ImportReference(replacement);
                        }
                        else
                        {
                            var nested = session.Methods.FirstOrDefault(item => item.Trampoline.Method == original
                                || item.Version.Body == original)?.Version.Body;
                            if (nested is not null && helpers.TryGetValue(nested, out var copied))
                            {
                                var imported = module.ImportReference((MethodDefinition)copied.Module.LookupToken(nested.MetadataToken));
                                instruction.Operand = imported;
                            }
                            else if (MapMember(original) is MethodBase mapped && mapped != original)
                            {
                                instruction.Operand = module.ImportReference(mapped);
                            }
                        }
                    }
                }

                foreach (var assembly in module.AssemblyReferences.ToArray())
                {
                    Grant(module, assembly.Name);
                }

                MemberInfo MapMember(MemberInfo member) => options.Original && edit?.Current is { } family
                    ? family.OriginalMember(member) : member;

                TypeReference MapType(TypeReference type)
                {
                    if (type is ByReferenceType byRef)
                    {
                        return new ByReferenceType(MapType(byRef.ElementType));
                    }

                    if (type is PointerType pointer)
                    {
                        return new PointerType(MapType(pointer.ElementType));
                    }

                    if (type is ArrayType array)
                    {
                        var mapped = new ArrayType(MapType(array.ElementType), array.Rank);
                        for (var index = 0; index < array.Dimensions.Count; index++)
                        {
                            mapped.Dimensions[index] = array.Dimensions[index];
                        }

                        return mapped;
                    }

                    if (type is GenericInstanceType generic)
                    {
                        var mapped = new GenericInstanceType(MapType(generic.ElementType));
                        foreach (var argument in generic.GenericArguments)
                        {
                            mapped.GenericArguments.Add(MapType(argument));
                        }

                        return mapped;
                    }

                    if (!options.Original || edit?.Current is not { } family)
                    {
                        return type;
                    }

                    var runtime = family.RuntimeTypes.FirstOrDefault(candidate => candidate.FullName == type.FullName.Replace('/', '+')
                        && candidate.Assembly.FullName == type.Scope.ToString());
                    return runtime is null ? type : module.ImportReference((Type)family.OriginalMember(runtime));
                }
            }

            var images = new List<NativeAssembly>();
            foreach (var image in helpers.Values)
            {
                using var output = new MemoryStream();
                image.Module.Write(output);
                images.Add(new NativeAssembly { Name = image.Module.Assembly.Name.FullName, Image = output.ToArray(), Role = "scenario" });
            }

            var root = helpers[scenario].Module;
            return (NativeCapture.Identify(scenario) with { Assembly = root.Assembly.Name.FullName }, [.. images]);
        }
        finally
        {
            foreach (var helper in helpers.Values)
            {
                helper.Module.Dispose();
                helper.Image.Dispose();
            }
        }
    }

    private static bool Compatible(MethodBase left, MethodBase right, MethodEdit? edit) => edit is not null
        || left.IsStatic == right.IsStatic && left.GetParameters().Select(parameter => parameter.ParameterType)
            .SequenceEqual(right.GetParameters().Select(parameter => parameter.ParameterType))
        && (left as MethodInfo)?.ReturnType == (right as MethodInfo)?.ReturnType
        && (left.IsStatic || left.DeclaringType == right.DeclaringType);

    private static void Grant(ModuleDefinition module, string assembly)
    {
        var template = module.Assembly.CustomAttributes.FirstOrDefault(attribute =>
            attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute");
        if (template is null || module.Assembly.CustomAttributes.Any(attribute => attribute.AttributeType == template.AttributeType
            && attribute.ConstructorArguments.Count != 0 && Equals(attribute.ConstructorArguments[0].Value, assembly)))
        {
            return;
        }

        var grant = new CustomAttribute(template.Constructor);
        grant.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, assembly));
        module.Assembly.CustomAttributes.Add(grant);
    }
}
