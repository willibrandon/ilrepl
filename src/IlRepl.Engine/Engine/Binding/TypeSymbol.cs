using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// The shape of a type, with no runtime object behind it: a primitive by keyword, a definition by
/// identity with the facts its metadata states, a construction over arguments, a generic parameter
/// by owner and position, or an array, byref, pointer, function pointer, modified, or pinned form
/// of another symbol. Two symbols are equal when they name the same type, by
/// <see cref="SymbolIdentity"/>; the facts a definition carries are for rendering and eligibility
/// and play no part in equality.
/// </summary>
public sealed class TypeSymbol : IEquatable<TypeSymbol>
{
    private TypeSymbol(TypeSymbolKind kind)
    {
        Kind = kind;
    }

    /// <summary>
    /// The shape.
    /// </summary>
    public TypeSymbolKind Kind { get; }

    /// <summary>
    /// The canonical keyword of a primitive.
    /// </summary>
    public string? Keyword { get; private init; }

    /// <summary>
    /// The identity of a named definition.
    /// </summary>
    public DefinitionId Definition { get; private init; }

    /// <summary>
    /// The metadata name of a named definition, arity suffix included; the CoreLib name of a
    /// primitive; the declared name of a generic parameter.
    /// </summary>
    public string Name { get; private init; } = "";

    /// <summary>
    /// The namespace of a named definition, or empty.
    /// </summary>
    public string Namespace { get; private init; } = "";

    /// <summary>
    /// The definition a nested named definition is declared in, or null.
    /// </summary>
    public TypeSymbol? Declaring { get; private init; }

    /// <summary>
    /// The simple name of the assembly that defines a named definition.
    /// </summary>
    public string AssemblyName { get; private init; } = "";

    /// <summary>
    /// The type attributes of a named definition.
    /// </summary>
    public TypeAttributes Attributes { get; private init; }

    /// <summary>
    /// True when a named definition is a value type, and for the value-type primitives.
    /// </summary>
    public bool IsValueType { get; private init; }

    /// <summary>
    /// The names of a named definition's generic parameters, inherited ones included; empty for a non-generic type.
    /// </summary>
    public IReadOnlyList<string> GenericParameterNames { get; private init; } = [];

    /// <summary>
    /// The element of an array, byref, pointer, modified, or pinned type; the definition of a construction.
    /// </summary>
    public TypeSymbol? Element { get; private init; }

    /// <summary>
    /// The arguments of a construction.
    /// </summary>
    public IReadOnlyList<TypeSymbol> Arguments { get; private init; } = [];

    /// <summary>
    /// The rank of a general array.
    /// </summary>
    public int Rank { get; private init; }

    /// <summary>
    /// The sizes a general array declares, leading dimensions first; shorter than the rank when some are open.
    /// </summary>
    public IReadOnlyList<int> Sizes { get; private init; } = [];

    /// <summary>
    /// The lower bounds a general array declares, leading dimensions first.
    /// </summary>
    public IReadOnlyList<int> LowerBounds { get; private init; } = [];

    /// <summary>
    /// The owner of a generic parameter: a type definition for <c>!N</c>, a method definition for <c>!!N</c>.
    /// </summary>
    public DefinitionId Owner { get; private init; }

    /// <summary>
    /// The position of a generic parameter among its owner's.
    /// </summary>
    public int Position { get; private init; }

    /// <summary>
    /// The variance and special constraints of a generic parameter.
    /// </summary>
    public GenericParameterAttributes ParameterAttributes { get; private init; }

    /// <summary>
    /// The signature of a function pointer.
    /// </summary>
    public MethodSignatureSymbol? Signature { get; private init; }

    /// <summary>
    /// The modifier of a modified type.
    /// </summary>
    public TypeSymbol? Modifier { get; private init; }

    /// <summary>
    /// True for <c>modreq</c>, false for <c>modopt</c>.
    /// </summary>
    public bool IsRequired { get; private init; }

