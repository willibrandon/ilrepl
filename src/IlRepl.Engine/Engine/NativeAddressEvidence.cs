using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Observes known runtime handles and prepares address probes without executing inspected code or helpers.
/// </summary>
public sealed class NativeAddressEvidence : IDisposable
{
    private readonly List<GCHandle> _pins = [];
    private readonly List<NativeAddressFact> _facts = [];
    private readonly List<NativeProbe> _probes = [];
    private readonly HashSet<ulong> _constants = [];
    private readonly HashSet<MemberInfo> _seen = [];
    private readonly AssemblyBuilder _assembly;
    private readonly ModuleBuilder _module;
    private readonly HashSet<Assembly> _granted = [];
    private readonly Dictionary<(string Kind, string Symbol), string> _display = [];
    private readonly List<(string Kind, string Symbol, Action<ILGenerator> Emit)> _pending = [];
    private int _next;

    /// <summary>
    /// Creates a separately identified probe assembly in the worker's requested loading context.
    /// </summary>
    public NativeAddressEvidence()
    {
        _assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ilrepl.native.probes." + Guid.NewGuid().ToString("N")),
            AssemblyLifetimeScope.Collectible ? AssemblyBuilderAccess.RunAndCollect : AssemblyBuilderAccess.Run);
        _module = _assembly.DefineDynamicModule("probes");
    }

    /// <summary>
    /// The proven address identities, retained while their string observations remain pinned.
    /// </summary>
    public NativeAddressFact[] Facts => [.. _facts];

    /// <summary>
    /// The known compilation-only return-value probes.
    /// </summary>
    public NativeProbe[] Probes => [.. _probes];

    /// <summary>
    /// The original integer and floating-point bit patterns seen in inspected IL.
    /// </summary>
    public ulong[] Constants => [.. _constants];

    /// <summary>
    /// Collects operand evidence after the selected compilation and authorized workload have completed.
    /// </summary>
    /// <param name="context">The initialized permission boundary and exact target metadata.</param>
    /// <param name="listings">Selected listings already flushed by the runtime, if available.</param>
    /// <param name="architecture">The worker's instruction architecture.</param>
    public void Collect(NativeWorkerContext context, IReadOnlyList<NativeCompilation>? listings = null, string architecture = "")
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RequireActivationPermission();
        Scan(context.Method, 0);
        if (listings is { Count: > 0 } && listings.All(listing => NativeNormalizer.Normalize(listing, _facts, [], listings,
            architecture, Constants, NativeCapture.Identify(context.Method).ReturnsPointer).Problems.Length == 0)) return;
        // Only relevant IL operands are candidates; buffered runtimes may not expose their selected listing until shutdown.
        foreach (var (kind, symbol, emit) in _pending) CompileProbe(kind, symbol, emit);
    }

    /// <summary>
    /// Releases pins after native output and address evidence have been saved.
    /// </summary>
    public void Dispose()
    {
        foreach (var pin in _pins) pin.Free();
        _pins.Clear();
    }

    private void Scan(MethodBase method, int depth)
    {
        if (!_seen.Add(method) || method.ContainsGenericParameters) return;
        var symbol = MethodSymbol(method);
        Add((ulong)method.MethodHandle.Value, "method-handle", symbol, "RuntimeMethodHandle.Value", MemberResolver.Describe(method));
        ObserveEntryPoint(method, symbol);
        if (method.DeclaringType is { } owner) ObserveType(owner);
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null || depth > 16) return;
        var typeArguments = method.DeclaringType?.GetGenericArguments();
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        foreach (var instruction in IlReader.Read(bytes).Instructions)
        {
            var token = instruction.Operand.Token;
            if (instruction.Op.Name.StartsWith("ldc.", StringComparison.Ordinal))
            {
                _constants.Add(unchecked((ulong)instruction.Operand.Integer));
                _constants.Add(instruction.Operand.Bits32);
                _constants.Add(instruction.Operand.Bits64);
            }
            if (instruction.Op.OperandType == OperandType.InlineString)
            {
                var value = method.Module.ResolveString(token);
                object reference = value;
                var pin = GCHandle.Alloc(reference, GCHandleType.Pinned);
                _pins.Add(pin);
                var identity = LiteralParser.Escape(value);
                Add((ulong)Unsafe.As<object, nuint>(ref reference), "string-object", identity, "pinned resolved ldstr object reference");
                _facts.Add(new NativeAddressFact
                {
                    Address = (ulong)pin.AddrOfPinnedObject(), Length = checked((ulong)(value.Length + 1) * 2),
                    Kind = "string-data", Symbol = identity, Evidence = "GCHandle.AddrOfPinnedObject for the resolved ldstr string",
                });
                Probe("string-object", identity, il => il.Emit(OpCodes.Ldstr, value));
                continue;
            }
            if (instruction.Op.OperandType is not (OperandType.InlineField or OperandType.InlineMethod
                or OperandType.InlineType or OperandType.InlineTok)) continue;
            var member = method.Module.ResolveMember(token, typeArguments, methodArguments);
            switch (member)
            {
                case Type type: ObserveType(type); break;
                case FieldInfo field:
                    ObserveType(field.DeclaringType!);
                    if (!_seen.Add(field)) break;
                    var fieldSymbol = field.DeclaringType!.AssemblyQualifiedName + "::" + field.Name + ":" + field.FieldType;
                    var fieldDisplay = TypeNameFormatter.Pretty(field.FieldType) + " " + TypeNameFormatter.Pretty(field.DeclaringType)
                        + "::" + TypeNameFormatter.IlAsmIdentifier(field.Name);
                    Add((ulong)field.FieldHandle.Value, "field-handle", fieldSymbol, "RuntimeFieldHandle.Value", fieldDisplay);
                    if (field.IsStatic && !field.IsLiteral)
                    {
                        Grant(field.Module.Assembly);
                        Probe("static-field", fieldSymbol, il => il.Emit(OpCodes.Ldsflda, field), fieldDisplay);
                    }
                    break;
                case MethodBase callee when !callee.ContainsGenericParameters:
                    if (callee.DeclaringType is { } declaring) ObserveType(declaring);
                    Add((ulong)callee.MethodHandle.Value, "method-handle", MethodSymbol(callee), "RuntimeMethodHandle.Value",
                        MemberResolver.Describe(callee));
                    ObserveEntryPoint(callee, MethodSymbol(callee));
                    // Follow captured user callees for operands introduced through inlining; framework code remains event-attributed.
                    if (SessionAssemblies.IsSessionAssembly(callee.Module.Assembly)) Scan(callee, depth + 1);
                    break;
            }
        }
    }

    private void ObserveEntryPoint(MethodBase method, string symbol)
    {
        // GetFunctionPointer can activate a module, so callers enter only after the complete activation check.
        var entry = method.MethodHandle.GetFunctionPointer();
        var display = MemberResolver.Describe(method);
        Add((ulong)entry, "entry-point", symbol, "RuntimeMethodHandle.GetFunctionPointer", display);
        var cell = NativeEntryPoint.IndirectionCell(entry);
        Add((ulong)cell, "entry-point-cell", symbol, "decoded PC-relative target load in the runtime method entry stub", display);
    }

    private void ObserveType(Type type)
    {
        if (type.ContainsGenericParameters || !_seen.Add(type)) return;
        Add((ulong)type.TypeHandle.Value, "type-handle", type.AssemblyQualifiedName!, "RuntimeTypeHandle.Value",
            TypeNameFormatter.Pretty(type));
        Grant(type.Assembly);
        Probe("type-object", type.AssemblyQualifiedName!, il =>
        {
            il.Emit(OpCodes.Ldtoken, type);
            il.Emit(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
        }, TypeNameFormatter.Pretty(type));
    }

    private void Probe(string kind, string symbol, Action<ILGenerator> emit, string display = "")
    {
        _pending.Add((kind, symbol, emit));
        _display[(kind, symbol)] = display;
    }

    private void CompileProbe(string kind, string symbol, Action<ILGenerator> emit)
    {
        var name = "Address" + _next++;
        var type = _module.DefineType("IlRepl.NativeProbe" + name, TypeAttributes.Public | TypeAttributes.Abstract
            | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit);
        var method = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, typeof(nint), Type.EmptyTypes);
        method.SetImplementationFlags(MethodImplAttributes.NoInlining | MethodImplAttributes.AggressiveOptimization);
        var il = method.GetILGenerator();
        emit(il);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Ret);
        var compiled = type.CreateType()!.GetMethod(name)!;
        _probes.Add(new NativeProbe { Method = compiled.DeclaringType!.FullName + ":" + name, Kind = kind, Symbol = symbol,
            DisplaySymbol = _display.GetValueOrDefault((kind, symbol), "") });
        RuntimeHelpers.PrepareMethod(compiled.MethodHandle);
    }

    private void Grant(Assembly assembly)
    {
        if (!_granted.Add(assembly)) return;
        // One attribute definition per probe assembly; subsequent grants reuse its constructor.
        var attributeName = "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute";
        var attribute = _assembly.GetType(attributeName);
        if (attribute is null)
        {
            var builder = _module.DefineType(attributeName, TypeAttributes.Public | TypeAttributes.Class, typeof(Attribute));
            var constructor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(string)]);
            var il = constructor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!);
            il.Emit(OpCodes.Ret);
            attribute = builder.CreateType();
        }
        _assembly.SetCustomAttribute(new CustomAttributeBuilder(attribute!.GetConstructor([typeof(string)])!, [assembly.GetName().Name]));
    }

    private void Add(ulong address, string kind, string symbol, string evidence, string display = "")
    {
        if (address != 0) _facts.Add(new NativeAddressFact
            { Address = address, Kind = kind, Symbol = symbol, DisplaySymbol = display, Evidence = evidence });
    }

    private static string MethodSymbol(MethodBase method) => method.Module.Assembly.FullName + "!" + MemberResolver.Describe(method);
}
