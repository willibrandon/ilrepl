using System.Globalization;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using MethodImplAttributes = Mono.Cecil.MethodImplAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Engine;

/// <summary>
/// Provides a stable method entry point whose implementation can be bound or replaced at an activation boundary.
/// </summary>
/// <remarks>
/// The stable entry point of a session method. Every caller, whether a cell, another session
/// method, or a session type, binds to <see cref="Method"/>, which forwards to whichever version
/// is bound through a delegate held in a private static field. A delegate to a static method
/// keeps that method's assembly alive, so a version lives exactly as long as something can
/// still reach it: this field, a delegate a user retained, or a call in flight.
/// </remarks>
public sealed class MethodTrampoline
{
    private readonly Lazy<Action<Delegate>> _bind;
    private readonly FieldInfo _implementationField;

    private MethodTrampoline(
        MethodSignature signature,
        DefinitionAssembly definition,
        MethodInfo method,
        Type delegateType,
        FieldInfo implementationField,
        Func<Action<Delegate>> bind)
    {
        Signature = signature;
        Definition = definition;
        Method = method;
        DelegateType = delegateType;
        _implementationField = implementationField;
        _bind = new Lazy<Action<Delegate>>(bind);
    }

    /// <summary>
    /// The signature the trampoline was created for.
    /// </summary>
    public MethodSignature Signature { get; }

    /// <summary>
    /// The trampoline's assembly.
    /// </summary>
    public DefinitionAssembly Definition { get; }

    /// <summary>
    /// The public static method callers bind to, declared on <c>IlRepl.Cell</c>.
    /// </summary>
    public MethodInfo Method { get; }

    /// <summary>
    /// The delegate type a version is bound through; its signature is the method's.
    /// </summary>
    public Type DelegateType { get; }

    /// <summary>
    /// Writes and loads a trampoline for a signature. Nothing is bound yet.
    /// </summary>
    /// <param name="signature">The signature.</param>
    /// <returns>The trampoline.</returns>
    /// <exception cref="ReplException">The runtime refused the signature.</exception>
    public static MethodTrampoline Create(MethodSignature signature) => Create(signature, null);

    /// <summary>
    /// Creates a trampoline whose signature may reference prototypes of families emitted in the same group.
    /// </summary>
    /// <param name="signature">The signature.</param>
    /// <param name="externals">Prototypes of the group, referenced by their assembly names, or null.</param>
    /// <returns>The trampoline.</returns>
    /// <exception cref="ReplException">The runtime refused the signature.</exception>
    public static MethodTrampoline Create(MethodSignature signature, IReadOnlyDictionary<Type, CecilWriter.ExternalPrototype>? externals)
    {
        ArgumentNullException.ThrowIfNull(signature);
        var writer = new CecilWriter(SessionAssemblyKind.Trampoline);
        foreach (var (prototype, external) in externals ?? new Dictionary<Type, CecilWriter.ExternalPrototype>())
        {
            writer.DefineExternal(prototype, external);
        }

        var cell = writer.DefineType("IlRepl", "Cell",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            writer.Object);
        var returnType = writer.ImportSignature(
            signature.ReturnType,
            signature.ExactReturnType,
            signature.ReturnRequiredModifiers,
            signature.ReturnOptionalModifiers);
        var parameterTypes = signature.Parameters.Select((parameter, index) => writer.ImportSignature(
            parameter.Type,
            parameter.ExactType,
            parameter.RequiredModifiers,
            parameter.OptionalModifiers)).ToArray();

        var delegateType = new TypeDefinition("", signature.Name + "Delegate",
            TypeAttributes.NestedPublic | TypeAttributes.Sealed | TypeAttributes.Class, writer.Import(typeof(MulticastDelegate)));
        cell.NestedTypes.Add(delegateType);
        var constructor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            writer.Module.TypeSystem.Void)
        {
            ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed,
        };

        constructor.Parameters.Add(new ParameterDefinition("object", ParameterAttributes.None, writer.Object));
        constructor.Parameters.Add(new ParameterDefinition("method", ParameterAttributes.None, writer.Module.TypeSystem.IntPtr));
        delegateType.Methods.Add(constructor);
        var invoke = new MethodDefinition("Invoke",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual, returnType)
        {
            ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed,
        };

        AddParameters(invoke, signature, parameterTypes);
        delegateType.Methods.Add(invoke);

        var field = new FieldDefinition(signature.Name + "Impl", FieldAttributes.Private | FieldAttributes.Static, delegateType);
        cell.Fields.Add(field);

