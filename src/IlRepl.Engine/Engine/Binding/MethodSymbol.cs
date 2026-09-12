using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Identifies a method or constructor on a particular declaring construction and generic instantiation.
/// </summary>
/// <remarks>
/// A method or constructor: its definition, the type it is referenced on, its instantiation, and
/// its signature as that reference sees it. Two symbols are equal when they name the same member
/// on the same declaring construction with the same generic arguments.
/// </remarks>
public sealed class MethodSymbol : IEquatable<MethodSymbol>
{
    /// <summary>
    /// The definition's identity.
    /// </summary>
    public required DefinitionId Definition { get; init; }

    /// <summary>
    /// Where the member comes from.
    /// </summary>
    public required MethodSymbolSource Source { get; init; }

    /// <summary>
    /// The type the member is referenced on, constructed when the reference names an instantiation; null for a session method.
    /// </summary>
    public TypeSymbol? DeclaringType { get; init; }

    /// <summary>
    /// The member name; <c>.ctor</c> or <c>.cctor</c> for constructors.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The method attributes.
    /// </summary>
    public MethodAttributes Attributes { get; init; }

    /// <summary>
    /// The implementation attributes.
    /// </summary>
    public MethodImplAttributes ImplAttributes { get; init; }

    /// <summary>
    /// The calling convention.
    /// </summary>
    public CallingConventions CallingConvention { get; init; } = CallingConventions.Standard;

    /// <summary>
    /// The return type, with the declaring construction's and the instantiation's arguments substituted.
    /// </summary>
    public required TypeSymbol ReturnType { get; init; }

    /// <summary>
    /// The complete return type when annotations cannot be represented by <see cref="ReturnType"/>.
    /// </summary>
    internal TypeSymbol? ExactReturnType { get; init; }

    /// <summary>
    /// The fixed parameters, substituted the same way.
    /// </summary>
    public IReadOnlyList<ParameterSymbol> Parameters { get; init; } = [];

    /// <summary>
    /// The generic parameters the definition declares.
    /// </summary>
    public IReadOnlyList<GenericParameterSymbol> GenericParameters { get; init; } = [];

    /// <summary>
    /// The generic arguments of an instantiated generic method; empty for a definition or a non-generic method.
    /// </summary>
    public IReadOnlyList<TypeSymbol> GenericArguments { get; init; } = [];

    /// <summary>
    /// The <c>modreq</c> types on the return type.
    /// </summary>
    public IReadOnlyList<TypeSymbol> ReturnRequiredModifiers { get; init; } = [];

    /// <summary>
    /// The <c>modopt</c> types on the return type.
    /// </summary>
    public IReadOnlyList<TypeSymbol> ReturnOptionalModifiers { get; init; } = [];

    /// <summary>
    /// True when a declared member's header has been seen; false for a forward reference.
    /// </summary>
    public bool IsDeclared { get; init; } = true;

    /// <summary>
    /// Whether metadata or accepted source supplies a body location, independently of implementation flags.
    /// </summary>
    public bool BodyAvailable { get; init; } = true;

