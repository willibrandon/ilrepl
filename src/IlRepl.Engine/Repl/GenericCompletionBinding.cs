using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Resolves generic completion owners and supplied arguments without materializing runtime constructions.
/// </summary>
internal sealed class GenericCompletionBinding
{
    private readonly EditingView _view;
    private readonly SnapshotBindingScope _scope;
    private readonly CompletionCandidateSource _source;

    /// <summary>
    /// Initializes generic binding for one captured editing view.
    /// </summary>
    /// <param name="view">The editing view.</param>
    /// <param name="source">The query's qualifier resolver.</param>
    public GenericCompletionBinding(EditingView view, CompletionCandidateSource source)
    {
        _view = view;
        _scope = ((SnapshotBindingScope)view.Scope).ForConfirmation();
        _source = source;
    }

    /// <summary>
    /// Resolves every possible owner or keeps the exact definition selected by a valid continuation anchor.
    /// </summary>
    /// <param name="ownerText">The owner reported by the caret classifier.</param>
    /// <param name="selected">The anchor's selected definition, or null for hand-typed text.</param>
    /// <returns>The matching generic definitions.</returns>
    public IReadOnlyList<GenericCompletionTarget> Targets(string? ownerText, GenericCompletionTarget? selected = null)
    {
        try
        {
            return ResolveTargets(ownerText, selected);
        }
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            return [];
        }
    }

    private IReadOnlyList<GenericCompletionTarget> ResolveTargets(string? ownerText, GenericCompletionTarget? selected)
    {
        if (string.IsNullOrWhiteSpace(ownerText))
        {
            return [];
        }

        var targets = new List<GenericCompletionTarget>();
        if (CilSyntaxParser.FindMemberSeparator(ownerText, 0, ownerText.Length) >= 0)
        {
            var syntax = CilSyntaxParser.ParseMethodReference(ownerText);
            var declaring = ownerText[syntax.DeclaringType!.Start..syntax.DeclaringType.End];
            foreach (var type in _source.ResolveOwners(declaring))
            {
                foreach (var method in _scope.AllMethods(type).Where(method => method.IsGenericDefinition
                    && string.Equals(method.Name, syntax.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    targets.Add(ForMethod(method));
                }
            }
        }
        else
        {
            foreach (var type in _source.ResolveOwners(ownerText, genericDefinitionsOnly: true))
            {
                targets.Add(ForType(type));
            }

            var name = CompletionCandidateSource.DecodePrefix(ownerText);
            foreach (var method in _scope.SessionMethods.Where(method => method.IsGenericDefinition
                && string.Equals(method.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                targets.Add(ForMethod(method));
            }
        }

        return selected is null ? targets : targets.Where(target => SameDefinition(target, selected)).ToArray();
    }

    /// <summary>
    /// Binds the completed arguments around the argument currently being edited.
    /// </summary>
    /// <param name="document">The immutable query document.</param>
    /// <param name="site">The type-argument site.</param>
    /// <param name="selected">The selected owner, or null.</param>
    /// <returns>The applicable owners with partial substitutions.</returns>
    public IReadOnlyList<GenericArgumentContext> Arguments(
        CompletionDocumentKey document, CompletionSite site, GenericCompletionTarget? selected)
    {
        var span = GenericArgumentSpan.At(document.Lines[document.Line], document.Caret, _view.InBlockComment);
        if (span is null)
        {
            return [];
        }

        var result = new List<GenericArgumentContext>();
        foreach (var target in Targets(site.GenericOwnerText, selected))
        {
            var count = target.Parameters.Count;
            if (site.ArgumentIndex < 0 || site.ArgumentIndex >= count || span.Arguments.Count > count
                || span.Close >= 0 && span.Arguments.Count != count)
            {
                continue;
            }

            var arguments = new TypeSymbol?[count];
            var valid = true;
            for (var i = 0; i < span.Arguments.Count; i++)
            {
                if (i == site.ArgumentIndex || span.Arguments[i].Length == 0)
                {
                    continue;
                }

                try
                {
                    arguments[i] = BindArgument(span.Arguments[i]);
                }
                catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
                {
                    valid = false;
                    break;
                }
            }

            if (valid)
            {
                result.Add(new GenericArgumentContext { Target = target, Arguments = arguments, Index = site.ArgumentIndex });
            }
        }

        return result;
    }

    /// <summary>
    /// Instantiates the selected generic method before offering its required parameter list.
    /// </summary>
    /// <param name="document">The immutable query document.</param>
    /// <param name="site">The signature site after the method's generic arguments.</param>
    /// <param name="selected">The selected owner, or null.</param>
    /// <returns>The applicable constructed methods.</returns>
    public IReadOnlyList<MethodSymbol> Signatures(
        CompletionDocumentKey document, CompletionSite site, GenericCompletionTarget? selected)
    {
        try
        {
            return ResolveSignatures(document, site, selected);
        }
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            return [];
        }
    }

    private List<MethodSymbol> ResolveSignatures(
        CompletionDocumentKey document, CompletionSite site, GenericCompletionTarget? selected)
    {
        var span = GenericArgumentSpan.At(document.Lines[document.Line], document.Caret, _view.InBlockComment, afterClose: true);
        if (span is null)
        {
            return [];
        }

        var arguments = span.Arguments.Select(BindArgument).ToArray();
        var result = new List<MethodSymbol>();
        foreach (var target in Targets(site.GenericOwnerText, selected))
        {
            try
            {
                if (target.Method is { } method && _scope.Instantiate(method, arguments) is { } constructed)
                {
                    result.Add(constructed);
                }
            }
            catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
            {
                // Constraint failure for one overload cannot hide another valid construction.
            }
        }

        return result;
    }

    private TypeSymbol BindArgument(string text)
    {
        var comment = false;
        return SymbolBinder.BindType(CilSyntaxParser.ParseType(CilLexer.StripComments(text, ref comment)), _scope).Type;
    }

    /// <summary>
    /// Retains a type definition's complete generic parameter declarations.
    /// </summary>
    /// <param name="type">The selected definition.</param>
    /// <returns>The owner used by its continuation token.</returns>
    public GenericCompletionTarget ForType(TypeSymbol type) => new()
    {
        Type = type, Parameters = _scope.GenericParameterDeclarations(type), Label = SymbolRenderer.Pretty(type),
    };

    /// <summary>
    /// Retains a method definition together with the declaring construction visible at the call site.
    /// </summary>
    /// <param name="method">The selected method definition.</param>
    /// <returns>The owner used by its continuation token.</returns>
    public static GenericCompletionTarget ForMethod(MethodSymbol method) => new()
    {
        Method = method, Parameters = method.GenericParameters, Label = SymbolRenderer.Describe(method),
    };

    /// <summary>
    /// Compares a continuation's definition and construction with a freshly resolved generic owner.
    /// </summary>
    /// <param name="first">The fresh owner.</param>
    /// <param name="second">The retained owner.</param>
    /// <returns>Whether the selection still names the same definition.</returns>
    public static bool SameDefinition(GenericCompletionTarget first, GenericCompletionTarget second)
    {
        TypeSymbol Map(TypeSymbol type) => SymbolRelations.Rewrite(type, part =>
        {
            if (part.IsGenericParameter && second.Rebindings.TryGetValue(part.Owner, out var owner))
            {
                return TypeSymbol.Parameter(owner.Definition, part.Kind == TypeSymbolKind.MethodParameter,
                    part.Position, part.Name, part.ParameterAttributes);
            }

            return second.Rebindings.GetValueOrDefault(part.Definition);
        });

        if (first.Type is { } type)
        {
            return second.Type is { } previous && SymbolIdentity.Equal(type, Map(previous));
        }

        if (first.Method is not { } method || second.Method is not { } selected)
        {
            return false;
        }

        if (MemberSpeller.SameMethod(method, selected))
        {
            return true;
        }

        return selected.Definition.Assembly < 0 && method.Definition.Assembly == selected.Definition.Assembly
            && method.Name == selected.Name && method.Attributes == selected.Attributes
            && MemberSpeller.SameMethod(method, SymbolRemapper.Method(selected, method.Definition, Map, selected.IsDeclared));
    }
}
