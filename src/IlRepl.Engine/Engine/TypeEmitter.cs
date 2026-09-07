using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using CecilEventAttributes = Mono.Cecil.EventAttributes;
using CecilFieldAttributes = Mono.Cecil.FieldAttributes;
using CecilGenericParameterAttributes = Mono.Cecil.GenericParameterAttributes;
using CecilMethodAttributes = Mono.Cecil.MethodAttributes;
using CecilMethodImplAttributes = Mono.Cecil.MethodImplAttributes;
using CecilParameterAttributes = Mono.Cecil.ParameterAttributes;
using CecilPropertyAttributes = Mono.Cecil.PropertyAttributes;
using CecilTypeAttributes = Mono.Cecil.TypeAttributes;
using NamedArgument = Mono.Cecil.CustomAttributeNamedArgument;

namespace IlRepl.Engine;

/// <summary>
/// Writes a type family exactly as declared into a session assembly, loads it, and prepares
/// its bodies on the JIT. The live type carries the declared metadata and nothing else: no
/// synthesized constructor, the declared layout, the declared attributes and modifiers. The
/// same writer serves an export, which is why live and saved metadata agree.
/// </summary>
public static class TypeEmitter
{
    /// <summary>
    /// Writes and loads a family.
    /// </summary>
    /// <param name="family">The outermost declaration, with its nested types.</param>
    /// <param name="prototypes">The prototype and members of every declaration, by path.</param>
    /// <param name="trampolines">The trampolines of the session methods bodies may call, by name.</param>
    /// <param name="prepare">True to ask the JIT to compile every body.</param>
    /// <returns>The loaded family.</returns>
    /// <exception cref="ReplException">The writer, the loader, or the JIT rejected the family.</exception>
    public static CompiledFamily Compile(TypeDeclaration family, IReadOnlyDictionary<string, (TypeBuilder Prototype, OwnMembers Members)> prototypes, IReadOnlyDictionary<string, MethodTrampoline> trampolines, bool prepare)
    {
        ArgumentNullException.ThrowIfNull(family);
        var name = SessionAssemblies.NextName(SessionAssemblyKind.Types);
        var (image, dependencies) = Write(family, prototypes, trampolines, name, null);
        var compiled = Load(image, name, dependencies, family);
        if (prepare)
        {
            try
            {
                Prepare(compiled, family);
            }
            catch
            {
                SessionAssemblies.Release(compiled.Definition);
                throw;
            }
        }

        return compiled;
    }

    /// <summary>
    /// Writes a family into an image under a name taken in advance. Prototypes of other families
    /// written in the same group are referenced by their assembly names.
    /// </summary>
    /// <param name="family">The outermost declaration.</param>
    /// <param name="prototypes">The prototype and members of every declaration, by path.</param>
    /// <param name="trampolines">The trampolines of the session methods, by name.</param>
    /// <param name="name">The assembly's simple name.</param>
    /// <param name="externals">Prototypes of the other families of the group, or null.</param>
    /// <returns>The image and the loaded session assemblies it references.</returns>
    /// <exception cref="ReplException">The writer rejected the family.</exception>
    public static (byte[] Image, IReadOnlyList<DefinitionAssembly> Dependencies) Write(TypeDeclaration family, IReadOnlyDictionary<string, (TypeBuilder Prototype, OwnMembers Members)> prototypes, IReadOnlyDictionary<string, MethodTrampoline> trampolines, string name, IReadOnlyDictionary<Type, CecilWriter.ExternalPrototype>? externals)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(prototypes);
        ArgumentNullException.ThrowIfNull(trampolines);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var writer = new CecilWriter(SessionAssemblyKind.Types, name);
        foreach (var (prototype, external) in externals ?? new Dictionary<Type, CecilWriter.ExternalPrototype>())
        {
            writer.DefineExternal(prototype, external);
        }