    /// <summary>
    /// Whether the method has an IL body available for disassembly.
    /// </summary>
    public bool HasIlBody => BodyAvailable && !IsAbstract
        && !Attributes.HasFlag(MethodAttributes.PinvokeImpl)
        && (ImplAttributes & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.IL
        && !ImplAttributes.HasFlag(MethodImplAttributes.InternalCall);

    /// <summary>
    /// True for a static member.
    /// </summary>
    public bool IsStatic => Source == MethodSymbolSource.Session || Attributes.HasFlag(MethodAttributes.Static);

    /// <summary>
    /// True for a virtual member.
    /// </summary>
    public bool IsVirtual => Attributes.HasFlag(MethodAttributes.Virtual);

    /// <summary>
    /// True for an abstract member.
    /// </summary>
    public bool IsAbstract => Attributes.HasFlag(MethodAttributes.Abstract);

    /// <summary>
    /// True for a public member.
    /// </summary>
    public bool IsPublic => (Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;

    /// <summary>
    /// True for a constructor or a type initializer.
    /// </summary>
    public bool IsConstructor => Name is ".ctor" or ".cctor";

    /// <summary>
    /// True for a vararg member.
    /// </summary>
    public bool IsVarArg => (CallingConvention & CallingConventions.VarArgs) != 0;

    /// <summary>
    /// True for a generic method definition that has not been instantiated.
    /// </summary>
    public bool IsGenericDefinition => GenericParameters.Count > 0 && GenericArguments.Count == 0;

    /// <summary>
    /// The number of generic parameters.
    /// </summary>
    public int Arity => GenericParameters.Count;

    /// <summary>
    /// The parameter types in order.
    /// </summary>
    public IReadOnlyList<TypeSymbol> ParameterTypes => [.. Parameters.Select(p => p.Type)];

    /// <summary>
    /// Attaches a parsed declaration to its owner and retained definition identity.
    /// </summary>
    /// <param name="definition">The definition's identity.</param>
    /// <param name="owner">The declaring type, or null for a session method.</param>
    /// <param name="source">The source to assign, or null to infer it from the owner.</param>
    /// <param name="declared">Whether the source header was accepted, or null to retain the current state.</param>
    /// <returns>The declaration with its identity and owner.</returns>
    internal MethodSymbol WithDefinition(
        DefinitionId definition,
        TypeSymbol? owner,
        MethodSymbolSource? source = null,
        bool? declared = null)
    {
        TypeSymbol Map(TypeSymbol type) => SymbolRelations.Rewrite(type, parameter =>
            parameter.Kind == TypeSymbolKind.MethodParameter && GenericParameters.Any(generic => generic.Owner == parameter.Owner)
                ? TypeSymbol.Parameter(definition, true, parameter.Position, parameter.Name, parameter.ParameterAttributes) : null);
        return new MethodSymbol
        {
            Definition = definition,
            Source = source ?? (owner is null ? MethodSymbolSource.Session : MethodSymbolSource.Declared),
            DeclaringType = owner,
            Name = Name,
            Attributes = Attributes,
            ImplAttributes = ImplAttributes,
            CallingConvention = CallingConvention,
            ReturnType = Map(ReturnType),
            ExactReturnType = ExactReturnType is null ? null : Map(ExactReturnType),
            Parameters = [.. Parameters.Select(parameter => parameter with
            {
                Type = Map(parameter.Type),
                ExactType = parameter.ExactType is null ? null : Map(parameter.ExactType),
                RequiredModifiers = [.. parameter.RequiredModifiers.Select(Map)],
                OptionalModifiers = [.. parameter.OptionalModifiers.Select(Map)],
            })],
            GenericParameters = [.. GenericParameters.Select(parameter => parameter with
            {
                Owner = definition, Constraints = [.. parameter.Constraints.Select(Map)],
            })],
            GenericArguments = [.. GenericArguments.Select(Map)],
            ReturnRequiredModifiers = [.. ReturnRequiredModifiers.Select(Map)],
            ReturnOptionalModifiers = [.. ReturnOptionalModifiers.Select(Map)],
            IsDeclared = declared ?? IsDeclared,
            BodyAvailable = BodyAvailable,
        };
    }

    /// <summary>
    /// A copy with a different declaring construction and signature, for a member seen through an instantiation.
    /// </summary>
    /// <param name="declaringType">The declaring construction.</param>
    /// <param name="returnType">The substituted return type.</param>
    /// <param name="parameters">The substituted parameters.</param>
    /// <param name="genericArguments">The instantiation, or empty.</param>
    /// <returns>The copy.</returns>
    public MethodSymbol With(TypeSymbol? declaringType, TypeSymbol returnType, IReadOnlyList<ParameterSymbol> parameters,
        IReadOnlyList<TypeSymbol> genericArguments)
    {
        ArgumentNullException.ThrowIfNull(returnType);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(genericArguments);
        var mappedParameters = parameters.Select((parameter, index) => parameter with
        {
            ExactType = index < Parameters.Count
                ? RuntimeSymbolTypes.RebaseExact(Parameters[index].Type, Parameters[index].ExactType, parameter.Type)
                : parameter.ExactType,
        }).ToArray();
        return WithExact(
            declaringType,
            returnType,
            RuntimeSymbolTypes.RebaseExact(ReturnType, ExactReturnType, returnType),
            mappedParameters,
            genericArguments);
    }

    /// <summary>
    /// A copy with a different declaring construction and complete substituted signature.
    /// </summary>
    internal MethodSymbol WithExact(
        TypeSymbol? declaringType,
        TypeSymbol returnType,
        TypeSymbol? exactReturnType,
        IReadOnlyList<ParameterSymbol> parameters,
        IReadOnlyList<TypeSymbol> genericArguments)
    {
        ArgumentNullException.ThrowIfNull(returnType);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(genericArguments);
        return new MethodSymbol
        {
            Definition = Definition,
            Source = Source,
            DeclaringType = declaringType,
            Name = Name,
            Attributes = Attributes,
            ImplAttributes = ImplAttributes,
            CallingConvention = CallingConvention,
            ReturnType = returnType,
            ExactReturnType = exactReturnType,
            Parameters = parameters,
            GenericParameters = GenericParameters,
            GenericArguments = genericArguments,
            ReturnRequiredModifiers = ReturnRequiredModifiers,
            ReturnOptionalModifiers = ReturnOptionalModifiers,
            IsDeclared = IsDeclared,
            BodyAvailable = BodyAvailable,
        };
    }

    /// <inheritdoc/>
    public bool Equals(MethodSymbol? other) => SymbolIdentity.Equal(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MethodSymbol other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => SymbolIdentity.Hash(this);

    /// <inheritdoc/>
    public override string ToString() => SymbolRenderer.Describe(this);
}
