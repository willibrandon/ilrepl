using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Captures unchanged implementation images and cell recipes without activating user code.
/// </summary>
public static class NativeCapture
{
    /// <summary>
    /// Resolves a native selector to its actual compiled implementation.
    /// </summary>
    /// <param name="session">The declaration context.</param>
    /// <param name="selector">The method reference.</param>
    /// <param name="original">Whether an edit's original is selected.</param>
    /// <returns>The closed implementation metadata.</returns>
    public static MethodBase Resolve(Session session, string selector, bool original = false)
    {
        var edit = session.Edits.FirstOrDefault(item => item.Name == selector);
        if (original && edit is null)
        {
            throw new ReplException("--original requires an edit name");
        }

        if (edit is not null)
        {
            return original ? edit.Original.Requested
            : edit.Method ?? throw new ReplException($"edit '{selector}' has no committed implementation");
        }

        var resolved = MemberResolver.ResolveMethod(selector, session.InspectionContext,
            wantConstructor: selector.Contains("::.ctor", StringComparison.Ordinal));
        if (resolved.Definition is { } definition)
        {
            return session.Methods.First(method => method.Signature.Name == definition.Name &&
                SignatureIdentity.Same(method.Signature, definition)).Version.Body;
        }

        var method = resolved.Method;
        if (method is null || method.DeclaringType is TypeBuilder || resolved.Declared is not null)
        {
            throw new ReplException("close the declaration before inspecting its native code");
        }

        return method;
    }

    /// <summary>
    /// Freezes one selected body or the pending cell and all required bindings.
    /// </summary>
    /// <param name="session">The live or reconstructed declaration context.</param>
    /// <param name="selector">The method reference, or an empty string for a cell.</param>
    /// <param name="options">The requested workload and loading settings.</param>
    /// <returns>The immutable worker input.</returns>
    public static NativeTarget Create(Session session, string selector, NativeOptions options)
    {
        if (session.OpenDepth != 0)
        {
            throw new ReplException("close the declaration before native inspection");
        }

        var images = new Dictionary<string, NativeAssembly>(StringComparer.Ordinal);
        var visited = new HashSet<Assembly>();
        var methods = new HashSet<MethodBase>();
        var capturedTypes = new HashSet<Type>();
        var bindings = new Dictionary<string, NativeBinding>(StringComparer.Ordinal);
        var knownBindings = session.Methods.ToDictionary(method => method.Trampoline.Definition.Assembly,
            method => method);
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, NativeMethodIdentity>(StringComparer.Ordinal);
        var framework = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var edit = session.Edits.FirstOrDefault(item => item.Name == selector);
        var sourceResolver = (options.Original ? edit?.Baseline : edit?.Current)?.SourceResolver ?? session.Resolver;

        void CaptureType(Type type)
        {
            if (type.HasElementType)
            {
                CaptureType(type.GetElementType()!);
                return;
            }

            if (type.IsGenericParameter || !capturedTypes.Add(type))
            {
                return;
            }

            Visit(type.Assembly);
            if (type.BaseType is { } parent)
            {
                CaptureType(parent);
            }

            if (type.IsConstructedGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    CaptureType(argument);
                }
            }
        }

