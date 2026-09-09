using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Reads a loaded assembly's definitions and references from raw metadata without runtime resolution.
/// </summary>
/// <remarks>
/// Describes one loaded assembly's definitions as symbols from its raw metadata, on either
/// runtime: type definitions with their facts, their members with decoded signatures, their bases,
/// interfaces, and generic parameters, and the types they export from elsewhere. It never resolves
/// a name through the runtime and never loads. References into other assemblies go through the
/// catalog a query supplies, so an answer is always relative to the assemblies that were loaded.
/// </remarks>
public sealed class AssemblySymbolSource
{
    private static readonly ConditionalWeakTable<Assembly, AssemblySymbolSource> Cache = [];
    private static readonly ConditionalWeakTable<Assembly, object> Unreadable = [];

    private readonly WeakReference<Assembly> _assembly;
    private readonly MetadataReader _reader;
    private readonly PEReader? _image;
    private readonly Dictionary<TypeDefinitionHandle, TypeSymbol> _definitions = [];
    private readonly Dictionary<TypeDefinitionHandle, TypeIndexEntry> _entries = [];
    private readonly Lock _gate = new();
    private AssemblyTypeIndex? _index;

    private AssemblySymbolSource(Assembly assembly, MetadataReader reader, PEReader? image)
    {
        _assembly = new WeakReference<Assembly>(assembly);
        _reader = reader;
        _image = image;
        Instance = RuntimeDefinitions.AssemblyInstance(assembly);
        Name = assembly.GetName().Name ?? reader.GetString(reader.GetModuleDefinition().Name);
        Mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid);
        Identity = LoadedAssemblyIdentity.Of(reader.GetAssemblyDefinition().GetAssemblyName());
    }

    /// <summary>
    /// The instance number the assembly's definitions carry.
    /// </summary>
    public long Instance { get; }

    /// <summary>
    /// The assembly's simple name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The module version id.
    /// </summary>
    public Guid Mvid { get; }

    /// <summary>
    /// True while the assembly is loaded.
    /// </summary>
    public bool IsAlive => _assembly.TryGetTarget(out _);

    /// <summary>
    /// True for the core library, whose primitive definitions are the keyword symbols.
    /// </summary>
    public bool IsCoreLib => Name == "System.Private.CoreLib";

    /// <summary>
    /// The metadata reader, valid while a lease is held.
    /// </summary>
    internal MetadataReader Reader => _reader;

    /// <summary>
    /// The full identity used when matching captured assembly references.
    /// </summary>
    internal LoadedAssemblyIdentity Identity { get; }

    /// <summary>
    /// The metadata source of a loaded assembly, or null when neither raw metadata nor a retained image is readable.
    /// </summary>
    /// <param name="assembly">The assembly.</param>
    /// <param name="image">The PE image the assembly was loaded from, when the caller retained it.</param>
    /// <returns>The source, or null.</returns>
    public static AssemblySymbolSource? For(Assembly assembly, byte[]? image = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (assembly.IsDynamic)
        {
            return null;
        }

        if (Cache.TryGetValue(assembly, out var existing))
        {
            return existing;
        }

        if (Unreadable.TryGetValue(assembly, out _) && image is null)
        {
            return null;
        }

        var reader = ModuleMetadata.TryOpen(assembly.ManifestModule);
        PEReader? pe = null;
        if (reader is null && image is not null)
        {
            try
            {
                pe = new PEReader(System.Collections.Immutable.ImmutableArray.Create(image));
                reader = pe.HasMetadata ? pe.GetMetadataReader() : null;
            }
            catch (BadImageFormatException)
            {
                pe?.Dispose();
                pe = null;
                reader = null;
            }
        }

        if (reader is null)
        {
            Unreadable.TryAdd(assembly, new object());
            return null;
        }

        var source = new AssemblySymbolSource(assembly, reader, pe);
        return Cache.TryGetValue(assembly, out var raced) ? raced : Cache.GetValue(assembly, _ => source);
    }

    /// <summary>
    /// Takes a hold on the assembly so its metadata stays readable.
    /// </summary>
    /// <returns>The lease.</returns>
    /// <exception cref="InvalidOperationException">The assembly has been collected.</exception>
    public MetadataLease Lease()
    {
        if (!_assembly.TryGetTarget(out var assembly))
        {
            throw new InvalidOperationException($"the assembly {Name} has been collected; its metadata is no longer readable");
        }

        return new MetadataLease(assembly, this);
    }

    /// <summary>
    /// The index of the assembly's definitions and exports.
    /// </summary>
    public AssemblyTypeIndex Index
    {
        get
        {
            lock (_gate)
            {
                return _index ??= new AssemblyTypeIndex(this, _reader);
            }
        }
    }

    /// <summary>
    /// Warms the metadata index without blocking browser input for a whole assembly scan.
    /// </summary>
    /// <param name="cancellationToken">Cancels unpublished work between metadata batches.</param>
    /// <returns>A task that completes once the shared index is available.</returns>
    public async ValueTask WarmIndexAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_index is not null)
            {
                return;
            }
        }

        using var lease = Lease();
        var index = await AssemblyTypeIndex.CreateAsync(this, _reader, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _index ??= index;
        }
    }

    /// <summary>
    /// The identity of a definition of this assembly.
    /// </summary>
    /// <param name="handle">A type, method, or field definition.</param>
    /// <returns>The identity.</returns>
    public DefinitionId IdOf(EntityHandle handle) => DefinitionId.Loaded(Instance, Mvid, MetadataTokens.GetToken(handle));

    /// <summary>
    /// The definition a token names, when it is a type definition of this assembly.
    /// </summary>
    /// <param name="id">The identity.</param>
    /// <returns>The handle, or null when the identity is not one of this assembly's type definitions.</returns>
    public TypeDefinitionHandle? TypeHandleOf(DefinitionId id)
    {
        if (id.Assembly != Instance || id.IsDeclaration || id.Module != Mvid)
        {
            return null;
        }

        var handle = MetadataTokens.EntityHandle(id.Token);
        return handle.Kind == HandleKind.TypeDefinition ? (TypeDefinitionHandle)handle : null;
    }

    /// <summary>
    /// The symbol of a type definition, with the facts its row states.
    /// </summary>
    /// <param name="handle">The definition.</param>
    /// <returns>The symbol.</returns>
    public TypeSymbol Definition(TypeDefinitionHandle handle) => Definition(handle, []);

    private TypeSymbol Definition(TypeDefinitionHandle handle, HashSet<TypeDefinitionHandle> parents)
    {
        if (!parents.Add(handle))
        {
            throw new BadImageFormatException("cyclic nested type metadata");
        }

        lock (_gate)
        {
            if (_definitions.TryGetValue(handle, out var known))
            {
                return known;
            }
        }

        var definition = _reader.GetTypeDefinition(handle);
        var declaringHandle = definition.GetDeclaringType();
        var declaring = declaringHandle.IsNil ? null : Definition(declaringHandle, parents);
        var name = _reader.GetString(definition.Name);
        var ns = declaring is null ? _reader.GetString(definition.Namespace) : declaring.Namespace;
        TypeSymbol symbol;
        if (declaring is null && IsCoreLib && CilPrimitives.KeywordOfCoreLibName(ns, name) is { } keyword)
        {
            // The core library's primitives are the keyword symbols, as they are when imported from a runtime type.
            symbol = TypeSymbol.Primitive(keyword);
        }
        else
        {
            var parameterNames = definition.GetGenericParameters()
                .Select(p => _reader.GetString(_reader.GetGenericParameter(p).Name)).ToList();
            var byRefLike = definition.GetCustomAttributes().Any(attribute =>
                AttributeTypeName(_reader.GetCustomAttribute(attribute), qualified: true)
                    == "System.Runtime.CompilerServices.IsByRefLikeAttribute");
            symbol = TypeSymbol.Named(
                IdOf(handle), name, ns, declaring, Name, definition.Attributes, IsValueType(definition), parameterNames, byRefLike);
        }

        lock (_gate)
        {
            _definitions.TryAdd(handle, symbol);
            return _definitions[handle];
        }
    }

    /// <summary>
    /// The index entry of a definition.
    /// </summary>
    /// <param name="handle">The definition.</param>
    /// <returns>The entry.</returns>
    public TypeIndexEntry Entry(TypeDefinitionHandle handle)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(handle, out var known))
            {
                return known;
            }
        }

        var symbol = Definition(handle);
        var definition = _reader.GetTypeDefinition(handle);
        var entry = new TypeIndexEntry(IdOf(handle), symbol.Name, symbol.Namespace, SymbolRenderer.IlPath(symbol), definition.Attributes,
            symbol.GenericParameterNames.Count,
            symbol.Kind == TypeSymbolKind.Primitive ? TypeIndexKind.Primitive : KindOf(definition, symbol),
            IsCompilerGenerated(definition, symbol.Name))
        {
            IsVisible = IsVisible(handle),
        };
        lock (_gate)
        {
            _entries.TryAdd(handle, entry);
            return _entries[handle];
        }
    }

    private bool IsVisible(TypeDefinitionHandle handle)
    {
        var definition = _reader.GetTypeDefinition(handle);
        var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
        var declaring = definition.GetDeclaringType();
        if (declaring.IsNil)
        {
            return visibility == TypeAttributes.Public;
        }

        return visibility == TypeAttributes.NestedPublic && IsVisible(declaring);
    }

    private bool IsValueType(TypeDefinition definition)
    {
        if (!TryBaseName(definition.BaseType, out var baseNamespace, out var baseName) || baseNamespace != "System")
        {
            return false;
        }

        if (baseName == "Enum")
        {
            return true;
        }

        // System.Enum itself extends ValueType and is a class; so is ValueType, whose base is object.
        return baseName == "ValueType"
            && !(_reader.GetString(definition.Namespace) == "System" && _reader.GetString(definition.Name) == "Enum");
    }

    private TypeIndexKind KindOf(TypeDefinition definition, TypeSymbol symbol)
    {
        if (definition.Attributes.HasFlag(TypeAttributes.Interface))
        {
            return TypeIndexKind.Interface;
        }

        if (TryBaseName(definition.BaseType, out var ns, out var name) && ns == "System")
        {
            if (name == "Enum")
            {
                return TypeIndexKind.Enum;
            }

            if (name is "MulticastDelegate" or "Delegate" && !(symbol.Namespace == "System" && symbol.Name is "MulticastDelegate"))
            {
                return TypeIndexKind.Delegate;
            }
        }

        return symbol.IsValueType ? TypeIndexKind.Struct : TypeIndexKind.Class;
    }

    private bool TryBaseName(EntityHandle baseType, out string ns, out string name)
    {
        ns = "";
        name = "";
        if (baseType.IsNil)
        {
            // Object, interfaces, and the module pseudo-type extend nothing; the nil coded index reads as a TypeDef row 0.
            return false;
        }

        switch (baseType.Kind)
        {
            case HandleKind.TypeDefinition:
            {
                var definition = _reader.GetTypeDefinition((TypeDefinitionHandle)baseType);
                ns = _reader.GetString(definition.Namespace);
                name = _reader.GetString(definition.Name);
                return true;
            }

            case HandleKind.TypeReference:
            {
                var reference = _reader.GetTypeReference((TypeReferenceHandle)baseType);
                ns = _reader.GetString(reference.Namespace);
                name = _reader.GetString(reference.Name);
                return true;
            }

            default:
                return false;
        }
    }

    private bool IsCompilerGenerated(TypeDefinition definition, string name)
    {
        if (name.Length > 0 && name[0] == '<')
        {
            return true;
        }

        foreach (var handle in definition.GetCustomAttributes())
        {
            if (AttributeTypeName(_reader.GetCustomAttribute(handle)) == "CompilerGeneratedAttribute")
            {
                return true;
            }
        }

        return false;
    }

    private string? AttributeTypeName(CustomAttribute attribute, bool qualified = false)
    {
        EntityHandle type;
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                type = _reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                break;
            case HandleKind.MethodDefinition:
                type = _reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
                break;
            default:
                return null;
        }

        var name = type.Kind switch
        {
            HandleKind.TypeReference => _reader.GetString(_reader.GetTypeReference((TypeReferenceHandle)type).Name),
            HandleKind.TypeDefinition => _reader.GetString(_reader.GetTypeDefinition((TypeDefinitionHandle)type).Name),
            _ => null,
        };
        if (!qualified || name is null)
        {
            return name;
        }

        var ns = type.Kind == HandleKind.TypeReference
            ? _reader.GetTypeReference((TypeReferenceHandle)type).Namespace
            : _reader.GetTypeDefinition((TypeDefinitionHandle)type).Namespace;
        return _reader.GetString(ns) + "." + name;
    }

    /// <summary>
    /// A reference this assembly makes that no loaded assembly defines, kept by its spelling.
    /// </summary>
    /// <param name="handle">The reference.</param>
    /// <param name="isValueType">True when the signature marked it <c>valuetype</c>.</param>
    /// <returns>The unresolved symbol.</returns>
    public TypeSymbol UnresolvedReference(TypeReferenceHandle handle, bool isValueType)
    {
        var reference = _reader.GetTypeReference(handle);
        var scope = reference.ResolutionScope;
        var assemblyName = scope.Kind == HandleKind.AssemblyReference
            ? _reader.GetString(_reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) : Name;
        return TypeSymbol.Unresolved(_reader.GetString(reference.Name), _reader.GetString(reference.Namespace), assemblyName, isValueType);
    }

    /// <summary>
    /// The generic owner a signature inside a type definition decodes against.
    /// </summary>
    /// <param name="handle">The type definition.</param>
    /// <returns>The owner.</returns>
    public SymbolGenericOwner OwnerOf(TypeDefinitionHandle handle)
    {
        var definition = _reader.GetTypeDefinition(handle);
        return new SymbolGenericOwner(IdOf(handle), [.. definition.GetGenericParameters().Select(ParameterFacts)], DefinitionId.None, []);
    }

    private (string Name, GenericParameterAttributes Attributes) ParameterFacts(GenericParameterHandle handle)
    {
        var parameter = _reader.GetGenericParameter(handle);
        return (_reader.GetString(parameter.Name), parameter.Attributes);
    }

    /// <summary>
    /// The base type of a definition, or null for object and interfaces.
    /// </summary>
    /// <param name="handle">The definition.</param>
    /// <param name="catalog">The catalog references resolve through.</param>
    /// <returns>The base type, written in terms of the definition's parameters.</returns>
    public TypeSymbol? BaseType(TypeDefinitionHandle handle, LoadedBindingCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var definition = _reader.GetTypeDefinition(handle);
        return definition.BaseType.IsNil ? null : Decode(definition.BaseType, OwnerOf(handle), catalog);
    }

    /// <summary>
    /// The interfaces a definition declares, written in terms of its parameters.
    /// </summary>
    /// <param name="handle">The definition.</param>
    /// <param name="catalog">The catalog references resolve through.</param>
    /// <returns>The interfaces, in declaration order.</returns>
    public IReadOnlyList<TypeSymbol> Interfaces(TypeDefinitionHandle handle, LoadedBindingCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var definition = _reader.GetTypeDefinition(handle);
        var owner = OwnerOf(handle);
        return [.. definition.GetInterfaceImplementations()
            .Select(i => Decode(_reader.GetInterfaceImplementation(i).Interface, owner, catalog))];
    }

    /// <summary>
    /// The generic parameters a definition declares, with their constraints.
    /// </summary>
    /// <param name="handle">The definition.</param>
    /// <param name="catalog">The catalog references resolve through.</param>
    /// <returns>The parameters, in order.</returns>
    public IReadOnlyList<GenericParameterSymbol> GenericParameters(TypeDefinitionHandle handle, LoadedBindingCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var definition = _reader.GetTypeDefinition(handle);
        var owner = OwnerOf(handle);
        return GenericParameters(definition.GetGenericParameters(), owner.Type, false, owner, catalog);
    }

    private List<GenericParameterSymbol> GenericParameters(GenericParameterHandleCollection handles, DefinitionId ownerId, bool isMethod,
        SymbolGenericOwner owner, LoadedBindingCatalog catalog)
    {
        var parameters = new List<GenericParameterSymbol>();
        foreach (var handle in handles)
        {
            var parameter = _reader.GetGenericParameter(handle);
            var constraints = parameter.GetConstraints().Select(c => Decode(_reader.GetGenericParameterConstraint(c).Type, owner,
                catalog)).ToList();
            parameters.Add(new GenericParameterSymbol(ownerId, isMethod, parameter.Index, _reader.GetString(parameter.Name),
                parameter.Attributes, constraints));
        }

        return parameters;
    }

    /// <summary>
    /// The methods and constructors declared by a definition, expressed in terms of its generic parameters.
    /// </summary>
    /// <param name="handle">The definition.</param>
    /// <param name="catalog">The catalog references resolve through.</param>
    /// <returns>The members, in metadata order.</returns>
    public IReadOnlyList<MethodSymbol> Methods(TypeDefinitionHandle handle, LoadedBindingCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var declaring = Definition(handle);
        var definition = _reader.GetTypeDefinition(handle);
        var typeOwner = OwnerOf(handle);
        var provider = new SymbolSignatureProvider(this, catalog);
        var methods = new List<MethodSymbol>();
        foreach (var methodHandle in definition.GetMethods())
        {
            try
            {
                var method = _reader.GetMethodDefinition(methodHandle);
                var id = IdOf(methodHandle);
                var owner = typeOwner.WithMethod(id, [.. method.GetGenericParameters().Select(ParameterFacts)]);
                var signature = method.DecodeSignature(provider, owner);
                var names = new string?[signature.ParameterTypes.Length];
                foreach (var parameterHandle in method.GetParameters())
                {
                    var parameter = _reader.GetParameter(parameterHandle);
                    if (parameter.SequenceNumber >= 1 && parameter.SequenceNumber <= names.Length)
                    {
                        names[parameter.SequenceNumber - 1] = _reader.GetString(parameter.Name);
                    }
                }

                var returnType = SymbolSignatureProvider.StripModifiers(
                    signature.ReturnType, out var returnRequired, out var returnOptional);
                var parameters = new List<ParameterSymbol>();
                for (var i = 0; i < signature.ParameterTypes.Length; i++)
                {
                    var type = SymbolSignatureProvider.StripModifiers(signature.ParameterTypes[i], out var required, out var optional);
                    parameters.Add(new ParameterSymbol(type, names[i]) { RequiredModifiers = required, OptionalModifiers = optional });
                }

                var convention = signature.Header.CallingConvention == SignatureCallingConvention.VarArgs
                    ? CallingConventions.VarArgs : CallingConventions.Standard;
                if (signature.Header.IsInstance)
                {
                    convention |= CallingConventions.HasThis;
                }

                if (signature.Header.Attributes.HasFlag(SignatureAttributes.ExplicitThis))
                {
                    convention |= CallingConventions.ExplicitThis;
                }

                methods.Add(new MethodSymbol
                {
                    Definition = id,
                    Source = MethodSymbolSource.Loaded,
                    DeclaringType = declaring,
                    Name = _reader.GetString(method.Name),
                    Attributes = method.Attributes,
                    ImplAttributes = method.ImplAttributes,
                    BodyAvailable = method.RelativeVirtualAddress != 0,
                    CallingConvention = convention,
                    ReturnType = returnType,
                    Parameters = parameters,
                    GenericParameters = GenericParameters(method.GetGenericParameters(), id, true, owner, catalog),
                    ReturnRequiredModifiers = returnRequired,
                    ReturnOptionalModifiers = returnOptional,
                });
            }
            catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
            {
                // A damaged row must not hide the healthy members beside it.
            }
        }

        return methods;
    }

    /// <summary>
    /// The fields a definition declares, on the definition itself.
    /// </summary>
    /// <param name="handle">The definition.</param>
    /// <param name="catalog">The catalog references resolve through.</param>
    /// <returns>The fields, in metadata order.</returns>
    public IReadOnlyList<FieldSymbol> Fields(TypeDefinitionHandle handle, LoadedBindingCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var declaring = Definition(handle);
        var definition = _reader.GetTypeDefinition(handle);
        var owner = OwnerOf(handle);
        var provider = new SymbolSignatureProvider(this, catalog);
        var fields = new List<FieldSymbol>();
        foreach (var fieldHandle in definition.GetFields())
        {
            try
            {
                var field = _reader.GetFieldDefinition(fieldHandle);
                var type = SymbolSignatureProvider.StripModifiers(
                    field.DecodeSignature(provider, owner), out var required, out var optional);
                fields.Add(new FieldSymbol
                {
                    Definition = IdOf(fieldHandle),
                    Source = MethodSymbolSource.Loaded,
                    DeclaringType = declaring,
                    Name = _reader.GetString(field.Name),
                    FieldType = type,
                    Attributes = field.Attributes,
                    RequiredModifiers = required,
                    OptionalModifiers = optional,
                });
            }
            catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
            {
                // A damaged row must not hide the healthy members beside it.
            }
        }

        return fields;
    }

    /// <summary>
    /// Decodes property signatures and accessor flags without resolving their runtime members.
    /// </summary>
    /// <param name="handle">The declaring definition.</param>
    /// <param name="catalog">The catalog used for signature references.</param>
    /// <returns>The declared properties in metadata order.</returns>
    public IReadOnlyList<PropertySymbol> Properties(TypeDefinitionHandle handle, LoadedBindingCatalog catalog)
    {
        var declaring = Definition(handle);
        var provider = new SymbolSignatureProvider(this, catalog);
        var owner = OwnerOf(handle);
        var properties = new List<PropertySymbol>();
        foreach (var propertyHandle in _reader.GetTypeDefinition(handle).GetProperties())
        {
            try
            {
                var property = _reader.GetPropertyDefinition(propertyHandle);
                var signature = property.DecodeSignature(provider, owner);
                var accessors = property.GetAccessors();
                MethodDefinitionHandle[] methods = [accessors.Getter, accessors.Setter, .. accessors.Others];
                MethodAttributes[] flags = [.. methods.Where(method => !method.IsNil)
                    .Select(method => _reader.GetMethodDefinition(method).Attributes)];
                properties.Add(new PropertySymbol(
                    IdOf(propertyHandle), declaring, _reader.GetString(property.Name),
                    SymbolSignatureProvider.StripModifiers(signature.ReturnType, out _, out _),
                    [.. signature.ParameterTypes.Select(type => SymbolSignatureProvider.StripModifiers(type, out _, out _))],
                    flags.Any(attributes => (attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public),
                    flags.Any(attributes => attributes.HasFlag(MethodAttributes.Static))));
            }
            catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
            {
                // Preserve the other properties when one metadata row cannot be read.
            }
        }

        return properties;
    }

    /// <summary>
    /// The underlying type of an enum definition: the type of its instance field.
    /// </summary>
    /// <param name="handle">The definition.</param>
    /// <param name="catalog">The catalog references resolve through.</param>
    /// <returns>The underlying type, or null when the definition is not an enum.</returns>
    public TypeSymbol? EnumUnderlyingType(TypeDefinitionHandle handle, LoadedBindingCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var definition = _reader.GetTypeDefinition(handle);
        if (!TryBaseName(definition.BaseType, out var ns, out var name) || ns != "System" || name != "Enum")
        {
            return null;
        }

        var provider = new SymbolSignatureProvider(this, catalog);
        foreach (var fieldHandle in definition.GetFields())
        {
            var field = _reader.GetFieldDefinition(fieldHandle);
            if (!field.Attributes.HasFlag(FieldAttributes.Static))
            {
                return SymbolSignatureProvider.StripModifiers(field.DecodeSignature(provider, OwnerOf(handle)), out _, out _);
            }
        }

        return null;
    }

    /// <summary>
    /// Decodes a type token against its module, captured catalog and generic owner.
    /// </summary>
    /// <param name="handle">A TypeDef, TypeRef, or TypeSpec handle.</param>
    /// <param name="owner">The generic owner in scope.</param>
    /// <param name="catalog">The catalog references resolve through.</param>
    /// <returns>The symbol.</returns>
    public TypeSymbol Decode(EntityHandle handle, SymbolGenericOwner owner, LoadedBindingCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(catalog);
        var provider = new SymbolSignatureProvider(this, catalog);
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => Definition((TypeDefinitionHandle)handle),
            HandleKind.TypeReference => provider.GetTypeFromReference(_reader, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => provider.GetTypeFromSpecification(_reader, owner, (TypeSpecificationHandle)handle, 0),
            _ => throw new BadImageFormatException($"unexpected type handle kind {handle.Kind}"),
        };
    }

    /// <summary>
    /// The namespace and name of a type reference, and the assembly it names when its scope is one.
    /// </summary>
    /// <param name="handle">The reference.</param>
    /// <returns>The namespace, the name, and the resolution scope.</returns>
    internal (string Namespace, string Name, EntityHandle Scope) ReferenceFacts(TypeReferenceHandle handle)
    {
        var reference = _reader.GetTypeReference(handle);
        return (_reader.GetString(reference.Namespace), _reader.GetString(reference.Name), reference.ResolutionScope);
    }

    /// <summary>
    /// The simple name an assembly reference names.
    /// </summary>
    /// <param name="handle">The reference.</param>
    /// <returns>The name.</returns>
    internal string ReferenceName(AssemblyReferenceHandle handle) => _reader.GetString(_reader.GetAssemblyReference(handle).Name);

    /// <summary>
    /// The forwarder an exported-type row names, following nested rows to the top-level forwarder.
    /// </summary>
    /// <param name="handle">The row.</param>
    /// <returns>The assembly reference, or null.</returns>
    internal AssemblyReferenceHandle? ForwarderOf(ExportedTypeHandle handle)
    {
        var exported = _reader.GetExportedType(handle);
        return exported.Implementation.Kind switch
        {
            HandleKind.AssemblyReference => (AssemblyReferenceHandle)exported.Implementation,
            HandleKind.ExportedType => ForwarderOf((ExportedTypeHandle)exported.Implementation),
            _ => null,
        };
    }

    /// <summary>
    /// The namespace and name of an exported-type row.
    /// </summary>
    /// <param name="handle">The row.</param>
    /// <returns>The names.</returns>
    internal (string Namespace, string Name) ExportedFacts(ExportedTypeHandle handle)
    {
        var exported = _reader.GetExportedType(handle);
        return (_reader.GetString(exported.Namespace), _reader.GetString(exported.Name));
    }

    /// <inheritdoc/>
    public override string ToString() => Name;
}