        try
        {
            WriteAll(writer, [(family, prototypes, null)], trampolines);
            return (writer.Write(), writer.Dependencies);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or NullReferenceException)
        {
            throw new ReplException($"the writer rejected {family.KindWord} {family.FullName}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Loads a written family and resolves its types.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="name">The assembly's simple name.</param>
    /// <param name="dependencies">The loaded session assemblies it references.</param>
    /// <param name="family">The outermost declaration.</param>
    /// <returns>The loaded family.</returns>
    /// <exception cref="ReplException">The runtime rejected the family.</exception>
    public static CompiledFamily Load(byte[] image, string name, IReadOnlyList<DefinitionAssembly> dependencies, TypeDeclaration family)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(family);
        DefinitionAssembly definition;
        try
        {
            definition = SessionAssemblies.Load(image, name, SessionAssemblyKind.Types, dependencies);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ArgumentException or InvalidOperationException)
        {
            throw new ReplException($"the runtime rejected {family.KindWord} {family.FullName}: {ex.Message}", ex);
        }

        try
        {
            var types = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var declaration in family.Family)
            {
                types[declaration.FullName] = LoadType(definition.Assembly, declaration);
            }

            return new CompiledFamily(definition, types);
        }
        catch
        {
            SessionAssemblies.Release(definition);
            throw;
        }
    }

