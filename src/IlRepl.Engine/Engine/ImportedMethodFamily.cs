using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using IlRepl.Engine.Binding;
using Mono.Cecil;
using GenericParameterAttributes = System.Reflection.GenericParameterAttributes;
using MethodAttributes = System.Reflection.MethodAttributes;
using MethodImplAttributes = System.Reflection.MethodImplAttributes;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Reproduces the reachable declaring context through ordinary parsing, validation, and emission.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static;
    private readonly Session _session;
    private readonly Dictionary<string, MethodInfo> _pinned;
    private readonly IReadOnlyList<MethodSignature> _signatures;
    private readonly TypeTable _sourceTypes;
    private readonly Dictionary<Type, string> _types = [];
    private readonly Dictionary<MethodBase, MethodEditBody?> _methods = [];
    private readonly List<EditDependency> _dependencies = [];
    private readonly List<(Type Type, string Location)> _constraints = [];
    private readonly Queue<MethodBase> _pending = new();
    private readonly HashSet<Type> _instantiated = [];
    private readonly Dictionary<MemberInfo, MemberInfo> _runtime = [];
    private readonly List<string> _problems = [];
    private MethodBase? _forwardingMethod;

    private ImportedMethodFamily(string name, Session session, IReadOnlyList<MethodSignature> signatures,
        Dictionary<string, MethodInfo> pinned, MethodEditBody selected, TypeTable sourceTypes)
    {
        Name = name;
        _session = session;
        _signatures = signatures;
        _pinned = pinned;
        _sourceTypes = sourceTypes;
        Selected = selected;
        try
        {
            Discover();
        }
        catch (ReplException exception)
        {
            _problems.Add(exception.Message);
        }
    }

    private void Discover()
    {
        AddMethod(Selected.Method);
        var requested = Selected.Listing.Requested;
        var arguments = requested.DeclaringType!.GetGenericArguments()
            .Concat(requested.IsGenericMethod ? requested.GetGenericArguments() : Type.EmptyTypes).ToArray();
        var parameters = Selected.Method.DeclaringType!.GetGenericArguments()
            .Concat(Selected.Method.IsGenericMethod ? Selected.Method.GetGenericArguments() : Type.EmptyTypes);
        foreach (var (argument, parameter) in arguments.Zip(parameters))
        {
            ConsiderType(argument, requested.DeclaringType);
            if (parameter.GenericParameterAttributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint)
                && _types.ContainsKey(DefinitionOf(argument)) && argument.GetConstructor(Type.EmptyTypes) is { } constructor)
            {
                AddMethod(constructor);
            }
        }

        if (!Selected.Method.IsStatic)
        {
            Instantiate(Selected.Method.DeclaringType!);
        }

        while (true)
        {
            while (_pending.TryDequeue(out var method))
            {
                if (method.IsAbstract || method.Attributes.HasFlag(MethodAttributes.PinvokeImpl)
                    || IsRuntimeDelegateMethod(method) || _metadataOnlyMethods.Contains(method)
                    || method.GetCustomAttributesData().Any(attribute =>
                    attribute.AttributeType == typeof(UnsafeAccessorAttribute)))
                {
                    continue;
                }

                MethodEditBody body;
                try
                {
                    body = method == Selected.Method ? Selected : MethodEditBody.Read(method, _session, _signatures, _sourceTypes);
                }
                catch (ReplException ex)
                {
                    _dependencies.Add(new EditDependency(MemberResolver.Describe(method), method.Module.Assembly.FullName!,
                        MemberResolver.Describe(Selected.Method), "blocked: " + ex.Message)
                    {
                        Access = MemberAccess.AccessWord(method.Attributes),
                    });
                    throw new ReplException($"cannot reproduce {MemberResolver.Describe(Selected.Method)}: "
                        + $"required helper {MemberResolver.Describe(method)}: {ex.Message}", ex);
                }

                _methods[method] = body;
                Scan(body);
            }

            // A later reference can require copying an owner that an earlier call retained externally.
            // Revisit those calls until every reference to a copied owner has its required definition.
            var typeCount = _types.Count;
            _dependencies.Clear();
            ScanSignatures();
            ScanMarshalling();
            ScanAttributes();
            foreach (var body in _methods.Values.OfType<MethodEditBody>().ToArray())
            {
                Scan(body);
            }

            ScanReflection();
            if (_pending.Count == 0 && typeCount == _types.Count)
            {
                break;
            }
        }

        foreach (var argument in arguments.Where(argument => !argument.IsGenericParameter))
        {
            ReportType(argument, MemberResolver.Describe(requested) + ": generic argument");
        }

        foreach (var (type, location) in _constraints)
        {
            ReportType(type, location);
        }

        ValidateBoundaries();
    }

    /// <summary>
    /// The edit name that scopes generated type names.
    /// </summary>
    internal string Name { get; }

    /// <summary>
    /// The selected method body for this family revision.
    /// </summary>
    internal MethodEditBody Selected { get; }

    /// <summary>
    /// The loaded assembly, or null until compilation succeeds.
    /// </summary>
    internal DefinitionAssembly? Definition { get; private set; }

    /// <summary>
    /// The selected compiled method with the requested generic instantiation.
    /// </summary>
    internal MethodBase? EntryPoint { get; private set; }

    /// <summary>
    /// The public entry used by the session alias, preserving the selected method's own metadata.
    /// </summary>
    internal MethodBase? CallableEntryPoint { get; private set; }

    /// <summary>
    /// The generated entry name, chosen outside the copied method names.
    /// </summary>
    private string ForwardingName
    {
        get
        {
            var name = "<ilrepl>_" + Name;
            while (_methods.Keys.Any(method => method.Name == name))
            {
                name += "_";
            }

            return name;
        }
    }

    /// <summary>
    /// The symbols and binding decisions reached while importing the family.
    /// </summary>
    internal IReadOnlyList<EditDependency> Dependencies => _dependencies;

    /// <summary>
    /// The preflight blockers retained with the editable source.
    /// </summary>
    internal IReadOnlyList<string> Problems => _problems;

    /// <summary>
    /// Every compiled type that must be registered in the session type table.
    /// </summary>
    internal IEnumerable<Type> RuntimeTypes => _runtime.Where(pair => pair.Key is Type).Select(pair => (Type)pair.Value);

    /// <summary>
    /// All declaring types whose callers must rebuild, including generated alias entry types.
    /// </summary>
    internal IEnumerable<Type> DependencyTypes => _forwardingMethod is { DeclaringType: { } owner }
        ? RuntimeTypes.Append(owner).Distinct() : RuntimeTypes;

    /// <summary>
    /// The original type identities that operand lookup must preserve across later session replacements.
    /// </summary>
    internal IEnumerable<Type> SourceTypes => _types.Keys;

    internal static ImportedMethodFamily Capture(string name, MethodBase method, Session session)
    {
        var signatures = session.Methods.Select(m => m.Signature).ToArray();
        var pinned = session.Methods.ToDictionary(m => m.Signature.Name, m => m.Version.Body, StringComparer.Ordinal);
        var types = session.TypeTable.Clone();
        return new ImportedMethodFamily(name, session, signatures, pinned, MethodEditBody.Read(method, session, signatures, types), types);
    }

    internal ImportedMethodFamily Revise(string source)
    {
        var types = _session.TypeTable.Clone();
        foreach (var type in SourceTypes)
        {
            types.Add(type.FullName!.Replace('+', '/'), type);
        }

        return new ImportedMethodFamily(Name, _session, _signatures, _pinned,
            MethodEditBody.Parse(Selected.Listing, source, _session, _signatures, types), _sourceTypes);
    }

    private static Type DefinitionOf(Type type) => type.IsGenericType ? type.GetGenericTypeDefinition() : type;

    private static bool IsRuntimeDelegateMethod(MethodBase method)
    {
        var implementation = method.GetMethodImplementationFlags();
        return typeof(MulticastDelegate).IsAssignableFrom(method.DeclaringType)
            && ((implementation & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.Runtime
                || implementation.HasFlag(MethodImplAttributes.InternalCall));
    }

    private void AddType(Type type)
    {
        type = DefinitionOf(type);
        if (_types.ContainsKey(type))
        {
            return;
        }

        if (type.IsPrimitive || type == typeof(string) || type == typeof(Array) || type == typeof(object)
            || type == typeof(ValueType) || type == typeof(Enum) || type.IsCOMObject || type.IsImport)
        {
            throw new ReplException(
                $"cannot reproduce the runtime-owned declaring type {TypeNameFormatter.Pretty(type)} in a session assembly");
        }

        if (type.DeclaringType is { } enclosing)
        {
            AddType(enclosing);
        }

        var suffix = _types.Count == 0 ? "Owner" : "Type" + _types.Count.ToString(CultureInfo.InvariantCulture);
        var name = type.DeclaringType is { } parent ? _types[DefinitionOf(parent)] + "/" + type.Name
            : "IlRepl.Edits." + Name + "." + suffix + (type.IsGenericTypeDefinition ? "`" + type.GetGenericArguments().Length : "");
        _types.Add(type, name);
        ConsiderConstraints(type.GetGenericArguments(), type, TypeNameFormatter.Pretty(type));
        if (type.BaseType is { } baseType)
        {
            ConsiderType(baseType, type);
        }

        foreach (var contract in type.GetInterfaces())
        {
            ConsiderType(contract, type);
        }

        foreach (var field in type.GetFields(Declared))
        {
            ScanSignature(RuntimeMetadataSignatures.Read(field), type, field + ": field signature");
        }

        if (type.TypeInitializer is { } initializer)
        {
            AddMethod(initializer);
        }

        foreach (var method in type.GetMethods(Declared).Where(method => method.IsVirtual))
        {
            AddMethod(method, metadataOnly: true);
        }
    }

    private void ConsiderType(Type type, Type from)
    {
        if (type.HasElementType)
        {
            ConsiderType(type.GetElementType()!, from);
        }
        else if (!type.IsGenericParameter)
        {
            if (type.IsConstructedGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    ConsiderType(argument, from);
                }
            }

            var definition = DefinitionOf(type);
            if (TypeRelations.IsSessionType(definition) || definition.Assembly == from.Assembly && !definition.IsVisible)
            {
                AddType(definition);
            }
        }
    }

    private void ConsiderConstraints(IEnumerable<Type> parameters, Type from, string member)
    {
        foreach (var parameter in parameters)
        {
            foreach (var constraint in parameter.GetGenericParameterConstraints())
            {
                _constraints.Add((constraint, member + ": constraint on " + parameter.Name));
                ConsiderType(constraint, from);
            }
        }
    }

    private void Instantiate(Type type)
    {
        type = DefinitionOf(type);
        if (!_instantiated.Add(type))
        {
            return;
        }

        AddType(type);
        foreach (var constructor in type.GetConstructors(Declared))
        {
            AddMethod(constructor, metadataOnly: true);
        }
    }

    private void AddMethod(MethodBase method, bool metadataOnly = false)
    {
        method = IlAsmRenderer.DefinitionOf(method);
        if (!metadataOnly && _metadataOnlyMethods.Remove(method))
        {
            _pending.Enqueue(method);
        }

        if (!_methods.TryAdd(method, null))
        {
            return;
        }

        if (metadataOnly && HasNonIlImplementation(method))
        {
            _metadataOnlyMethods.Add(method);
        }

        AddType(method.DeclaringType!);
        if (method.IsGenericMethodDefinition)
        {
            ConsiderConstraints(method.GetGenericArguments(), method.DeclaringType!, MemberResolver.Describe(method));
        }

        _pending.Enqueue(method);
        ScanMethodSignature(method, method == Selected.Method ? Selected : null);
    }

    private void Scan(MethodEditBody body)
    {
        foreach (var parameter in body.State.Signature!.TypeParameters)
        {
            foreach (var constraint in parameter.Constraints)
            {
                ConsiderType(constraint, body.Method.DeclaringType!);
                ReportType(constraint, MemberResolver.Describe(body.Method) + ": constraint on " + parameter.Name);
            }
        }

        foreach (var local in body.State.Locals)
        {
            var symbol = local.ExactType ?? RuntimeSymbolImporter.Import(local.Type);
            foreach (var dependency in RuntimeSymbolTypes.Materialized(symbol).Distinct())
            {
                ConsiderType(dependency, body.Method.DeclaringType!);
                if (!dependency.IsGenericParameter)
                {
                    ReportType(dependency, MemberResolver.Describe(body.Method) + ": local "
                        + (local.Name ?? TypeNameFormatter.Pretty(local.Type)));
                }
            }
        }

        foreach (var entry in body.State.Entries)
        {
            if ((entry.ExceptionRegion?.CatchType ?? entry.CatchType) is not { } catchType)
            {
                continue;
            }

            // Session types follow their copied context; external catches must retain the identities external helpers throw.
            if (TypeRelations.IsSessionType(catchType))
            {
                ConsiderType(catchType, body.Method.DeclaringType!);
            }

            ReportType(catchType, MemberResolver.Describe(body.Method) + ": catch " + TypeNameFormatter.Pretty(catchType));
        }

        foreach (var entry in body.State.Entries.Where(entry => entry.Instruction is not null))
        {
            var instruction = entry.Instruction!;
            var location = MemberResolver.Describe(body.Method) + ": " + instruction.Text;
            switch (instruction.Operand)
            {
                case ResolvedMethod resolved:
                {
                    var target = resolved.Method ?? _pinned[resolved.Definition!.Name];
                    target = IlAsmRenderer.DefinitionOf(target);
                    if (ReflectsMembers(target))
                    {
                        _reflectionLocation ??= location;
                    }

                    var owner = DefinitionOf(target.DeclaringType!);
                    var accessible = MemberAccess.MethodVerdict(new ResolvedMethod(target, null), body.State.Member!.Scope,
                        _session.TypeTable, judgeAll: true) is null;
                    var copy = resolved.IsSessionMethod || _types.ContainsKey(owner) || TypeRelations.IsSessionType(owner)
                        || (owner.Assembly == body.Method.Module.Assembly && ((!target.IsPublic && !((target.IsFamily
                            || target.IsFamilyOrAssembly) && accessible))
                            || !owner.IsVisible));
                    if (copy)
                    {
                        try
                        {
                            AddMethod(target);
                        }
                        catch (ReplException ex)
                        {
                            throw new ReplException($"{location}: required {MemberResolver.Describe(target)}: {ex.Message}", ex);
                        }
                        if (instruction.Op == OpCodes.Newobj)
                        {
                            Instantiate(owner);
                        }
                    }

                    _dependencies.Add(new EditDependency(MemberResolver.Describe(target), target.Module.Assembly.FullName!, location,
                        copy ? "copied" : "external") { Access = MemberAccess.AccessWord(target.Attributes) });
                    break;
                }
                case FieldInfo field:
                {
                    var owner = DefinitionOf(field.DeclaringType!);
                    var copy = _types.ContainsKey(owner) || TypeRelations.IsSessionType(owner)
                        || (owner.Assembly == body.Method.Module.Assembly && (!field.IsPublic || !owner.IsVisible));
                    if (copy)
                    {
                        AddType(owner);
                    }

                    _dependencies.Add(new EditDependency(field.ToString()!, field.Module.Assembly.FullName!, location, copy ? "copied"
                        : "external") { Access = MemberAccess.AccessWord(field.Attributes) });
                    break;
                }
                case Type type:
                    ConsiderType(type, body.Method.DeclaringType!);
                    if (instruction.Op == OpCodes.Ldtoken && ContainsCopiedType(type))
                    {
                        _reflectionLocation ??= location;
                    }

                    if (!type.IsGenericParameter)
                    {
                        ReportType(type, location);
                    }

                    break;
                case CalliSignature signature:
                {
                    var symbols = signature.ExactSymbol is { } exact ? exact.Parameters.Prepend(exact.ReturnType)
                        : signature.ParameterTypes.Concat(signature.OptionalParameterTypes ?? []).Prepend(signature.ReturnType)
                            .Select(type => RuntimeSymbolImporter.Import(type));
                    foreach (var dependency in symbols.SelectMany(RuntimeSymbolTypes.Materialized).Distinct())
                    {
                        ConsiderType(dependency, body.Method.DeclaringType!);
                        if (!dependency.IsGenericParameter)
                        {
                            ReportType(dependency, location);
                        }
                    }

                    break;
                }
                default:
                    break;
            }
        }
    }

    private void ReportType(Type type, string location)
    {
        var disposition = ContainsCopiedType(type) ? "copied (distinct type identity)" : "external";
        _dependencies.Add(new EditDependency(TypeNameFormatter.Pretty(type), type.Assembly.FullName!, location, disposition)
        {
            Access = type.IsVisible ? "public" : "nonpublic",
        });
    }

    private void ValidateBoundaries()
    {
        foreach (var body in _methods.Values.OfType<MethodEditBody>())
        {
            foreach (var entry in body.State.Entries.Where(entry => entry.Instruction is not null))
            {
                var instruction = entry.Instruction!;
                string? problem = null;
                switch (instruction.Operand)
                {
                    case ResolvedMethod { Method: { } target } resolved when !_methods.ContainsKey(IlAsmRenderer.DefinitionOf(target)):
                        problem = MemberAccess.MethodVerdict(resolved, body.State.Member!.Scope, _session.TypeTable, judgeAll: true);
                        var declaration = IlAsmRenderer.DefinitionOf(target);
                        var boundary = declaration.GetParameters().Select(p => p.ParameterType)
                            .Concat(declaration is MethodInfo info ? [info.ReturnType] : Type.EmptyTypes)
                            .FirstOrDefault(ContainsCopiedType);
                        if (boundary is not null)
                        {
                            problem = $"external member {MemberResolver.Describe(target)} requires the original nominal type "
                                + $"{TypeNameFormatter.Pretty(boundary)}; the copy has a distinct identity";
                        }

                        // Assembly access from the source assembly is lost by a copy.
                        if (!target.IsPublic && !target.IsFamily && !target.IsFamilyOrAssembly)
                        {
                            problem ??= $"external member {MemberResolver.Describe(target)} is "
                                + $"{MemberAccess.AccessWord(target.Attributes)} in {target.Module.Assembly.FullName}";
                        }

                        break;
                    case FieldInfo field when !_types.ContainsKey(DefinitionOf(field.DeclaringType!)):
                        problem = MemberAccess.FieldVerdict(field, body.State.Member!.Scope, _session.TypeTable, judgeAll: true);
                        var fieldType = DefinitionOf(field.DeclaringType!).GetFields(Declared)
                            .Single(candidate => candidate.MetadataToken == field.MetadataToken).FieldType;
                        if (ContainsCopiedType(fieldType))
                        {
                            problem = $"external field {field} requires an original nominal type that the copy cannot supply";
                        }

                        if (!field.IsPublic && !field.IsFamily && !field.IsFamilyOrAssembly)
                        {
                            problem ??= $"external field {field} is {MemberAccess.AccessWord(field.Attributes)} "
                                + $"in {field.Module.Assembly.FullName}";
                        }

                        break;
                    default:
                        break;
                }

                if (problem is not null)
                {
                    throw new ReplException($"{MemberResolver.Describe(body.Method)} at '{instruction.Text}': {problem}");
                }
            }
        }
    }

    private bool ContainsCopiedType(Type type) => type.HasElementType ? ContainsCopiedType(type.GetElementType()!)
        : !type.IsGenericParameter && (_types.ContainsKey(DefinitionOf(type)) || (type.IsConstructedGenericType
            && type.GetGenericArguments().Any(ContainsCopiedType)));

    internal void Compile()
    {
        RequireValid();
        var writer = new CecilWriter(SessionAssemblyKind.Types);
        var definitions = Write(writer);
        Definition = writer.Load();
        foreach (var (original, definition) in definitions)
        {
            MemberInfo runtime = definition switch
            {
                TypeDefinition type => Definition.Assembly.GetType(CecilSerializedTypeName.Name(type), throwOnError: true)!,
                MethodDefinition method => Definition.Assembly.ManifestModule.ResolveMethod(method.MetadataToken.ToInt32())!,
                FieldDefinition field => Definition.Assembly.ManifestModule.ResolveField(field.MetadataToken.ToInt32())!,
                _ => throw new InvalidOperationException("unknown copied member"),
            };
            _runtime.Add(original, runtime);
        }

        EntryPoint = CloseRequested((MethodBase)_runtime[Selected.Method]);
        CallableEntryPoint = EntryPoint;
        var selected = (MethodDefinition)definitions[Selected.Method];
        if ((!selected.IsPublic || !Selected.Method.DeclaringType!.IsVisible)
            && selected.CallingConvention != MethodCallingConvention.VarArg)
        {
            var forwarding = CecilForwardingMethod.Find(selected, ForwardingName);
            _forwardingMethod = Definition.Assembly.ManifestModule.ResolveMethod(forwarding.MetadataToken.ToInt32())!;
            CallableEntryPoint = CloseRequested(_forwardingMethod);
        }

        if (MethodPreparation.IsSupported)
        {
            foreach (var method in _runtime.Values.OfType<MethodBase>().Append(EntryPoint).Append(CallableEntryPoint)
                .Where(method => !method.IsAbstract && !method.ContainsGenericParameters && !HasNonIlImplementation(method)).Distinct())
            {
                try
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                }
                catch (Exception exception) when (exception is InvalidProgramException or TypeLoadException
                    or MissingMethodException or MemberAccessException or NotSupportedException)
                {
                    throw new ReplException($"runtime rejected {MemberResolver.Describe(method)}: {exception.Message}", exception);
                }
            }
        }
    }

    private MethodBase CloseRequested(MethodBase method)
    {
        var requested = Selected.Listing.Requested;
        if (requested.DeclaringType is { IsConstructedGenericType: true } declaring)
        {
            var arguments = declaring.GetGenericArguments().Select(MapRuntimeType).ToArray();
            var owner = method.DeclaringType!.MakeGenericType(arguments);
            method = owner.GetMethods(Declared).Cast<MethodBase>().Concat(owner.GetConstructors(Declared))
                .Single(candidate => candidate.MetadataToken == method.MetadataToken);
        }

        if (requested is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } generic)
        {
            method = ((MethodInfo)method).MakeGenericMethod(generic.GetGenericArguments().Select(MapRuntimeType).ToArray());
        }

        return method;
    }

    internal void RequireValid()
    {
        if (_problems.Count != 0)
        {
            throw new ReplException(string.Join(Environment.NewLine, _problems));
        }
    }

    internal void Reject(string problem)
    {
        if (Definition is { } definition)
        {
            SessionAssemblies.Release(definition);
            Definition = null;
        }

        EntryPoint = null;
        CallableEntryPoint = null;
        _forwardingMethod = null;
        _runtime.Clear();
        _problems.Add(problem);
    }

    private Type MapRuntimeType(Type type)
    {
        if (_runtime.TryGetValue(type, out var copied))
        {
            return (Type)copied;
        }

        if (type.IsConstructedGenericType)
        {
            var arguments = type.GetGenericArguments().Select(MapRuntimeType).ToArray();
            return MapRuntimeType(type.GetGenericTypeDefinition()).MakeGenericType(arguments);
        }

        if (type.IsArray)
        {
            var element = MapRuntimeType(type.GetElementType()!);
            return type.IsSZArray ? element.MakeArrayType() : element.MakeArrayType(type.GetArrayRank());
        }

        return type;
    }
}