        var bind = new MethodDefinition("Bind", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            writer.Module.TypeSystem.Void);
        bind.Parameters.Add(new ParameterDefinition("impl", ParameterAttributes.None, writer.Import(typeof(Delegate))));
        var il = bind.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, delegateType);
        il.Emit(OpCodes.Volatile);
        il.Emit(OpCodes.Stsfld, field);
        il.Emit(OpCodes.Ret);
        cell.Methods.Add(bind);

        var method = new MethodDefinition(signature.Name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            returnType);
        AddParameters(method, signature, parameterTypes);
        il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Volatile);
        il.Emit(OpCodes.Ldsfld, field);
        for (var i = 0; i < parameterTypes.Length; i++)
        {
            il.Emit(OpCodes.Ldarg, method.Parameters[i]);
        }

        il.Emit(OpCodes.Callvirt, invoke);
        il.Emit(OpCodes.Ret);
        cell.Methods.Add(method);

        var definition = writer.Load();
        var type = definition.Assembly.GetType("IlRepl.Cell")
            ?? throw new ReplException($"the trampoline for {signature.Name} did not load");
        var loaded = type.GetMethod(signature.Name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new ReplException($"the trampoline for {signature.Name} has no entry point");
        var loadedDelegate = type.GetNestedType(signature.Name + "Delegate")
            ?? throw new ReplException($"the trampoline for {signature.Name} has no delegate type");
        var loadedBind = type.GetMethod("Bind", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new ReplException($"the trampoline for {signature.Name} has no binder");
        var loadedField = type.GetField(field.Name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new ReplException($"the trampoline for {signature.Name} has no implementation field");

        // The emitted cast preserves type safety without a separate generic binder instantiation for every method.
        return new MethodTrampoline(signature, definition, loaded, loadedDelegate, loadedField,
            loadedBind.CreateDelegate<Action<Delegate>>);
    }

    /// <summary>
    /// Binds one implementation while existing callers finish on the version they entered with.
    /// </summary>
    /// <param name="implementation">A delegate of <see cref="DelegateType"/> over the version's body.</param>
    public void Bind(Delegate implementation)
    {
        ArgumentNullException.ThrowIfNull(implementation);
        _bind.Value(implementation);
    }

    /// <summary>
    /// Publishes a reconstructed implementation before execution without compiling a separate setter for each method.
    /// </summary>
    /// <param name="implementation">A delegate with the exact generated signature.</param>
    internal void BindInitial(Delegate implementation)
    {
        ArgumentNullException.ThrowIfNull(implementation);
        if (implementation.GetType() != DelegateType)
        {
            throw new InvalidCastException("the implementation does not have the trampoline's delegate type");
        }

        // The barrier also covers the runtime's first reflection setter, before its shared accessor is initialized.
        // Readers acquire the reference through the trampoline's volatile load.
        Thread.MemoryBarrier();
        _implementationField.SetValue(null, implementation);
    }

    /// <summary>
    /// Prepares the typed volatile setter before entering a replacement's publication phase.
    /// </summary>
    internal void PrepareBinding()
    {
        var bind = _bind.Value;
        if (MethodPreparation.IsSupported)
        {
            MethodPreparation.Prepare(bind.Method);
        }
    }

    /// <summary>
    /// Restores an unchanged captured trampoline without binding or compiling its implementation.
    /// </summary>
    /// <param name="signature">The resolved callable signature.</param>
    /// <param name="method">The loaded trampoline.</param>
    /// <returns>The inactive callable binding.</returns>
    internal static MethodTrampoline Restore(MethodSignature signature, MethodInfo method)
    {
        if (!SessionAssemblies.TryGetDefinition(method.Module.Assembly, out var definition))
        {
            throw new ReplException("the captured trampoline has no owned assembly");
        }

        var field = method.DeclaringType!.GetField(method.Name + "Impl", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new ReplException("the captured trampoline has no implementation field");
        return new MethodTrampoline(signature, definition, method, field.FieldType, field, () => value => field.SetValue(null, value));
    }

    private static void AddParameters(MethodDefinition method, MethodSignature signature, TypeReference[] parameterTypes)
    {
        for (var i = 0; i < parameterTypes.Length; i++)
        {
            var name = signature.Parameters[i].Name ?? ("arg" + i.ToString(CultureInfo.InvariantCulture));
            method.Parameters.Add(new ParameterDefinition(name, ParameterAttributes.None, parameterTypes[i]));
        }
    }
}
