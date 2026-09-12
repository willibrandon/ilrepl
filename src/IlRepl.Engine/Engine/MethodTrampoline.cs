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
/// The stable entry point of a session method. Every caller, whether a cell, another session
/// method, or a session type, binds to <see cref="Method"/>, which forwards to whichever version
/// is bound through a delegate held in a private static field. A delegate to a static method
/// keeps that method's assembly alive, so a version lives exactly as long as something can
/// still reach it: this field, a delegate a user retained, or a call in flight.
/// </summary>
public sealed class MethodTrampoline
{
    private readonly Action<Delegate> _bind;

    private MethodTrampoline(MethodSignature signature, DefinitionAssembly definition, MethodInfo method, Type delegateType, Action<Delegate> bind)
    {
        Signature = signature;
        Definition = definition;
        Method = method;
        DelegateType = delegateType;
        _bind = bind;
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
    /// Creates the trampoline for a signature that may mention prototypes of families written in
    /// the same group.
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

        var cell = writer.DefineType("IlRepl", "Cell", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit, writer.Object);
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

        var delegateType = new TypeDefinition("", signature.Name + "Delegate", TypeAttributes.NestedPublic | TypeAttributes.Sealed | TypeAttributes.Class, writer.Import(typeof(MulticastDelegate)));
        cell.NestedTypes.Add(delegateType);
        var constructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, writer.Module.TypeSystem.Void)
        {
            ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed,
        };
        constructor.Parameters.Add(new ParameterDefinition("object", ParameterAttributes.None, writer.Object));
        constructor.Parameters.Add(new ParameterDefinition("method", ParameterAttributes.None, writer.Module.TypeSystem.IntPtr));
        delegateType.Methods.Add(constructor);
        var invoke = new MethodDefinition("Invoke", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual, returnType)
        {
            ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed,
        };
        AddParameters(invoke, signature, parameterTypes);
        delegateType.Methods.Add(invoke);

        var field = new FieldDefinition(signature.Name + "Impl", FieldAttributes.Private | FieldAttributes.Static, delegateType);
        cell.Fields.Add(field);

        var bind = new MethodDefinition("Bind", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, writer.Module.TypeSystem.Void);
        bind.Parameters.Add(new ParameterDefinition("impl", ParameterAttributes.None, delegateType));
        var il = bind.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Volatile);
        il.Emit(OpCodes.Stsfld, field);
        il.Emit(OpCodes.Ret);
        cell.Methods.Add(bind);

        var method = new MethodDefinition(signature.Name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, returnType);
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
        var type = definition.Assembly.GetType("IlRepl.Cell") ?? throw new ReplException($"the trampoline for {signature.Name} did not load");
        var loaded = type.GetMethod(signature.Name, BindingFlags.Public | BindingFlags.Static) ?? throw new ReplException($"the trampoline for {signature.Name} has no entry point");
        var loadedDelegate = type.GetNestedType(signature.Name + "Delegate") ?? throw new ReplException($"the trampoline for {signature.Name} has no delegate type");
        var loadedBind = type.GetMethod("Bind", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new ReplException($"the trampoline for {signature.Name} has no binder");

        // Binding must not go through reflection once a commit is under way, so the typed Bind
        // is wrapped once here into a delegate that takes any delegate of the right type.
        var typed = loadedBind.CreateDelegate(typeof(Action<>).MakeGenericType(loadedDelegate));
        var shim = typeof(MethodTrampoline).GetMethod(nameof(BindCore), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(loadedDelegate);
        var binder = (Action<Delegate>)shim.CreateDelegate(typeof(Action<Delegate>), typed);
        return new MethodTrampoline(signature, definition, loaded, loadedDelegate, binder);
    }

    /// <summary>
    /// Points the trampoline at a version. One reference store; callers already inside a call
    /// finish on the version they entered with.
    /// </summary>
    /// <param name="implementation">A delegate of <see cref="DelegateType"/> over the version's body.</param>
    public void Bind(Delegate implementation)
    {
        ArgumentNullException.ThrowIfNull(implementation);
        _bind(implementation);
    }

    private static void AddParameters(MethodDefinition method, MethodSignature signature, TypeReference[] parameterTypes)
    {
        for (var i = 0; i < parameterTypes.Length; i++)
        {
            var name = signature.Parameters[i].Name ?? ("arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            method.Parameters.Add(new ParameterDefinition(name, ParameterAttributes.None, parameterTypes[i]));
        }
    }

    private static void BindCore<T>(Action<T> bind, Delegate implementation) where T : Delegate => bind((T)implementation);
}