    /// <summary>
    /// True for a generic parameter of either kind.
    /// </summary>
    public bool IsGenericParameter => Kind is TypeSymbolKind.TypeParameter or TypeSymbolKind.MethodParameter;

    /// <summary>
    /// True for a definition with generic parameters that is not instantiated.
    /// </summary>
    public bool IsGenericDefinition => Kind is TypeSymbolKind.Named or TypeSymbolKind.Unresolved && GenericParameterNames.Count > 0;

    /// <summary>
    /// True for a construction of a generic definition.
    /// </summary>
    public bool IsConstructed => Kind == TypeSymbolKind.Constructed;

    /// <summary>
    /// True for a vector or a general array.
    /// </summary>
    public bool IsArray => Kind is TypeSymbolKind.SzArray or TypeSymbolKind.Array;

    /// <summary>
    /// True when the symbol wraps an element: an array, byref, pointer, modified, or pinned type.
    /// </summary>
    public bool HasElement => Kind is TypeSymbolKind.SzArray or TypeSymbolKind.Array or TypeSymbolKind.ByRef or TypeSymbolKind.Pointer or TypeSymbolKind.Modified or TypeSymbolKind.Pinned;

    /// <summary>
    /// True for an interface definition or a construction of one.
    /// </summary>
    public bool IsInterface => DefinitionOrSelf.Kind == TypeSymbolKind.Named && DefinitionOrSelf.Attributes.HasFlag(TypeAttributes.Interface);

    /// <summary>
    /// True for an abstract definition or a construction of one.
    /// </summary>
    public bool IsAbstract => DefinitionOrSelf.Kind == TypeSymbolKind.Named && DefinitionOrSelf.Attributes.HasFlag(TypeAttributes.Abstract);

    /// <summary>
    /// True for a sealed definition or a construction of one.
    /// </summary>
    public bool IsSealed => DefinitionOrSelf.Kind == TypeSymbolKind.Named && DefinitionOrSelf.Attributes.HasFlag(TypeAttributes.Sealed);

    /// <summary>
    /// True when the type is nested in another.
    /// </summary>
    public bool IsNested => DefinitionOrSelf.Declaring is not null;

    /// <summary>
    /// The definition of a construction, or the symbol itself.
    /// </summary>
    public TypeSymbol DefinitionOrSelf => Kind == TypeSymbolKind.Constructed ? Element! : this;

    /// <summary>
    /// The number of generic parameters of a definition or a construction.
    /// </summary>
    public int Arity => DefinitionOrSelf.GenericParameterNames.Count;

    /// <summary>
    /// The core type once modifiers and pinning are set apart.
    /// </summary>
    public TypeSymbol Unwrapped => Kind is TypeSymbolKind.Modified or TypeSymbolKind.Pinned ? Element!.Unwrapped : this;

    /// <summary>
    /// True when a value of this type is a value type on the stack: a value-type definition or
    /// construction, or a value-type primitive. A generic parameter is neither.
    /// </summary>
    public bool IsValueTypeShape => Kind switch
    {
        TypeSymbolKind.Primitive => IsValueType,
        TypeSymbolKind.Named => IsValueType,
        TypeSymbolKind.Constructed => Element!.IsValueType,
        _ => false,
    };

