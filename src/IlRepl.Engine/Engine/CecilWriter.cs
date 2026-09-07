using System.Reflection;
using Mono.Cecil;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Engine;

/// <summary>
/// Builds one session assembly with Mono.Cecil. It imports every framework and session member a
/// body reaches for, records which session assemblies were referenced so the loader can keep
/// them alive, and grants the assembly access to those session assemblies through
/// <c>IgnoresAccessChecksTo</c>, because session assemblies have names nobody can grant to
/// in advance.
/// </summary>
public sealed class CecilWriter
{
    private readonly Dictionary<string, DefinitionAssembly> _dependencies = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, TypeReference> _definedTypes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FieldInfo, FieldReference> _definedFields = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MethodBase, MethodReference> _definedMethods = new(ReferenceEqualityComparer.Instance);
    private MethodDefinition? _ignoresAccessChecksConstructor;

    /// <summary>
    /// Starts an assembly.
    /// </summary>
    /// <param name="kind">What the assembly will hold.</param>
    public CecilWriter(SessionAssemblyKind kind)
    {
        Kind = kind;
        Name = SessionAssemblies.NextName(kind);
        Assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(Name, SessionAssemblies.Version), Name, ModuleKind.Dll);
        ReferenceCoreLibrary();
    }

    /// <summary>
    /// Starts an assembly with a name of the caller's choosing, for an export.
    /// </summary>
    /// <param name="name">The simple name.</param>
    public CecilWriter(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Kind = SessionAssemblyKind.Cell;
        Name = name;
        IsExport = true;
        Assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0, 0, 0)), name, ModuleKind.Dll);
        ReferenceCoreLibrary();
    }

    /// <summary>
    /// Cecil's default core library for a new module is mscorlib 4.0, which the desktop resolves
    /// through a facade and the browser bundle does not carry. Referencing the runtime's own core
    /// library first makes the module's type system name that instead.
    /// </summary>
    private void ReferenceCoreLibrary() => Module.ImportReference(typeof(object));

    /// <summary>
    /// True for an export, which carries every session type itself and may reference no session assembly.
    /// </summary>
    public bool IsExport { get; }

    /// <summary>
    /// What the assembly holds.
    /// </summary>
    public SessionAssemblyKind Kind { get; }

    /// <summary>
    /// The assembly's unique simple name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The assembly being built.
    /// </summary>
    public AssemblyDefinition Assembly { get; }

    /// <summary>
    /// The module being built.
    /// </summary>
    public ModuleDefinition Module => Assembly.MainModule;

    /// <summary>
    /// The session assemblies referenced so far.
    /// </summary>
    public IReadOnlyList<DefinitionAssembly> Dependencies => [.. _dependencies.Values];

    /// <summary>
    /// The <c>object</c> type reference.
    /// </summary>
    public TypeReference Object => Import(typeof(object));

    /// <summary>
    /// Records that a prototype builder or a generic parameter builder is written as a definition
    /// of this module, so bodies bound to the prototype emit against the real definition.
    /// </summary>
    /// <param name="prototype">The builder.</param>
    /// <param name="definition">The definition or generic parameter in this module.</param>
    public void Define(Type prototype, TypeReference definition)
    {
        ArgumentNullException.ThrowIfNull(prototype);
        ArgumentNullException.ThrowIfNull(definition);
        _definedTypes[prototype] = definition;
    }

    /// <summary>
    /// Records that a prototype field builder is written as a field of this module.
    /// </summary>
    /// <param name="prototype">The builder.</param>
    /// <param name="definition">The field definition.</param>
    public void Define(FieldInfo prototype, FieldReference definition)
    {
        ArgumentNullException.ThrowIfNull(prototype);
        ArgumentNullException.ThrowIfNull(definition);
        _definedFields[prototype] = definition;
    }

    /// <summary>
    /// Records that a prototype method builder is written as a method of this module.
    /// </summary>
    /// <param name="prototype">The builder.</param>
    /// <param name="definition">The method definition.</param>
    public void Define(MethodBase prototype, MethodReference definition)
    {
        ArgumentNullException.ThrowIfNull(prototype);
        ArgumentNullException.ThrowIfNull(definition);
        _definedMethods[prototype] = definition;
    }

    /// <summary>
    /// Imports a type, noting a session assembly it comes from. A prototype maps to its
    /// definition here; constructed types are rebuilt around their imported parts.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The reference.</returns>
    public TypeReference Import(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (_definedTypes.TryGetValue(type, out var defined))
        {
            return defined;
        }

        if (type.IsByRef)
        {
            return new ByReferenceType(Import(type.GetElementType()!));
        }

        if (type.IsPointer)
        {
            return new PointerType(Import(type.GetElementType()!));
        }

        if (type.IsArray)
        {
            var element = Import(type.GetElementType()!);
            if (type.IsSZArray)
            {
                return new ArrayType(element);
            }

            var array = new ArrayType(element, type.GetArrayRank());
            for (var i = 0; i < array.Rank; i++)
            {
                array.Dimensions[i] = new ArrayDimension(0, null);
            }

            return array;
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var instance = new GenericInstanceType(Import(type.GetGenericTypeDefinition()));
            foreach (var argument in type.GetGenericArguments())
            {
                instance.GenericArguments.Add(Import(argument));
            }

            return instance;
        }

        NoteSessionMembers(type);
        return Module.ImportReference(type);
    }

    /// <summary>
    /// Imports a field, noting a session assembly it comes from. A prototype field maps to its
    /// definition; a field of an instantiated prototype becomes a reference on the instantiation.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <returns>The reference.</returns>
    public FieldReference Import(FieldInfo field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (_definedFields.TryGetValue(field, out var defined))
        {
            return defined;
        }

        var declaring = field.DeclaringType;
        if (declaring is { IsGenericType: true, IsGenericTypeDefinition: false } && _definedTypes.TryGetValue(declaring.GetGenericTypeDefinition(), out var definitionType))
        {
            var definitionField = definitionType.Resolve()!.Fields.First(f => f.Name == field.Name);
            return new FieldReference(field.Name, definitionField.FieldType, Import(declaring));
        }

        if (declaring is { IsGenericType: true, IsGenericTypeDefinition: false } && MentionsBuilder(declaring))
        {
            // A field of a loaded generic type instantiated with a prototype's parameters.
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var definitionField = declaring.GetGenericTypeDefinition().GetField(field.Name, all)
                ?? throw new ReplException($"{TypeNameFormatter.Pretty(declaring)} has no field {field.Name}");
            var onDefinition = Import(definitionField);
            return new FieldReference(field.Name, onDefinition.FieldType, Import(declaring));
        }

        NoteSessionMembers(declaring);
        NoteSessionMembers(field.FieldType);
        return Module.ImportReference(field);
    }

    /// <summary>
    /// Imports a member of a type, where the member may be a prototype builder and the declaring
    /// type an instantiation of the prototype.
    /// </summary>
    /// <param name="method">The method or constructor.</param>
    /// <param name="declaring">The declaring type as referenced, or null for the method's own.</param>
    /// <returns>The reference.</returns>
    public MethodReference Import(MethodBase method, Type? declaring)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (_definedMethods.TryGetValue(method, out var defined))
        {
            if (declaring is { IsGenericType: true, IsGenericTypeDefinition: false })
            {
                var onInstance = new MethodReference(defined.Name, defined.ReturnType, Import(declaring))
                {
                    HasThis = defined.HasThis,
                    ExplicitThis = defined.ExplicitThis,
                    CallingConvention = defined.CallingConvention,
                };
                foreach (var parameter in defined.Parameters)
                {
                    onInstance.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
                }

                foreach (var parameter in defined.GenericParameters)
                {
                    onInstance.GenericParameters.Add(new GenericParameter(parameter.Name, onInstance));
                }

                return onInstance;
            }

            return defined;
        }

        return Import(method);
    }

    /// <summary>
    /// Imports a method or constructor, noting a session assembly it comes from. A member of a
    /// loaded type instantiated with a prototype's parameters, or a generic method instantiated
    /// with them, is rebuilt here, since the reflection importer cannot see a builder.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <returns>The reference.</returns>
    public MethodReference Import(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (_definedMethods.TryGetValue(method, out var defined))
        {
            return defined;
        }

        if (method is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } instantiated && (instantiated.GetGenericArguments().Any(MentionsBuilder) || _definedMethods.ContainsKey(instantiated.GetGenericMethodDefinition()) || IsDefinedInstantiationMember(instantiated.GetGenericMethodDefinition())))
        {
            var generic = new GenericInstanceMethod(Import(instantiated.GetGenericMethodDefinition()));
            foreach (var argument in instantiated.GetGenericArguments())
            {
                generic.GenericArguments.Add(Import(argument));
            }

            return generic;
        }

        if (method.DeclaringType is { IsGenericType: true, IsGenericTypeDefinition: false } definedInstance && _definedTypes.ContainsKey(definedInstance.GetGenericTypeDefinition()) && method is not System.Reflection.Emit.MethodBuilder and not System.Reflection.Emit.ConstructorBuilder)
        {
            // A member of a loaded session type, reached through an instantiation: the definition
            // was written here, so the reference is built on the written instantiation.
            var definitionMethod = method.Module.ResolveMethod(method.MetadataToken)!;
            if (_definedMethods.TryGetValue(definitionMethod, out var written))
            {
                var onWritten = new MethodReference(written.Name, written.ReturnType, Import(definedInstance))
                {
                    HasThis = written.HasThis,
                    ExplicitThis = written.ExplicitThis,
                    CallingConvention = written.CallingConvention,
                };
                foreach (var parameter in written.Parameters)
                {
                    onWritten.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
                }

                foreach (var parameter in written.GenericParameters)
                {
                    onWritten.GenericParameters.Add(new GenericParameter(parameter.Name, onWritten));
                }

                return onWritten;
            }
        }

        if (method.DeclaringType is { IsGenericType: true, IsGenericTypeDefinition: false } declaring && MentionsBuilder(declaring) && !(_definedTypes.ContainsKey(declaring.GetGenericTypeDefinition())))
        {
            var definitionType = declaring.GetGenericTypeDefinition();
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var wanted = method.GetParameters().Select(p => p.ParameterType).ToArray();
            var definitionMethod = definitionType.GetMembers(all).OfType<MethodBase>()
                .FirstOrDefault(m => m.Name == method.Name && m.IsStatic == method.IsStatic && m is ConstructorInfo == method is ConstructorInfo
                    && m.GetParameters().Length == wanted.Length && m.GetParameters().Zip(wanted).All(p => TypeIdentity.Equal(p.First.ParameterType, p.Second)))
                ?? throw new ReplException($"{TypeNameFormatter.Pretty(declaring)}::{method.Name} has no definition on {TypeNameFormatter.Pretty(definitionType)}");
            var onDefinition = Import(definitionMethod);
            var onInstance = new MethodReference(onDefinition.Name, onDefinition.ReturnType, Import(declaring))
            {
                HasThis = onDefinition.HasThis,
                ExplicitThis = onDefinition.ExplicitThis,
                CallingConvention = onDefinition.CallingConvention,
            };
            foreach (var parameter in onDefinition.Parameters)
            {
                onInstance.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
            }

            foreach (var parameter in onDefinition.GenericParameters)
            {
                onInstance.GenericParameters.Add(new GenericParameter(parameter.Name, onInstance));
            }

            return onInstance;
        }

        NoteSessionMembers(method.DeclaringType);
        foreach (var parameter in method.GetParameters())
        {
            NoteSessionMembers(parameter.ParameterType);
        }

        if (method is MethodInfo info)
        {
            NoteSessionMembers(info.ReturnType);
            if (info.IsGenericMethod && !info.IsGenericMethodDefinition)
            {
                foreach (var argument in info.GetGenericArguments())
                {
                    NoteSessionMembers(argument);
                }
            }
        }

        var reference = Module.ImportReference(method);
        if (method.CallingConvention.HasFlag(CallingConventions.VarArgs) && reference.CallingConvention != MethodCallingConvention.VarArg)
        {
            // The reflection importer drops the vararg convention, and a reference without it
            // names a method that does not exist.
            reference.CallingConvention = MethodCallingConvention.VarArg;
        }

        return reference;
    }

    /// <summary>
    /// Defines a top-level type in the module.
    /// </summary>
    /// <param name="ns">The namespace, or empty.</param>
    /// <param name="name">The type name.</param>
    /// <param name="attributes">The type attributes.</param>
    /// <param name="baseType">The base type reference, or null for an interface.</param>
    /// <returns>The definition.</returns>
    public TypeDefinition DefineType(string ns, string name, TypeAttributes attributes, TypeReference? baseType)
    {
        ArgumentNullException.ThrowIfNull(name);
        var type = new TypeDefinition(ns, name, attributes, baseType);
        Module.Types.Add(type);
        return type;
    }

    /// <summary>
    /// Writes the assembly to an image.
    /// </summary>
    /// <returns>The PE image.</returns>
    public byte[] Write()
    {
        using var stream = new MemoryStream();
        Assembly.Write(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Writes the assembly and loads it into a context of its own.
    /// </summary>
    /// <returns>The registered assembly.</returns>
    public DefinitionAssembly Load() => SessionAssemblies.Load(Write(), Name, Kind, Dependencies);

    /// <summary>
    /// Grants this assembly access to every member of a session assembly. The attribute type is
    /// defined in this module, as the runtime expects.
    /// </summary>
    /// <param name="dependency">The session assembly.</param>
    public void GrantAccessTo(DefinitionAssembly dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        if (_dependencies.ContainsKey(dependency.Name))
        {
            return;
        }

        _dependencies[dependency.Name] = dependency;
        _ignoresAccessChecksConstructor ??= DefineIgnoresAccessChecksAttribute();
        var attribute = new CustomAttribute(_ignoresAccessChecksConstructor);
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(Module.TypeSystem.String, dependency.Name));
        Assembly.CustomAttributes.Add(attribute);
    }

    private bool IsDefinedInstantiationMember(MethodBase definition) =>
        definition.DeclaringType is { IsGenericType: true, IsGenericTypeDefinition: false } declaring && _definedTypes.ContainsKey(declaring.GetGenericTypeDefinition());

    /// <summary>
    /// True when a builder, a prototype or one of its generic parameters, appears anywhere in the type.
    /// </summary>
    private static bool MentionsBuilder(Type type)
    {
        while (type.HasElementType)
        {
            type = type.GetElementType()!;
        }

        if (type is System.Reflection.Emit.TypeBuilder or System.Reflection.Emit.GenericTypeParameterBuilder)
        {
            return true;
        }

        if (type.IsGenericParameter)
        {
            return type.DeclaringMethod is System.Reflection.Emit.MethodBuilder || type.DeclaringType is System.Reflection.Emit.TypeBuilder;
        }

        return type.IsGenericType && !type.IsGenericTypeDefinition && (MentionsBuilder(type.GetGenericTypeDefinition()) || type.GetGenericArguments().Any(MentionsBuilder));
    }

    private void NoteSessionMembers(Type? type)
    {
        while (type is not null)
        {
            if (type.HasElementType)
            {
                type = type.GetElementType();
                continue;
            }

            if (type.IsConstructedGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    NoteSessionMembers(argument);
                }

                type = type.GetGenericTypeDefinition();
            }

            if (!type.IsGenericParameter && SessionAssemblies.TryGetDefinition(type.Assembly, out var definition))
            {
                if (IsExport)
                {
                    throw new ReplException($"{TypeNameFormatter.Pretty(type)} belongs to session assembly {definition.Name}, which an export cannot reference (it is not part of the session's types)");
                }

                GrantAccessTo(definition);
            }

            return;
        }
    }

    private MethodDefinition DefineIgnoresAccessChecksAttribute()
    {
        var attribute = DefineType("System.Runtime.CompilerServices", "IgnoresAccessChecksToAttribute", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed, Module.ImportReference(typeof(Attribute)));
        var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, Module.TypeSystem.Void);
        ctor.Parameters.Add(new ParameterDefinition("assemblyName", ParameterAttributes.None, Module.TypeSystem.String));
        var il = ctor.Body.GetILProcessor();
        il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
        il.Emit(Mono.Cecil.Cil.OpCodes.Call, Module.ImportReference(typeof(Attribute).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!));
        il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
        attribute.Methods.Add(ctor);
        return ctor;
    }
}
