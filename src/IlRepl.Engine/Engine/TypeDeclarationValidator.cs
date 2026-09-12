using System.Reflection;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Validates type declarations through the shared symbol rules before their runtime assembly is emitted.
/// </summary>
public static class TypeDeclarationValidator
{
    /// <summary>
    /// Uses the accepting session's existing binding context to validate a completed declaration.
    /// </summary>
    /// <param name="declaration">The declaration.</param>
    /// <param name="type">Its prototype or runtime type.</param>
    /// <param name="types">The complete family table.</param>
    /// <param name="context">The accepting session's context.</param>
    /// <returns>Implicit static interface mappings that must be emitted.</returns>
    public static IReadOnlyList<ClassOverrideDeclaration> Validate(
        TypeDeclaration declaration, Type type, TypeTable types, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(types);
        var scope = new RuntimeBindingScope(context with { Types = types });
        var adapter = new RuntimeBindingAdapter(scope);
        var symbol = scope.ImportType(type);
        MethodSymbol Target(MethodBase target)
        {
            var owner = scope.ImportType(target.DeclaringType!);
            var candidates = scope.TryGetDeclaration(owner, out var members)
                ? members.FindMethods(target.Name) : scope.Methods(owner, target.Name);
            return candidates.Single(candidate => candidate.Definition == RuntimeDefinitions.Of(target));
        }

        var input = new TypeValidationState(
            symbol, declaration.FullName, declaration.Kind, declaration.Layout, declaration.PackingSize, declaration.ClassSize,
            declaration.Fields.Where(field => !field.IsStatic).ToDictionary(field => field.Name, field => field.Offset),
            declaration.TypeParameters.Select((parameter, index) => new GenericParameterSymbol(
                symbol.Definition, false, index, parameter.Name, parameter.Attributes,
                parameter.Constraints.Select(scope.ImportType).ToArray())).ToArray(),
            declaration.Methods.SelectMany(method => method.Overrides.Select(mapping => Target(mapping.Target)))
                .Concat(declaration.Overrides.Select(mapping => Target(mapping.Target))).ToArray());
        return TypeDeclarationBinding.Validate(input, scope).Select(mapping => new ClassOverrideDeclaration(
            adapter.ToResolvedMethod(mapping.Target).Method!, "static " + scope.Describe(mapping.Target.Method), mapping.Body.Name,
            adapter.ToType(mapping.Body.ReturnType), adapter.ToTypes(mapping.Body.ParameterTypes), mapping.Body.IsStatic, "")
        {
            ExactBodyReturnType = mapping.Body.ExactReturnType,
            ExactBodyParameterTypes = [.. mapping.Body.Parameters.Select(parameter => parameter.ExactType)],
        }).ToArray();
    }
}
