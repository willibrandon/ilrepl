using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Binds method headers through the shared declaration grammar and projects their symbols for execution.
/// </summary>
public static class MethodHeaderParser
{
    /// <summary>
    /// Parses a class member header and projects its symbols into the runtime context.
    /// </summary>
    /// <param name="spec">The header text.</param>
    /// <param name="context">The parse context of the open type, with its generic parameters in scope.</param>
    /// <param name="owner">The type being written.</param>
    /// <param name="opensBlock">True when the header ended with <c>{</c>.</param>
    /// <param name="closesBlock">True when the header ended with <c>{ }</c>.</param>
    /// <param name="typeParameters">The generic parameters of a generic method, created so <c>!!N</c> resolved while parsing.</param>
    /// <param name="defineTypeParameters">Creates the generic parameters for the names, or null to use throwaway ones.</param>
    /// <returns>The signature with its attributes.</returns>
    /// <exception cref="ReplException">The header is malformed or inconsistent.</exception>
    public static MethodSignature ParseMember(
        string spec,
        ParseContext context,
        TypeHeader owner,
        out bool opensBlock,
        out bool closesBlock,
        out Type[] typeParameters,
        Func<string[], Type[]>? defineTypeParameters = null)
    {
        var scope = new RuntimeBindingScope(context);
        Type[] runtimeParameters = [];
        TypeSymbol[] Define(string[] names)
        {
            runtimeParameters = defineTypeParameters?.Invoke(names) ?? PrototypeGenerics.Create(names);
            return [.. runtimeParameters.Select(scope.ImportType)];
        }

        var symbol = MethodDeclarationParser.ParseMember(spec, scope, owner, out opensBlock, out closesBlock, out _, Define);
        typeParameters = runtimeParameters;
        return Project(symbol, new RuntimeBindingAdapter(scope));
    }

    /// <summary>
    /// Parses the text after <c>.method</c>.
    /// </summary>
    /// <param name="spec">The header text.</param>
    /// <param name="context">The parse context; its methods are the session's table, its generics are empty.</param>
    /// <param name="opensBlock">True when the header ended with <c>{</c>.</param>
    /// <returns>The signature.</returns>
    /// <exception cref="ReplException">The header is malformed or uses unsupported features.</exception>
    public static MethodSignature Parse(string spec, ParseContext context, out bool opensBlock)
    {
        var scope = new RuntimeBindingScope(context);
        return Project(MethodDeclarationParser.Parse(spec, scope, out opensBlock), new RuntimeBindingAdapter(scope));
    }

    private static MethodSignature Project(MethodSymbol method, RuntimeBindingAdapter adapter) => new(
        method.Name,
        adapter.ToType(method.ReturnType),
        [.. method.Parameters.Select(p => new ArgumentDeclaration(adapter.ToType(p.Type), p.Name, null, "")
        {
            ExactType = p.ExactType,
            Attributes = p.Attributes,
            RequiredModifiers = adapter.ToTypes(p.RequiredModifiers),
            OptionalModifiers = adapter.ToTypes(p.OptionalModifiers),
        })])
    {
        ExactSymbol = RequiresExact(method) ? method : null,
        ExactReturnType = method.ExactReturnType,
        Attributes = method.Attributes,
        ImplAttributes = method.ImplAttributes,
        CallingConvention = method.CallingConvention,
        ReturnRequiredModifiers = adapter.ToTypes(method.ReturnRequiredModifiers),
        ReturnOptionalModifiers = adapter.ToTypes(method.ReturnOptionalModifiers),
        TypeParameters = [.. method.GenericParameters.Select(p => new GenericParameterDeclaration(
            p.Name, p.Attributes, adapter.ToTypes(p.Constraints)))],
    };

    private static bool RequiresExact(MethodSymbol method) => method.ExactReturnType is not null
        || method.Parameters.Any(parameter => parameter.ExactType is not null);
}
