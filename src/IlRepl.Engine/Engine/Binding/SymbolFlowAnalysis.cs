namespace IlRepl.Engine.Binding;

/// <summary>
/// Adapts independently bound editing bodies to the shared control-flow rules.
/// </summary>
internal static class SymbolFlowAnalysis
{
    /// <summary>
    /// Creates assignment and merge rules over the captured declarations and metadata.
    /// </summary>
    public static FlowTypeRules<TypeSymbol> Rules(IBindingScope scope) => new(
        SymbolStackAlgebra.Instance,
        type => SymbolStackCompatibility.Category(type, scope),
        (from, to) => SymbolRelations.IsAssignable(from, to, scope),
        scope.BaseOf,
        EditingStack.Name,
        SymbolStackAlgebra.BoxedType,
        type =>
        {
            var declaration = scope.ParameterDeclaration(type);
            var constraints = declaration?.Constraints ?? [];
            var reference = declaration?.HasReferenceTypeConstraint == true
                || constraints.Any(constraint => !constraint.IsInterface && !constraint.IsGenericParameter && !constraint.IsValueTypeShape
                    && constraint.Name is not ("Object" or "ValueType" or "Enum"));
            return new FlowParameter<TypeSymbol>(reference, declaration?.HasValueTypeConstraint == true, constraints);
        },
        type => (type.Kind == TypeSymbolKind.SzArray ? 1 : type.Rank, type.Kind == TypeSymbolKind.SzArray),
        (element, rank, vector) => vector ? TypeSymbol.SzArray(element) : TypeSymbol.Array(element, rank, [], []),
        type => scope.EnumUnderlyingType(type) ?? type);

    /// <summary>
    /// Analyzes the body without creating runtime definitions.
    /// </summary>
    public static FlowResult<TypeSymbol> Run(EditingBody body, IBindingScope scope, CancellationToken cancellationToken = default)
    {
        var returnType = body.Signature?.ReturnType;
        var declaringType = body.Signature?.DeclaringType;
        var tracksConstructorInitialization = body.Signature is { Name: ".ctor", IsStatic: false }
            && declaringType?.IsValueType == false;
        var graph = new FlowGraph<TypeSymbol>(
            body.FlowNodes, TypeSymbol.Object, hasThis: body.ThisIndex == 0, declaringType: declaringType,
            tracksConstructorInitialization: tracksConstructorInitialization)
        {
            BodyName = body.Signature?.Name ?? "cell",
        };
        return new ControlFlowAnalysis<TypeSymbol>(Rules(scope)).Run(graph,
            SymbolIdentity.Equal(returnType, TypeSymbol.Void) ? null : returnType,
            body.Signature is null, cancellationToken);
    }

    /// <summary>
    /// Analyzes a captured body with cancellation points within the fixed-point worklist.
    /// </summary>
    public static ValueTask<FlowResult<TypeSymbol>> RunAsync(EditingBody body, IBindingScope scope,
        CancellationToken cancellationToken = default)
    {
        var returnType = body.Signature?.ReturnType;
        var declaringType = body.Signature?.DeclaringType;
        var tracksConstructorInitialization = body.Signature is { Name: ".ctor", IsStatic: false }
            && declaringType?.IsValueType == false;
        var graph = new FlowGraph<TypeSymbol>(
            body.FlowNodes, TypeSymbol.Object, hasThis: body.ThisIndex == 0, declaringType: declaringType,
            tracksConstructorInitialization: tracksConstructorInitialization)
        {
            BodyName = body.Signature?.Name ?? "cell",
        };
        return new ControlFlowAnalysis<TypeSymbol>(Rules(scope)).RunAsync(graph,
            SymbolIdentity.Equal(returnType, TypeSymbol.Void) ? null : returnType,
            body.Signature is null, cancellationToken);
    }
}
