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
    }

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
    public TypeReference Object => Module.TypeSystem.Object;

    /// <summary>
    /// Imports a type, noting a session assembly it comes from.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The reference.</returns>
    public TypeReference Import(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        NoteSessionMembers(type);
        return Module.ImportReference(type);
    }

    /// <summary>
    /// Imports a field, noting a session assembly it comes from.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <returns>The reference.</returns>
    public FieldReference Import(FieldInfo field)
    {
        ArgumentNullException.ThrowIfNull(field);
        NoteSessionMembers(field.DeclaringType);
        NoteSessionMembers(field.FieldType);
        return Module.ImportReference(field);
    }

    /// <summary>
    /// Imports a method or constructor, noting a session assembly it comes from.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <returns>The reference.</returns>
    public MethodReference Import(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
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
