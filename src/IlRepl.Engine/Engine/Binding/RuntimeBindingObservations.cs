using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Records bindings already established by real input so later snapshots need no runtime resolution callbacks.
/// </summary>
internal static class RuntimeBindingObservations
{
    private static readonly ConditionalWeakTable<Assembly, RuntimeBindingObservationSet> Assemblies = [];
    private static long s_revision;

    /// <summary>
    /// The epoch of observed bindings, advanced only by real runtime operations.
    /// </summary>
    public static long Revision => Interlocked.Read(ref s_revision);

    /// <summary>
    /// Reads the observation version of one requesting assembly without creating a registry entry.
    /// </summary>
    /// <param name="assembly">The requesting assembly.</param>
    /// <returns>Its observed binding version.</returns>
    public static long VersionOf(Assembly assembly) =>
        Assemblies.TryGetValue(assembly, out var observations) ? observations.Version : 0;

    /// <summary>
    /// Copies the known bindings of one requesting assembly.
    /// </summary>
    /// <param name="assembly">The requesting assembly.</param>
    /// <returns>Its type-reference bindings.</returns>
    public static IReadOnlyDictionary<int, TypeSymbol> Capture(Assembly assembly) =>
        Assemblies.TryGetValue(assembly, out var observations) ? observations.Capture() : new Dictionary<int, TypeSymbol>();

    /// <summary>
    /// Records the exact types the runtime supplied for a loaded member's signature.
    /// </summary>
    /// <param name="method">The real method whose signature has already been read.</param>
    /// <param name="symbol">The copied signature.</param>
    public static void Record(MethodBase method, MethodSymbol symbol)
    {
        if ((symbol.Definition.Token & unchecked((int)0xff000000)) != 0x06000000 || method.Module.Assembly.IsDynamic)
        {
            return;
        }

        Record(method.Module.Assembly, (source, provider, found) =>
        {
            var handle = (MethodDefinitionHandle)MetadataTokens.Handle(symbol.Definition.Token);
            var signature = source.Reader.GetMethodDefinition(handle).DecodeSignature(provider, SymbolGenericOwner.None);
            PairAnnotated(signature.ReturnType, symbol.ExactReturnType ?? symbol.ReturnType, symbol.ReturnRequiredModifiers,
                symbol.ReturnOptionalModifiers, found);
            for (var index = 0; index < Math.Min(signature.ParameterTypes.Length, symbol.Parameters.Count); index++)
            {
                var parameter = symbol.Parameters[index];
                PairAnnotated(signature.ParameterTypes[index], parameter.ExactType ?? parameter.Type, parameter.RequiredModifiers,
                    parameter.OptionalModifiers, found);
            }
        });
    }

    /// <summary>
    /// Records the exact type the runtime supplied for a loaded field.
    /// </summary>
    /// <param name="field">The real field whose type has already been read.</param>
    /// <param name="symbol">The copied signature.</param>
    public static void Record(FieldInfo field, FieldSymbol symbol)
    {
        if ((symbol.Definition.Token & unchecked((int)0xff000000)) != 0x04000000 || field.Module.Assembly.IsDynamic)
        {
            return;
        }

        Record(field.Module.Assembly, (source, provider, found) =>
        {
            var handle = (FieldDefinitionHandle)MetadataTokens.Handle(symbol.Definition.Token);
            var type = source.Reader.GetFieldDefinition(handle).DecodeSignature(provider, SymbolGenericOwner.None);
            PairAnnotated(type, symbol.ExactType ?? symbol.FieldType, symbol.RequiredModifiers, symbol.OptionalModifiers, found);
        });
    }