    /// <summary>
    /// A primitive by its canonical keyword.
    /// </summary>
    /// <param name="keyword">A canonical keyword such as <c>int32</c>.</param>
    /// <returns>The symbol.</returns>
    public static TypeSymbol Primitive(string keyword)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        var coreLibName = CilPrimitives.CoreLibNameOf(keyword);
        return new TypeSymbol(TypeSymbolKind.Primitive)
        {
            Keyword = keyword,
            Name = coreLibName["System.".Length..],
            Namespace = "System",
            AssemblyName = "System.Private.CoreLib",
            IsValueType = keyword is not ("string" or "object"),
        };
    }

    /// <summary>
    /// A named definition with the facts its metadata states.
    /// </summary>
    /// <param name="definition">The identity.</param>
    /// <param name="name">The metadata name, arity suffix included.</param>
    /// <param name="ns">The namespace, or empty.</param>
    /// <param name="declaring">The enclosing definition, or null.</param>
    /// <param name="assemblyName">The defining assembly's simple name.</param>
    /// <param name="attributes">The type attributes.</param>
    /// <param name="isValueType">True for a value type.</param>
    /// <param name="genericParameterNames">The generic parameter names, or empty.</param>
    /// <returns>The symbol.</returns>
    public static TypeSymbol Named(DefinitionId definition, string name, string ns, TypeSymbol? declaring, string assemblyName, TypeAttributes attributes, bool isValueType, IReadOnlyList<string> genericParameterNames)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(ns);
        ArgumentNullException.ThrowIfNull(assemblyName);
        ArgumentNullException.ThrowIfNull(genericParameterNames);
        return new TypeSymbol(TypeSymbolKind.Named)
        {
            Definition = definition,
            Name = name,
            Namespace = ns,
            Declaring = declaring,
            AssemblyName = assemblyName,
            Attributes = attributes,
            IsValueType = isValueType,
            GenericParameterNames = genericParameterNames,
        };
    }

    /// <summary>
    /// A generic definition instantiated with arguments.
    /// </summary>
    /// <param name="definition">The generic definition.</param>
    /// <param name="arguments">The arguments, as many as the definition has parameters.</param>
    /// <returns>The symbol.</returns>
    public static TypeSymbol Construct(TypeSymbol definition, IReadOnlyList<TypeSymbol> arguments)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(arguments);
        if (definition.Kind is not (TypeSymbolKind.Named or TypeSymbolKind.Unresolved))
        {
            throw new ArgumentException("only a named definition can be instantiated", nameof(definition));
        }

        return new TypeSymbol(TypeSymbolKind.Constructed) { Element = definition, Arguments = arguments };
    }

    /// <summary>
    /// A generic parameter of a type or a method.
    /// </summary>
    /// <param name="owner">The declaring definition.</param>
    /// <param name="isMethodParameter">True for <c>!!N</c>.</param>
    /// <param name="position">The position.</param>
    /// <param name="name">The declared name.</param>
    /// <param name="attributes">The variance and special constraints.</param>
    /// <returns>The symbol.</returns>
    public static TypeSymbol Parameter(DefinitionId owner, bool isMethodParameter, int position, string name, GenericParameterAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new TypeSymbol(isMethodParameter ? TypeSymbolKind.MethodParameter : TypeSymbolKind.TypeParameter)
        {
            Owner = owner,
            Position = position,
            Name = name,
            ParameterAttributes = attributes,
        };
    }

    /// <summary>
    /// A vector of the element.
    /// </summary>
    /// <param name="element">The element type.</param>
    /// <returns>The symbol.</returns>
    public static TypeSymbol SzArray(TypeSymbol element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return new TypeSymbol(TypeSymbolKind.SzArray) { Element = element };
    }

    /// <summary>
    /// A general array of the element.
    /// </summary>
    /// <param name="element">The element type.</param>
    /// <param name="rank">The rank.</param>
    /// <param name="sizes">The declared sizes, or empty.</param>
    /// <param name="lowerBounds">The declared lower bounds, or empty.</param>
    /// <returns>The symbol.</returns>
    public static TypeSymbol Array(TypeSymbol element, int rank, IReadOnlyList<int> sizes, IReadOnlyList<int> lowerBounds)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(sizes);
        ArgumentNullException.ThrowIfNull(lowerBounds);
        return new TypeSymbol(TypeSymbolKind.Array) { Element = element, Rank = rank, Sizes = sizes, LowerBounds = lowerBounds };
    }

    /// <summary>
    /// A managed pointer to the element.
    /// </summary>
    /// <param name="element">The element type.</param>
    /// <returns>The symbol.</returns>
    public static TypeSymbol ByRef(TypeSymbol element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return new TypeSymbol(TypeSymbolKind.ByRef) { Element = element };
    }

    /// <summary>
    /// An unmanaged pointer to the element.
    /// </summary>
    /// <param name="element">The element type.</param>
    /// <returns>The symbol.</returns>
    public static TypeSymbol Pointer(TypeSymbol element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return new TypeSymbol(TypeSymbolKind.Pointer) { Element = element };
    }

    /// <summary>
    /// A function pointer with a signature.
    /// </summary>
    /// <param name="signature">The signature.</param>
    /// <returns>The symbol.</returns>
    public static TypeSymbol FunctionPointer(MethodSignatureSymbol signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        return new TypeSymbol(TypeSymbolKind.FunctionPointer) { Signature = signature };
    }

    /// <summary>
    /// The element with a custom modifier.
    /// </summary>
    /// <param name="element">The modified type.</param>
    /// <param name="modifier">The modifier type.</param>
    /// <param name="isRequired">True for <c>modreq</c>.</param>
    /// <returns>The symbol.</returns>
    public static TypeSymbol Modified(TypeSymbol element, TypeSymbol modifier, bool isRequired)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(modifier);
        return new TypeSymbol(TypeSymbolKind.Modified) { Element = element, Modifier = modifier, IsRequired = isRequired };
    }

    /// <summary>
    /// The element, pinned.
    /// </summary>
    /// <param name="element">The pinned type.</param>
    /// <returns>The symbol.</returns>
    public static TypeSymbol Pinned(TypeSymbol element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return new TypeSymbol(TypeSymbolKind.Pinned) { Element = element };
    }

    /// <summary>
    /// A reference to a type no loaded assembly defines, kept by its spelling.
    /// </summary>
    /// <param name="name">The metadata name, arity suffix included.</param>
    /// <param name="ns">The namespace, or empty.</param>
    /// <param name="assemblyName">The simple name of the assembly the reference names, or empty.</param>
    /// <param name="isValueType">True when the reference was marked <c>valuetype</c>.</param>
    /// <returns>The symbol.</returns>
    public static TypeSymbol Unresolved(string name, string ns, string assemblyName, bool isValueType)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(ns);
        ArgumentNullException.ThrowIfNull(assemblyName);
        var tick = name.LastIndexOf('`');
        var arity = tick > 0 && int.TryParse(name[(tick + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) ? count : 0;
        return new TypeSymbol(TypeSymbolKind.Unresolved)
        {
            Name = name,
            Namespace = ns,
            AssemblyName = assemblyName,
            IsValueType = isValueType,
            GenericParameterNames = [.. Enumerable.Range(0, arity).Select(i => "!" + SymbolRenderer.Number(i))],
        };
    }

    /// <summary>
    /// True when the symbol, or any part of it, is a reference nothing loaded defines. A member
    /// whose signature has one cannot be confirmed as a candidate.
    /// </summary>
    public bool HasUnresolved => Kind switch
    {
        TypeSymbolKind.Unresolved => true,
        TypeSymbolKind.Constructed => Element!.HasUnresolved || Arguments.Any(a => a.HasUnresolved),
        TypeSymbolKind.FunctionPointer => Signature!.ReturnType.HasUnresolved || Signature.Parameters.Any(p => p.HasUnresolved),
        TypeSymbolKind.Modified => Element!.HasUnresolved || Modifier!.HasUnresolved,
        TypeSymbolKind.SzArray or TypeSymbolKind.Array or TypeSymbolKind.ByRef or TypeSymbolKind.Pointer or TypeSymbolKind.Pinned => Element!.HasUnresolved,
        _ => false,
    };

    /// <summary>
    /// The object primitive.
    /// </summary>
    public static TypeSymbol Object { get; } = Primitive("object");

    /// <summary>
    /// The void primitive.
    /// </summary>
    public static TypeSymbol Void { get; } = Primitive("void");

    /// <inheritdoc/>
    public bool Equals(TypeSymbol? other) => SymbolIdentity.Equal(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TypeSymbol other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => SymbolIdentity.Hash(this);

    /// <inheritdoc/>
    public override string ToString() => SymbolRenderer.Pretty(this);
}