        NativeMethodIdentity CaptureMethod(MethodBase method)
        {
            if (methods.Count > 4096)
            {
                throw new ReplException("native inspection cannot establish a finite method dependency closure");
            }

            Visit(method.Module.Assembly);
            if (method.DeclaringType is { } declaring)
            {
                CaptureType(declaring);
            }

            if (method.IsGenericMethod)
            {
                foreach (var argument in method.GetGenericArguments())
                {
                    CaptureType(argument);
                }
            }

            foreach (var parameter in method.GetParameters())
            {
                CaptureType(parameter.ParameterType);
            }

            if (method is MethodInfo info)
            {
                CaptureType(info.ReturnType);
            }

            if (methods.Add(method) && Path.GetDirectoryName(method.Module.Assembly.Location) != framework
                && method.GetMethodBody() is { } body)
            {
                foreach (var local in body.LocalVariables)
                {
                    CaptureType(local.LocalType);
                }

                foreach (var clause in body.ExceptionHandlingClauses)
                {
                    if (clause.Flags == ExceptionHandlingClauseOptions.Clause && clause.CatchType is { } caught)
                    {
                        CaptureType(caught);
                    }
                }

                foreach (var instruction in IlReader.Read(body.GetILAsByteArray()!).Instructions)
                {
                    if (instruction.Op.OperandType is not (OperandType.InlineMethod or OperandType.InlineType
                        or OperandType.InlineField or OperandType.InlineTok))
                    {
                        continue;
                    }

                    var member = method.Module.ResolveMember(instruction.Operand.Token, method.DeclaringType?.GetGenericArguments(),
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                    switch (member)
                    {
                        case Type type: CaptureType(type); break;
                        case MethodBase callee: CaptureMethod(callee); break;
                        case FieldInfo field: CaptureType(field.DeclaringType!); CaptureType(field.FieldType); break;
                    }
                }
            }

            return Identify(method);
        }

        void Visit(Assembly assembly)
        {
            if (!visited.Add(assembly))
            {
                return;
            }

            var location = assembly.IsDynamic ? "" : assembly.Location;
            if (location.Length != 0 && Path.GetDirectoryName(location) == framework)
            {
                return;
            }

            byte[] image;
            var role = "reference";
            if (SessionAssemblies.TryGetDefinition(assembly, out var definition))
            {
                image = definition.Image ?? throw new ReplException("a referenced dynamic cell requires retained source");
                role = definition.Kind.ToString().ToLowerInvariant();
                foreach (var dependency in definition.Dependencies)
                {
                    Visit(dependency.Assembly);
                }
            }
            else if (sourceResolver.TryGetImage(assembly, out var captured))
            {
                image = captured;
            }
            else if (ReferenceLoadContext.TryGetMappedImage(assembly, out var mapped))
            {
                image = mapped;
            }
            else if (location.Length != 0)
            {
                image = File.ReadAllBytes(location);
            }
            else
            {
                throw new ReplException($"no immutable image is available for '{assembly.FullName}'");
            }

            using (var pe = new PEReader(new MemoryStream(image, writable: false)))
            {
                var reader = pe.GetMetadataReader();
                if (reader.GetGuid(reader.GetModuleDefinition().Mvid) != assembly.ManifestModule.ModuleVersionId)
                {
                    throw new ReplException($"dependency image '{assembly.FullName}' no longer matches the loaded module");
                }
            }

            var name = assembly.FullName!;
            if (images.TryGetValue(name, out var previous) && !previous.Image.AsSpan().SequenceEqual(image))
            {
                throw new ReplException($"conflicting captured images for '{name}'");
            }

            images[name] = new NativeAssembly { Name = name, Image = image, Role = role };
            if (knownBindings.TryGetValue(assembly, out var method))
            {
                bindings[name] = new NativeBinding
                {
                    Name = method.Signature.Name, Trampoline = Identify(method.Trampoline.Method),
                    Implementation = CaptureMethod(method.Version.Body),
                };
            }
            else if (role == "trampoline")
            {
                var owner = assembly.GetType("IlRepl.Cell")!;
                foreach (var field in owner.GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                {
                    if (field.GetValue(null) is not Delegate implementation)
                    {
                        continue;
                    }

                    var callable = owner.GetMethod(field.Name[..^4], BindingFlags.Static | BindingFlags.Public)!;
                    bindings[name] = new NativeBinding
                    {
                        Name = callable.Name, Visible = false, Trampoline = Identify(callable),
                            Implementation = CaptureMethod(implementation.Method),
                    };
                }
            }

            var context = AssemblyLoadContext.GetLoadContext(assembly);
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                try
                {
                    Visit(context?.LoadFromAssemblyName(reference) ?? Assembly.Load(reference));
                }
                catch (FileNotFoundException)
                {
                    // Metadata can retain unused optional references; reached IL operands are resolved separately and must succeed.
                }
            }
        }

        var cell = selector.Length == 0;
        var selected = cell ? null : Resolve(session, selector, options.Original);
        if (selected is not null)
        {
            if (selected.ContainsGenericParameters)
            {
                throw new ReplException("native inspection requires closed declaring-type and method generic arguments");
            }

            if (selected.GetMethodBody() is null)
            {
                throw new ReplException("the selected method has no managed IL body");
            }
        }
        else
        {
            if (session.Cell.IsEmpty)
            {
                throw new ReplException("there is no executable cell to inspect");
            }

            CellCompiler.RequireComplete(session);
        }

        var identity = selected is null ? null : CaptureMethod(selected);
        NativeMethodIdentity? scenario = null;
        if (options.Scenario is { } scenarioName)
        {
            var workload = session.Methods.FirstOrDefault(method => method.Signature.Name == scenarioName)
                ?? throw new ReplException($"no session scenario '{scenarioName}'");
            if (workload.Signature.Parameters.Count != 0)
            {
                throw new ReplException("a native scenario must be parameterless");
            }

            scenario = CaptureMethod(workload.Version.Body);
            if (selected is not null)
            {
                var rebound = NativeScenarioCapture.Create(session, workload.Version.Body, selected,
                    options with { Selector = options.Selector.Length == 0 ? selector : options.Selector }, CaptureMethod);
                scenario = rebound.Scenario;
                foreach (var image in rebound.Images)
                {
                    images.Add(image.Name, image);
                }
            }
        }

        if (cell || scenario is not null)
        {
            if (cell)
            {
                foreach (var entry in session.Cell.Entries)
                {
                    switch (entry.Instruction?.Operand)
                    {
                        case Type type: CaptureType(type); break;
                        case FieldInfo field: CaptureType(field.DeclaringType!); CaptureType(field.FieldType); break;
                        case ResolvedMethod { Method: { } method }: CaptureMethod(method); break;
                    }
                }

                foreach (var argument in session.Cell.Arguments)
                {
                    CaptureType(argument.Type);
                }

                foreach (var local in session.Cell.Locals)
                {
                    CaptureType(local.Type);
                }
            }

            foreach (var (name, type) in session.TypeTable.Entries)
            {
                CaptureType(type);
                types[name] = type.AssemblyQualifiedName!;
            }

            foreach (var method in session.Methods)
            {
                Visit(method.Trampoline.Definition.Assembly);
            }

            foreach (var (name, method) in session.TypeTable.MethodAliases)
            {
                aliases[name] = CaptureMethod(method);
            }
        }

        foreach (var type in session.TypeArguments ?? [])
        {
            CaptureType(type);
        }

        if (options.Run && scenario is null && selected is not null)
        {
            if (!selected.IsStatic)
            {
                throw new ReplException("instance execution requires using Scenario to construct the receiver");
            }

            var parameters = selected.GetParameters();
            if ((options.Arguments?.Length ?? 0) != parameters.Length)
            {
                throw new ReplException($"{selector} requires {parameters.Length} literal arguments or using Scenario");
            }

            for (var index = 0; index < parameters.Length; index++)
            {
                _ = ValueLiteralParser.Parse(options.Arguments![index], parameters[index].ParameterType.IsByRef
                    ? parameters[index].ParameterType.GetElementType()! : parameters[index].ParameterType);
            }
        }

        var resources = images.Values.ToDictionary(image => image.Name, image => new ComparisonAssembly(image.Name, image.Image)
        {
            OriginalLocation = visited.FirstOrDefault(assembly => assembly.FullName == image.Name && !assembly.IsDynamic)?.Location,
        }, StringComparer.OrdinalIgnoreCase);

        ComparisonCapture.CaptureSatellites(session, resources, sourceResolver);
        foreach (var resource in resources.Values)
        {
            images.TryAdd(resource.Name, new NativeAssembly { Name = resource.Name, Image = resource.Image, Role = "reference" });
        }

        var fingerprint = selected is null
            ? SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', session.DeclarationLines.Concat(session.BodyLines))))
            : SHA256.HashData(selected.GetMethodBody()!.GetILAsByteArray()!);
        return new NativeTarget
        {
            Name = cell ? "current cell" : selector + (options.Original ? " (original)" : ""), Method = identity, Scenario = scenario,
            Fingerprint = Convert.ToHexStringLower(fingerprint), Assemblies = [.. images.Values], Bindings = [.. bindings.Values],
            Types = types, Aliases = aliases, NativeLibraries = ComparisonCapture.CaptureNativeLibraries(
                sourceResolver),
            Cell = cell
                ? new NativeCell
                {
                    Declarations = [.. session.DeclarationLines],
                    Body = [.. session.BodyLines],
                    TypeArguments = [.. (session.TypeArguments ?? []).Select(type => type.AssemblyQualifiedName!)],
                }
                : null,
        };
    }