    /// <summary>
    /// Asks the JIT to compile every body of a loaded family.
    /// </summary>
    /// <param name="compiled">The loaded family.</param>
    /// <param name="family">Its outermost declaration.</param>
    /// <exception cref="ReplException">The JIT or the runtime rejected a body.</exception>
    public static void Prepare(CompiledFamily compiled, TypeDeclaration family)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(family);
        foreach (var declaration in family.Family)
        {
            Prepare(declaration, compiled.Types[declaration.FullName]);
        }
    }

    /// <summary>
    /// Writes a family into an existing writer, for an export. The prototypes map onto the
    /// definitions written; nothing is loaded.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="family">The outermost declaration.</param>
    /// <param name="prototypes">The prototype and members of every declaration, by path.</param>
    /// <param name="trampolines">The trampolines of the session methods, by name.</param>
    /// <param name="runtimeTypes">The loaded types of the family by path, whose members other bodies are bound to, or null.</param>
    public static void Write(CecilWriter writer, TypeDeclaration family, IReadOnlyDictionary<string, (TypeBuilder Prototype, OwnMembers Members)> prototypes, IReadOnlyDictionary<string, MethodTrampoline> trampolines, IReadOnlyDictionary<string, Type>? runtimeTypes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(family);
        WriteAll(writer, [(family, prototypes, runtimeTypes)], trampolines);
    }

    /// <summary>
    /// Writes several families into one writer: every type of every family is declared before
    /// any shape or body is imported, so the families may mention each other in any order.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="families">The families with their prototypes and, for an export, their loaded types.</param>
    /// <param name="trampolines">The trampolines of the session methods, by name.</param>
    public static void WriteAll(CecilWriter writer, IReadOnlyList<(TypeDeclaration Family, IReadOnlyDictionary<string, (TypeBuilder Prototype, OwnMembers Members)> Prototypes, IReadOnlyDictionary<string, Type>? RuntimeTypes)> families, IReadOnlyDictionary<string, MethodTrampoline> trampolines)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(families);
        ArgumentNullException.ThrowIfNull(trampolines);
        var emitters = families.Select(f => (Emitter: new Emitter(writer, f.Prototypes, trampolines) { RuntimeTypes = f.RuntimeTypes }, f.Family)).ToList();
        foreach (var (emitter, family) in emitters)
        {
            emitter.Declare(family);
        }

        foreach (var (emitter, family) in emitters)
        {
            emitter.Shape(family);
        }

        foreach (var (emitter, family) in emitters)
        {
            emitter.Members(family);
        }

        foreach (var (emitter, family) in emitters)
        {
            emitter.Details(family);
        }

        foreach (var (emitter, family) in emitters)
        {
            emitter.Bodies(family);
        }
    }

    internal static Type LoadType(Assembly assembly, TypeDeclaration declaration)
    {
        var name = ReflectionName(declaration);
        try
        {
            return assembly.GetType(name, throwOnError: true)!;
        }
        catch (TypeLoadException ex)
        {
            throw new ReplException($"the runtime rejected {declaration.KindWord} {declaration.FullName}: {ex.Message}", ex);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new ReplException($"the runtime rejected {declaration.KindWord} {declaration.FullName}: {ex.InnerException.Message}", ex);
        }
    }

    /// <summary>
    /// The reflection name of a declaration: the namespace, then the nesting path with '+'.
    /// </summary>
    /// <param name="declaration">The declaration.</param>
    /// <returns>The name <c>Assembly.GetType</c> accepts.</returns>
    public static string ReflectionName(TypeDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        return declaration.FullName.Replace('/', '+');
    }

    private static void Prepare(TypeDeclaration declaration, Type type)
    {
        Type[]? instantiation = null;
        if (type.IsGenericTypeDefinition)
        {
            instantiation = ClosedInstantiation(type.GetGenericArguments());
            if (instantiation is null)
            {
                return;
            }
        }

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            if (method.IsAbstract || method.ContainsGenericParameters && method is MethodInfo { IsGenericMethodDefinition: true })
            {
                continue;
            }

            try
            {
                if (instantiation is null)
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                }
                else
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle, [.. instantiation.Select(t => t.TypeHandle)]);
                }
            }
            catch (InvalidProgramException ex) when (ex.Message.Contains("Vararg", StringComparison.OrdinalIgnoreCase))
            {
                throw new ReplException($"the runtime only supports the vararg calling convention on Windows; {declaration.KindWord} {declaration.FullName}::{method.Name} cannot be prepared here", ex);
            }
            catch (InvalidProgramException ex)
            {
                throw new ReplException($"the JIT rejected {declaration.FullName}::{method.Name}: {ex.Message} (check .show for a stack mismatch between branches)", ex);
            }
            catch (Exception ex) when (ex is TypeLoadException or MissingMemberException or BadImageFormatException or TypeInitializationException or ArgumentException)
            {
                throw new ReplException($"the runtime rejected {declaration.FullName}::{method.Name}: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Picks type arguments the constraints admit, or null when a constraint needs a specific type.
    /// </summary>
    private static Type[]? ClosedInstantiation(Type[] parameters)
    {
        var arguments = new Type[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.GetGenericParameterConstraints().Length > 0)
            {
                return null;
            }

            var special = parameter.GenericParameterAttributes & System.Reflection.GenericParameterAttributes.SpecialConstraintMask;
            arguments[i] = special.HasFlag(System.Reflection.GenericParameterAttributes.NotNullableValueTypeConstraint) ? typeof(int) : typeof(object);
        }

        return arguments;
    }

    private sealed class Emitter(CecilWriter writer, IReadOnlyDictionary<string, (TypeBuilder Prototype, OwnMembers Members)> prototypes, IReadOnlyDictionary<string, MethodTrampoline> trampolines)
    {
        private readonly Dictionary<string, TypeDefinition> _definitions = new(StringComparer.Ordinal);
        private readonly Dictionary<MethodDeclaration, MethodDefinition> _methods = new(ReferenceEqualityComparer.Instance);
        private readonly EmitMap _map = new(signature => trampolines.TryGetValue(signature.Name, out var trampoline) ? trampoline.Method : throw new ReplException($"no method '{signature.Name}' is bound in the session"));

        /// <summary>
        /// The loaded types of the family by path, for an export: their members map onto the definitions too.
        /// </summary>
        public IReadOnlyDictionary<string, Type>? RuntimeTypes { get; init; }

        private Type? RuntimeTypeOf(string path) => RuntimeTypes is not null && RuntimeTypes.TryGetValue(path, out var type) ? type : null;

        public void Declare(TypeDeclaration family) => DefineTypes(family, null);

        public void Shape(TypeDeclaration family)
        {
            foreach (var declaration in family.Family)
            {
                DefineShape(declaration);
            }
        }

        public void Members(TypeDeclaration family)
        {
            foreach (var declaration in family.Family)
            {
                DefineMembers(declaration);
            }
        }

        public void Details(TypeDeclaration family)
        {
            foreach (var declaration in family.Family)
            {
                DefineDetails(declaration);
            }
        }

        public void Bodies(TypeDeclaration family)
        {
            foreach (var declaration in family.Family)
            {
                EmitBodies(declaration);
            }
        }

        private void DefineTypes(TypeDeclaration declaration, TypeDefinition? enclosing)
        {
            var (prototype, _) = prototypes[declaration.FullName];
            var attributes = (CecilTypeAttributes)declaration.Attributes;
            TypeDefinition definition;
            if (enclosing is null)
            {
                definition = writer.DefineType(declaration.Namespace, declaration.Name, attributes, null);
            }
            else
            {
                definition = new TypeDefinition("", declaration.Name, attributes, null);
                enclosing.NestedTypes.Add(definition);
            }

            _definitions[declaration.FullName] = definition;
            writer.Define(prototype, definition);
            var runtime = RuntimeTypeOf(declaration.FullName);
            if (runtime is not null)
            {
                writer.Define(runtime, definition);
            }

            var builders = prototype.IsGenericTypeDefinition ? prototype.GetGenericArguments() : [];
            var runtimeParameters = runtime is { IsGenericTypeDefinition: true } ? runtime.GetGenericArguments() : [];
            for (var i = 0; i < declaration.TypeParameters.Count; i++)
            {
                var parameter = new GenericParameter(declaration.TypeParameters[i].Name, definition);
                definition.GenericParameters.Add(parameter);
                if (i < builders.Length)
                {
                    writer.Define(builders[i], parameter);
                }

                if (i < runtimeParameters.Length)
                {
                    writer.Define(runtimeParameters[i], parameter);
                }
            }

            foreach (var nested in declaration.NestedTypes)
            {
                DefineTypes(nested, definition);
            }
        }

        private void DefineShape(TypeDeclaration declaration)
        {
            var definition = _definitions[declaration.FullName];
            if (declaration.BaseType is not null)
            {
                definition.BaseType = writer.Import(declaration.BaseType);
            }

            foreach (var iface in declaration.Interfaces)
            {
                definition.Interfaces.Add(new InterfaceImplementation(writer.Import(iface)));
            }

            for (var i = 0; i < declaration.TypeParameters.Count; i++)
            {
                var declared = declaration.TypeParameters[i];
                var parameter = definition.GenericParameters[i];
                parameter.Attributes = (CecilGenericParameterAttributes)declared.Attributes;
                foreach (var constraint in declared.Constraints)
                {
                    parameter.Constraints.Add(new GenericParameterConstraint(writer.Import(constraint)));
                }
            }

            if (declaration.PackingSize is not null || declaration.ClassSize is not null)
            {
                // Both fields are written; an unset one would be -1 in the row.
                definition.PackingSize = (short)(declaration.PackingSize ?? 0);
                definition.ClassSize = declaration.ClassSize ?? 0;
            }

        }

        private void DefineMembers(TypeDeclaration declaration)
        {
            var definition = _definitions[declaration.FullName];
            var (prototype, members) = prototypes[declaration.FullName];
            foreach (var field in declaration.Fields)
            {
                var type = Modified(writer.Import(field.Type), field.RequiredModifiers, field.OptionalModifiers);
                var cecilField = new FieldDefinition(field.Name, (CecilFieldAttributes)field.Attributes, type);
                if (field.Offset is { } offset)
                {
                    cecilField.Offset = offset;
                }

                if (field.HasDefault)
                {
                    cecilField.Constant = ConstantFor(field.DefaultValue);
                }

                definition.Fields.Add(cecilField);
                var builder = members.FindField(field.Name)?.Builder;
                if (builder is not null)
                {
                    writer.Define(builder, cecilField);
                }

                if (RuntimeTypeOf(declaration.FullName)?.GetField(field.Name, AllMembers) is { } runtimeField)
                {
                    writer.Define(runtimeField, cecilField);
                }
            }

            foreach (var method in declaration.Methods)
            {
                var signature = method.Signature;
                var builder = FindBuilder(members, signature);
                var cecilMethod = new MethodDefinition(signature.Name, (CecilMethodAttributes)signature.Attributes, writer.Module.TypeSystem.Void)
                {
                    ImplAttributes = (CecilMethodImplAttributes)signature.ImplAttributes,
                    HasThis = !signature.IsStatic,
                    ExplicitThis = signature.CallingConvention.HasFlag(CallingConventions.ExplicitThis),
                };
                if (signature.CallingConvention.HasFlag(CallingConventions.VarArgs))
                {
                    cecilMethod.CallingConvention = MethodCallingConvention.VarArg;
                }

                definition.Methods.Add(cecilMethod);
                _methods[method] = cecilMethod;
                if (RuntimeMethodOf(declaration, method) is { } runtimeMethod)
                {
                    writer.Define(runtimeMethod, cecilMethod);
                    var runtimeGenerics = runtimeMethod is MethodInfo { IsGenericMethodDefinition: true } generic ? generic.GetGenericArguments() : [];
                    for (var i = 0; i < runtimeGenerics.Length && i < signature.TypeParameters.Count; i++)
                    {
                        // Registered below once the definition's own parameters exist.
                        _runtimeMethodParameters[(cecilMethod, i)] = runtimeGenerics[i];
                    }
                }

                if (builder is not null)
                {
                    writer.Define(builder, cecilMethod);
                    var methodBuilders = builder is MethodBuilder mb && mb.IsGenericMethodDefinition ? mb.GetGenericArguments() : [];
                    for (var i = 0; i < signature.TypeParameters.Count; i++)
                    {
                        var parameter = new GenericParameter(signature.TypeParameters[i].Name, cecilMethod);
                        cecilMethod.GenericParameters.Add(parameter);
                        if (i < methodBuilders.Length)
                        {
                            writer.Define(methodBuilders[i], parameter);
                        }

                        if (_runtimeMethodParameters.TryGetValue((cecilMethod, i), out var runtimeParameter))
                        {
                            writer.Define(runtimeParameter, parameter);
                        }
                    }
                }
            }

            _ = prototype;
        }

        /// <summary>
        /// Signatures, attributes, properties, and events: imported once every type and member
        /// of every family in the group exists, since an attribute may name a constructor of
        /// another family.
        /// </summary>
        private void DefineDetails(TypeDeclaration declaration)
        {
            var definition = _definitions[declaration.FullName];
            foreach (var attribute in declaration.CustomAttributes)
            {
                definition.CustomAttributes.Add(Attribute(attribute));
            }

            foreach (var field in declaration.Fields)
            {
                var cecilField = definition.Fields.First(f => f.Name == field.Name);
                foreach (var attribute in field.CustomAttributes)
                {
                    cecilField.CustomAttributes.Add(Attribute(attribute));
                }
            }

            foreach (var method in declaration.Methods)
            {
                var signature = method.Signature;
                var cecilMethod = _methods[method];
                cecilMethod.ReturnType = Modified(writer.Import(signature.ReturnType), signature.ReturnRequiredModifiers, signature.ReturnOptionalModifiers);
                for (var i = 0; i < signature.TypeParameters.Count; i++)
                {
                    var declared = signature.TypeParameters[i];
                    var parameter = cecilMethod.GenericParameters[i];
                    parameter.Attributes = (CecilGenericParameterAttributes)declared.Attributes;
                    foreach (var constraint in declared.Constraints)
                    {
                        parameter.Constraints.Add(new GenericParameterConstraint(writer.Import(constraint)));
                    }
                }

                for (var i = 0; i < signature.Parameters.Count; i++)
                {
                    var parameter = signature.Parameters[i];
                    var type = Modified(writer.Import(parameter.Type), parameter.RequiredModifiers, parameter.OptionalModifiers);
                    var cecilParameter = new ParameterDefinition(parameter.Name ?? ("arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), (CecilParameterAttributes)parameter.Attributes, type);
                    if (parameter.HasDefault)
                    {
                        cecilParameter.Constant = ConstantFor(parameter.DefaultValue);
                    }

                    foreach (var attribute in parameter.CustomAttributes)
                    {
                        cecilParameter.CustomAttributes.Add(Attribute(attribute));
                    }

                    cecilMethod.Parameters.Add(cecilParameter);
                }

                foreach (var attribute in signature.CustomAttributes)
                {
                    cecilMethod.CustomAttributes.Add(Attribute(attribute));
                }

                foreach (var attribute in signature.ReturnCustomAttributes)
                {
                    cecilMethod.MethodReturnType.CustomAttributes.Add(Attribute(attribute));
                }
            }

            foreach (var property in declaration.Properties)
            {
                var cecilProperty = new PropertyDefinition(property.Name, (CecilPropertyAttributes)property.Attributes, writer.Import(property.Type))
                {
                    HasThis = !property.IsStatic,
                };
                foreach (var parameterType in property.ParameterTypes)
                {
                    cecilProperty.Parameters.Add(new ParameterDefinition(writer.Import(parameterType)));
                }

                if (property.HasDefault)
                {
                    cecilProperty.Constant = ConstantFor(property.DefaultValue);
                }

                cecilProperty.GetMethod = property.Getter is null ? null : _methods[property.Getter];
                cecilProperty.SetMethod = property.Setter is null ? null : _methods[property.Setter];
                foreach (var other in property.Others)
                {
                    cecilProperty.OtherMethods.Add(_methods[other]);
                }

                foreach (var attribute in property.CustomAttributes)
                {
                    cecilProperty.CustomAttributes.Add(Attribute(attribute));
                }

                definition.Properties.Add(cecilProperty);
            }

            foreach (var evt in declaration.Events)
            {
                var cecilEvent = new EventDefinition(evt.Name, (CecilEventAttributes)evt.Attributes, writer.Import(evt.HandlerType))
                {
                    AddMethod = _methods[evt.AddOn],
                    RemoveMethod = _methods[evt.RemoveOn],
                    InvokeMethod = evt.Fire is null ? null : _methods[evt.Fire],
                };
                foreach (var attribute in evt.CustomAttributes)
                {
                    cecilEvent.CustomAttributes.Add(Attribute(attribute));
                }

                definition.Events.Add(cecilEvent);
            }
        }

        private void EmitBodies(TypeDeclaration declaration)
        {
            foreach (var method in declaration.Methods)
            {
                var cecilMethod = _methods[method];
                foreach (var over in method.Overrides)
                {
                    cecilMethod.Overrides.Add(writer.Import(over.Target, over.Target.DeclaringType));
                }

                if (method.Body is { } body)
                {
                    CecilBodyEmitter.Emit(cecilMethod, body, writer, _map);
                }
            }

            foreach (var over in declaration.Overrides)
            {
                var implementing = declaration.Methods.FirstOrDefault(m => m.Name == over.BodyName && m.IsStatic == over.BodyIsStatic
                    && TypeIdentity.Equal(m.Signature.ReturnType, over.BodyReturnType)
                    && m.Signature.Parameters.Count == over.BodyParameterTypes.Count
                    && m.Signature.ParameterTypes.Zip(over.BodyParameterTypes).All(p => TypeIdentity.Equal(p.First, p.Second)))
                    ?? throw new ReplException($".override names {over.BodyName}, which {declaration.FullName} does not declare");
                _methods[implementing].Overrides.Add(writer.Import(over.Target, over.Target.DeclaringType));
            }
        }

        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        private readonly Dictionary<(MethodDefinition, int), Type> _runtimeMethodParameters = [];

        /// <summary>
        /// The loaded method behind a declaration, matched by name, kind, and parameter shape.
        /// </summary>
        private MethodBase? RuntimeMethodOf(TypeDeclaration declaration, MethodDeclaration method)
        {
            var runtime = RuntimeTypeOf(declaration.FullName);
            if (runtime is null)
            {
                return null;
            }

            var signature = method.Signature;
            var wanted = signature.ParameterTypes.Select(TypeNameFormatter.Pretty).ToArray();
            IEnumerable<MethodBase> candidates = method.IsConstructor || method.IsTypeInitializer
                ? runtime.GetConstructors(AllMembers).Where(c => c.IsStatic == method.IsTypeInitializer)
                : runtime.GetMethods(AllMembers).Where(m => m.Name == signature.Name && m.IsStatic == signature.IsStatic);
            return candidates.FirstOrDefault(m =>
            {
                var parameters = m.GetParameters();
                return parameters.Length == wanted.Length && parameters.Select(p => TypeNameFormatter.Pretty(p.ParameterType)).SequenceEqual(wanted);
            });
        }

        private static MethodBase? FindBuilder(OwnMembers members, MethodSignature signature)
        {
            foreach (var (declared, builder, isDeclared) in members.Methods)
            {
                if (isDeclared && ReferenceEquals(declared, signature))
                {
                    return builder;
                }
            }

            foreach (var (declared, builder, isDeclared) in members.Methods)
            {
                if (isDeclared && declared.Name == signature.Name && declared.IsStatic == signature.IsStatic && declared.Parameters.Count == signature.Parameters.Count
                    && declared.ParameterTypes.Zip(signature.ParameterTypes).All(p => TypeIdentity.Equal(p.First, p.Second)))
                {
                    return builder;
                }
            }

            return null;
        }

        private TypeReference Modified(TypeReference type, IReadOnlyList<Type> required, IReadOnlyList<Type> optional)
        {
            var result = type;
            foreach (var modifier in optional)
            {
                result = new OptionalModifierType(writer.Import(modifier), result);
            }

            foreach (var modifier in required)
            {
                result = new RequiredModifierType(writer.Import(modifier), result);
            }

            return result;
        }

        private static object? ConstantFor(object? value) => value is Enum e ? System.Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()), System.Globalization.CultureInfo.InvariantCulture) : value;

        private CustomAttribute Attribute(CustomAttributeDeclaration declaration)
        {
            var attribute = new CustomAttribute(writer.Import(declaration.Constructor));
            var parameters = declaration.Constructor.GetParameters();
            for (var i = 0; i < declaration.FixedArguments.Count; i++)
            {
                attribute.ConstructorArguments.Add(Argument(parameters[i].ParameterType, declaration.FixedArguments[i]));
            }

            foreach (var (field, value) in declaration.NamedFields)
            {
                attribute.Fields.Add(new NamedArgument(field.Name, Argument(field.FieldType, value)));
            }

            foreach (var (property, value) in declaration.NamedProperties)
            {
                attribute.Properties.Add(new NamedArgument(property.Name, Argument(property.PropertyType, value)));
            }

            return attribute;
        }

        private CustomAttributeArgument Argument(Type declared, object? value)
        {
            if (declared == typeof(object))
            {
                // A boxed value carries its own type in the blob.
                var actual = value switch
                {
                    null => typeof(object),
                    Type => typeof(Type),
                    _ => value.GetType(),
                };
                return new CustomAttributeArgument(writer.Import(typeof(object)), value is null ? null : Argument(actual, value));
            }

            if (declared == typeof(Type))
            {
                return new CustomAttributeArgument(writer.Import(typeof(Type)), value is Type t ? writer.Import(t) : null);
            }

            if (declared.IsArray)
            {
                if (value is not Array array)
                {
                    return new CustomAttributeArgument(writer.Import(declared), null);
                }

                var element = declared.GetElementType()!;
                var items = new CustomAttributeArgument[array.Length];
                for (var i = 0; i < array.Length; i++)
                {
                    items[i] = Argument(element, array.GetValue(i));
                }

                return new CustomAttributeArgument(writer.Import(declared), items);
            }

            if (declared.IsEnum)
            {
                return new CustomAttributeArgument(writer.Import(declared), ConstantFor(value));
            }

            return new CustomAttributeArgument(writer.Import(declared), value);
        }
    }
}
