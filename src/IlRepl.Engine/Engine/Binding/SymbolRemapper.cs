namespace IlRepl.Engine.Binding;

/// <summary>
/// Remaps complete symbolic signatures when a replacement changes type or method definition identities.
/// </summary>
internal static class SymbolRemapper
{
    /// <summary>
    /// Rewrites every type in a method signature while preserving its declaration flags.
    /// </summary>
    /// <param name="method">The original signature.</param>
    /// <param name="identity">The new definition identity.</param>
    /// <param name="map">The type substitution.</param>
    /// <param name="declared">Whether the source header has been accepted.</param>
    /// <returns>The independent signature.</returns>
    public static MethodSymbol Method(MethodSymbol method, DefinitionId identity, Func<TypeSymbol, TypeSymbol> map, bool declared)
    {
        var owned = method.WithDefinition(identity, method.DeclaringType is null ? null : map(method.DeclaringType));
        return new MethodSymbol
        {
            Definition = identity,
            Source = method.Source == MethodSymbolSource.Session ? MethodSymbolSource.Session : MethodSymbolSource.Declared,
            DeclaringType = owned.DeclaringType,
            Name = owned.Name,
            Attributes = owned.Attributes,
            ImplAttributes = owned.ImplAttributes,
            CallingConvention = owned.CallingConvention,
            ReturnType = map(owned.ReturnType),
            ExactReturnType = owned.ExactReturnType is null ? null : map(owned.ExactReturnType),
            Parameters = [.. owned.Parameters.Select(parameter => parameter with
            {
                Type = map(parameter.Type), RequiredModifiers = [.. parameter.RequiredModifiers.Select(map)],
                ExactType = parameter.ExactType is null ? null : map(parameter.ExactType),
                OptionalModifiers = [.. parameter.OptionalModifiers.Select(map)],
            })],
            GenericParameters = [.. owned.GenericParameters.Select(parameter => parameter with
            {
                Constraints = [.. parameter.Constraints.Select(map)],
            })],
            GenericArguments = [.. owned.GenericArguments.Select(map)],
            ReturnRequiredModifiers = [.. owned.ReturnRequiredModifiers.Select(map)],
            ReturnOptionalModifiers = [.. owned.ReturnOptionalModifiers.Select(map)],
            IsDeclared = declared,
            BodyAvailable = owned.BodyAvailable,
        };
    }

    /// <summary>
    /// Rewrites the declaring type, field type and custom modifiers of a field declaration.
    /// </summary>
    /// <param name="field">The original field.</param>
    /// <param name="identity">The new definition identity.</param>
    /// <param name="map">The type substitution.</param>
    /// <returns>The independent field.</returns>
    public static FieldSymbol Field(FieldSymbol field, DefinitionId identity, Func<TypeSymbol, TypeSymbol> map) => new()
    {
        Definition = identity,
        Source = MethodSymbolSource.Declared,
        DeclaringType = map(field.DeclaringType),
        Name = field.Name,
        FieldType = map(field.FieldType),
        ExactType = field.ExactType is null ? null : map(field.ExactType),
        Attributes = field.Attributes,
        RequiredModifiers = [.. field.RequiredModifiers.Select(map)],
        OptionalModifiers = [.. field.OptionalModifiers.Select(map)],
    };
}