    /// <summary>
    /// Describes a closed method using its original metadata token and generic identities.
    /// </summary>
    /// <param name="method">The runtime method metadata.</param>
    /// <returns>The portable identity.</returns>
    public static NativeMethodIdentity Identify(MethodBase method) => new()
    {
        Assembly = method.Module.Assembly.FullName!, Type = method.DeclaringType is { IsConstructedGenericType: true } owner
            ? owner.GetGenericTypeDefinition().FullName! : method.DeclaringType?.FullName ?? "<Module>",
        Token = method.MetadataToken, JitNames = method.ContainsGenericParameters ? [] : NativeMethodName.Listings(method),
            DisplayName = MemberResolver.Describe(method),
        ReturnsPointer = method is MethodInfo returning && IsPointerReturn(returning.ReturnType),
        TypeArguments = method.DeclaringType is { IsConstructedGenericType: true } declaring
            ? [.. declaring.GetGenericArguments().Select(type => type.AssemblyQualifiedName!)] : [],
        MethodArguments = method.IsGenericMethod && !method.IsGenericMethodDefinition
            ? [.. method.GetGenericArguments().Select(type => type.AssemblyQualifiedName!)] : [],
    };

    private static bool IsPointerReturn(Type type) => !type.IsValueType || type.IsPointer || type.IsByRef
        || type == typeof(nint) || type == typeof(nuint) || type == typeof(RuntimeTypeHandle)
        || type == typeof(RuntimeMethodHandle) || type == typeof(RuntimeFieldHandle) || type == typeof(GCHandle);
}
