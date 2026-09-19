using System.Reflection;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Keeps same-assembly helpers with the copied state and signatures that their reachable bodies use.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private readonly Dictionary<MethodBase, (Type[] Types, MethodBase[] Calls, bool LooksUpTypes)> _sharedHelperReferences = [];

    private void DiscoverSharedHelpers()
    {
        foreach (var body in _methods.Values.OfType<MethodEditBody>().ToArray())
        {
            if (_runtimeHelperTypes.Contains(ContextOf(body.Method)))
            {
                continue;
            }

            foreach (var resolved in body.State.Entries.Select(entry => entry.Instruction?.Operand).OfType<ResolvedMethod>())
            {
                var target = resolved.Method ?? _pinned[resolved.Definition!.Name];
                if (target.Module.Assembly != body.Method.Module.Assembly
                    || _methods.ContainsKey(IlAsmRenderer.DefinitionOf(target)) || !SharesCopiedContext(target))
                {
                    continue;
                }

                AddMethod(target);
            }
        }
    }

    private bool SharesCopiedContext(MethodBase target)
    {
        var pending = new Queue<MethodBase>();
        var visited = new HashSet<MethodBase>();
        pending.Enqueue(target);
        while (pending.TryDequeue(out var method))
        {
            if (method.DeclaringType is { } owner && (!method.IsStatic && ContainsCopiedType(owner)
                    || owner.IsConstructedGenericType && owner.GetGenericArguments().Any(ContainsCopiedType))
                || method.IsGenericMethod && method.GetGenericArguments().Any(ContainsCopiedType))
            {
                return true;
            }

            method = IlAsmRenderer.DefinitionOf(method);
            if (method == Selected.Method)
            {
                return true;
            }

            if (!visited.Add(method))
            {
                continue;
            }

            var references = SharedHelperReferences(method);
            if (references.LooksUpTypes || references.Types.Any(ContainsCopiedType))
            {
                return true;
            }

            foreach (var call in references.Calls)
            {
                if (call.Module.Assembly == target.Module.Assembly)
                {
                    pending.Enqueue(call);
                }
            }
        }

        return false;
    }

    private (Type[] Types, MethodBase[] Calls, bool LooksUpTypes) SharedHelperReferences(MethodBase method)
    {
        if (_sharedHelperReferences.TryGetValue(method, out var cached))
        {
            return cached;
        }

        var types = new HashSet<Type>();
        var calls = new HashSet<MethodBase>();
        var looksUpTypes = false;
        var signature = RuntimeMetadataSignatures.Read(method);
        types.UnionWith(signature.Parameters.Prepend(signature.ReturnType).SelectMany(SignatureTypes));
        var parameters = (method.DeclaringType?.GetGenericArguments() ?? Type.EmptyTypes)
            .Concat(method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes);
        types.UnionWith(parameters.Where(type => type.IsGenericParameter).SelectMany(type => type.GetGenericParameterConstraints()));
        if (method.DeclaringType?.TypeInitializer is { } initializer && initializer != method)
        {
            calls.Add(initializer);
        }

        if (!method.IsAbstract && !method.Attributes.HasFlag(MethodAttributes.PinvokeImpl) && !HasNonIlImplementation(method))
        {
            DisassembledMethod listing;
            try
            {
                listing = MethodDisassembler.Disassemble(method, _session);
            }
            catch (ReplException exception)
            {
                throw new ReplException($"cannot inspect same-assembly helper {MemberResolver.Describe(method)}: "
                    + exception.Message, exception);
            }

            if (listing.Problems.Count != 0 || listing.Entries.Any(entry => entry.Kind == DisassembledEntryKind.Raw))
            {
                throw new ReplException($"cannot inspect same-assembly helper {MemberResolver.Describe(method)}: "
                    + "its complete metadata dependencies are unavailable");
            }

            types.UnionWith(listing.Locals.SelectMany(SignatureTypes));
            types.UnionWith(listing.Clauses.Select(clause => clause.CatchType).OfType<Type>());
            foreach (var entry in listing.Entries)
            {
                switch (entry.Instruction?.Operand)
                {
                    case ResolvedMethod { Method: { } call }:
                        calls.Add(call);
                        if (!call.IsStatic && call.DeclaringType is { } owner)
                        {
                            types.Add(owner);
                        }

                        if (call.IsGenericMethod)
                        {
                            types.UnionWith(call.GetGenericArguments());
                        }

                        looksUpTypes |= IsTypeLookup(call) || IsAssemblyActivation(call) || IsActivation(call);
                        break;
                    case FieldInfo field:
                        if (field.DeclaringType is { } fieldOwner)
                        {
                            types.Add(fieldOwner);
                            if (fieldOwner.TypeInitializer is { } fieldInitializer)
                            {
                                calls.Add(fieldInitializer);
                            }
                        }

                        types.UnionWith(SignatureTypes(RuntimeMetadataSignatures.Read(field)));
                        break;
                    case Type type:
                        types.Add(type);
                        break;
                    case CalliSignature calli:
                        var symbols = calli.ExactSymbol is { } exact ? exact.Parameters.Prepend(exact.ReturnType)
                            : calli.ParameterTypes.Concat(calli.OptionalParameterTypes ?? []).Prepend(calli.ReturnType)
                                .Select(type => RuntimeSymbolImporter.Import(type));
                        types.UnionWith(symbols.SelectMany(RuntimeSymbolTypes.Materialized));
                        break;
                }
            }
        }

        var references = (types.ToArray(), calls.ToArray(), looksUpTypes);
        _sharedHelperReferences.Add(method, references);
        return references;
    }
}