    /// <summary>
    /// Remembers a base type that an actual runtime lookup has already supplied.
    /// </summary>
    /// <param name="type">The runtime declaring type.</param>
    /// <param name="baseType">Its imported base type.</param>
    public static void RecordBase(Type type, TypeSymbol baseType)
    {
        if (type.Assembly.IsDynamic || type.IsGenericParameter || type.HasElementType)
        {
            return;
        }

        Record(type.Assembly, (source, provider, found) =>
        {
            var row = source.Reader.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(type.MetadataToken));
            if (!row.BaseType.IsNil)
            {
                Pair(Decode(source, provider, row.BaseType), baseType, found);
            }
        });
    }

    /// <summary>
    /// Remembers interface references whose runtime constructions were read by actual input.
    /// </summary>
    /// <param name="type">The runtime declaring type.</param>
    /// <param name="interfaces">Its imported interfaces.</param>
    public static void RecordInterfaces(Type type, IReadOnlyList<TypeSymbol> interfaces)
    {
        if (type.Assembly.IsDynamic || type.IsGenericParameter || type.HasElementType)
        {
            return;
        }

        Record(type.Assembly, (source, provider, found) =>
        {
            var row = source.Reader.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(type.MetadataToken));
            foreach (var handle in row.GetInterfaceImplementations())
            {
                var reference = source.Reader.GetInterfaceImplementation(handle).Interface;
                PairFromRuntimeList(type, reference, Decode(source, provider, reference), interfaces, found);
            }
        });
    }

    /// <summary>
    /// Remembers a generic parameter's constraint references after real reflection read them.
    /// </summary>
    /// <param name="parameter">The runtime parameter.</param>
    /// <param name="symbol">The imported parameter and constraints.</param>
    public static void RecordConstraints(Type parameter, GenericParameterSymbol symbol)
    {
        if (parameter.Assembly.IsDynamic)
        {
            return;
        }

        Record(parameter.Assembly, (source, provider, found) =>
        {
            var owner = MetadataTokens.Handle(symbol.Owner.Token);
            var parameters = owner.Kind == HandleKind.MethodDefinition
                ? source.Reader.GetMethodDefinition((MethodDefinitionHandle)owner).GetGenericParameters()
                : source.Reader.GetTypeDefinition((TypeDefinitionHandle)owner).GetGenericParameters();
            foreach (var handle in parameters)
            {
                var row = source.Reader.GetGenericParameter(handle);
                if (row.Index != symbol.Position)
                {
                    continue;
                }

                foreach (var constraint in row.GetConstraints())
                {
                    var reference = source.Reader.GetGenericParameterConstraint(constraint).Type;
                    PairFromRuntimeList(parameter, reference, Decode(source, provider, reference), symbol.Constraints, found);
                }
            }
        });
    }

    private static void PairFromRuntimeList(Type owner, EntityHandle reference, TypeSymbol signature,
        IReadOnlyList<TypeSymbol> candidates, Dictionary<int, TypeSymbol> found)
    {
        var path = SymbolRenderer.IlPath(signature.DefinitionOrSelf);
        var matches = candidates.Where(candidate => SymbolRenderer.IlPath(candidate.DefinitionOrSelf) == path).ToArray();
        if (matches.Length == 1)
        {
            Pair(signature, matches[0], found);
        }
        else if (matches.Length > 1)
        {
            // Actual input has already read this runtime list. Its order and names cannot identify
            // a metadata row when assemblies or constructions collide; use the row's resolved token.
            var declaringType = owner.IsGenericParameter ? owner.DeclaringType : owner;
            var declaringMethod = owner.IsGenericParameter ? owner.DeclaringMethod : null;
            var typeArguments = declaringType is { IsGenericType: true } ? declaringType.GetGenericArguments() : null;
            var methodArguments = declaringMethod is { IsGenericMethod: true } ? declaringMethod.GetGenericArguments() : null;
            var actual = RuntimeSymbolImporter.Import(owner.Module.ResolveType(
                MetadataTokens.GetToken(reference), typeArguments, methodArguments));
            if (matches.Contains(actual))
            {
                Pair(signature, actual, found);
            }
        }
    }

    private static TypeSymbol Decode(AssemblySymbolSource source, SymbolSignatureProvider provider, EntityHandle handle) =>
        handle.Kind switch
        {
            HandleKind.TypeReference => provider.GetTypeFromReference(source.Reader, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => provider.GetTypeFromSpecification(
                source.Reader, SymbolGenericOwner.None, (TypeSpecificationHandle)handle, 0),
            HandleKind.TypeDefinition => source.Definition((TypeDefinitionHandle)handle),
            _ => throw new BadImageFormatException("invalid type in runtime binding observation"),
        };

    private static void Record(Assembly assembly,
        Action<AssemblySymbolSource, SymbolSignatureProvider, Dictionary<int, TypeSymbol>> read)
    {
        try
        {
            if (AssemblySymbolSource.For(assembly) is not { } source)
            {
                return;
            }

            using var lease = source.Lease();
            var provider = new SymbolSignatureProvider(source, new LoadedBindingCatalog([]),
                (handle, kind) => Reference(source, handle, kind));
            var found = new Dictionary<int, TypeSymbol>();
            read(source, provider, found);
            if (found.Count != 0 && Assemblies.GetValue(assembly, _ => new RuntimeBindingObservationSet()).Add(found))
            {
                Interlocked.Increment(ref s_revision);
            }
        }
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            // A runtime member remains usable when its metadata cannot supply a reference-level observation.
        }
    }

    private static TypeSymbol Reference(AssemblySymbolSource source, TypeReferenceHandle handle, byte kind)
    {
        var facts = source.ReferenceFacts(handle);
        var declaring = facts.Scope.Kind == HandleKind.TypeReference
            ? Reference(source, (TypeReferenceHandle)facts.Scope, (byte)SignatureTypeKind.Class) : null;
        return TypeSymbol.Named(source.IdOf(handle), facts.Name, facts.Namespace, declaring, source.Name,
            TypeAttributes.Public, kind == (byte)SignatureTypeKind.ValueType, []);
    }

    private static void PairAnnotated(TypeSymbol metadata, TypeSymbol actual, IReadOnlyList<TypeSymbol> required,
        IReadOnlyList<TypeSymbol> optional, Dictionary<int, TypeSymbol> found)
    {
        if (actual.Kind == TypeSymbolKind.Modified)
        {
            Pair(metadata, actual, found);
            return;
        }

        var core = SymbolSignatureProvider.StripModifiers(metadata, out var requiredReferences, out var optionalReferences);
        Pair(core, actual, found);
        for (var index = 0; index < Math.Min(required.Count, requiredReferences.Count); index++)
        {
            Pair(requiredReferences[index], required[index], found);
        }

        for (var index = 0; index < Math.Min(optional.Count, optionalReferences.Count); index++)
        {
            Pair(optionalReferences[index], optional[index], found);
        }
    }

    private static void Pair(TypeSymbol metadata, TypeSymbol actual, Dictionary<int, TypeSymbol> found)
    {
        if (metadata.Kind == TypeSymbolKind.Named && (metadata.Definition.Token & unchecked((int)0xff000000)) == 0x01000000)
        {
            var target = actual.DefinitionOrSelf;
            var path = target.Kind == TypeSymbolKind.Primitive
                ? CilPrimitives.CoreLibNameOf(target.Keyword!) : SymbolRenderer.IlPath(target);
            if (SymbolRenderer.IlPath(metadata) == path)
            {
                found.TryAdd(metadata.Definition.Token, target);
            }

            return;
        }

        if (metadata.Kind != actual.Kind)
        {
            return;
        }

        if (metadata.Element is not null && actual.Element is not null)
        {
            Pair(metadata.Element, actual.Element, found);
        }

        if (metadata.Modifier is not null && actual.Modifier is not null)
        {
            Pair(metadata.Modifier, actual.Modifier, found);
        }

        for (var index = 0; index < Math.Min(metadata.Arguments.Count, actual.Arguments.Count); index++)
        {
            Pair(metadata.Arguments[index], actual.Arguments[index], found);
        }

        if (metadata.Signature is { } left && actual.Signature is { } right)
        {
            Pair(left.ReturnType, right.ReturnType, found);
            for (var index = 0; index < Math.Min(left.Parameters.Count, right.Parameters.Count); index++)
            {
                Pair(left.Parameters[index], right.Parameters[index], found);
            }
        }
    }
}
