using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine.Binding;

public sealed partial class EditingSession
{
    private void OpenMethod(string spec, string line)
    {
        var before = _state.Clone();
        var owner = _state.OpenTypes.LastOrDefault();
        var identity = NextDefinition();
        var scope = Scope();
        bool opens;
        var closes = false;
        var parsed = owner is null
            ? MethodDeclarationParser.Parse(spec, scope.WithGenerics(SymbolGenericContext.Empty), out opens)
            : MethodDeclarationParser.ParseMember(spec, scope, owner.Header, out opens, out closes, out _,
                names => [.. names.Select((name, index) => TypeSymbol.Parameter(
                    identity, true, index, name, GenericParameterAttributes.None))]);
        var method = parsed.WithDefinition(identity, owner?.Type);
        if (owner is null && _preparedMethods?.GetValueOrDefault(method.Name) is { } prepared)
        {
            method = method.WithDefinition(prepared.Definition, null);
        }
        if (owner is not null)
        {
            var declaration = _state.Types.DeclarationOf(owner.Type)!;
            var previous = declaration.Methods.FirstOrDefault(candidate => SameSignature(candidate, method));
            if (previous is { IsDeclared: true })
            {
                throw new ReplException($"{method.Name} is already declared in {owner.Path}");
            }

            if (previous is not null)
            {
                method = method.WithDefinition(previous.Definition, owner.Type);
            }

            ReplaceMembers(declaration, declaration.Fields,
                declaration.Methods.Where(candidate => candidate != previous).Append(method));
        }
        else
        {
            var previous = _state.Methods.FirstOrDefault(candidate => candidate.Name == method.Name);
            if (previous is not null && !SameSignature(previous, method))
            {
                RequireCompatibleMethodReferences(previous);
            }
        }

        var body = new EditingBody
        {
            Before = before,
            Signature = method,
            Header = line,
            BraceSeen = opens,
            IsVarArg = method.IsVarArg,
            LabelSpace = _state.NextBody++,
            Access = new AccessContext(owner?.Type, owner is null ? "method " + method.Name : "class " + owner.Path),
            Generics = new SymbolGenericContext(
                owner is null ? [] : _state.Types.DeclarationOf(owner.Type)!.GenericParameters.Select(p => p.AsType).ToArray(),
                method.GenericParameters.Select(p => p.AsType).ToArray()),
        };
        if (owner is not null && !method.IsStatic)
        {
            var receiver = body.Generics.TypeArguments.Count == 0
                ? owner.Type : TypeSymbol.Construct(owner.Type, body.Generics.TypeArguments);
            body.Arguments.Add(new VariableSymbol(owner.Type.IsValueTypeShape ? TypeSymbol.ByRef(receiver) : receiver, "this", false));
            body.ThisIndex = 0;
        }

        body.Arguments.AddRange(method.Parameters.Select(parameter => new VariableSymbol(parameter.Type, parameter.Name, false)));
        _state.Method = body;
        if (closes)
        {
            CloseMethod();
        }
    }

    private void CloseMethod()
    {
        var body = _state.Method!;
        if (_analyzingDocument && body.Signature is not { IsAbstract: true })
        {
            AddFlowNode(body, new FlowNode<TypeSymbol>(FlowLocation(body, "}"), "}")
            {
                Instruction = new StackOperandView<TypeSymbol> { Op = OpCodes.Ret },
                Synthetic = true,
            }, Scope());
        }
        else if (!_analyzingDocument)
        {
            RequireResolvedLabels(body);
            if (!body.EndsFlow && body.Signature is not { IsAbstract: true })
            {
                ValidateReturn(body, Scope());
            }
        }

        if (_state.OpenTypes.LastOrDefault() is { } owner)
        {
            owner.Bodies.Add(body);
        }
        else
        {
            var method = body.Signature!;
            var existing = _state.Methods.FindIndex(candidate => candidate.Name == method.Name);
            if (existing >= 0)
            {
                _state.Methods[existing] = method;
            }
            else
            {
                _state.Methods.Add(method);
            }

            _state.Definitions.RemoveAll(definition => !definition.IsFamily && definition.Name == method.Name);
            _state.Definitions.Add(new EditingDefinition(
                method.Name, false, body.Header!, [.. body.Lines], [],
                [.. ReferencedTypes(body)], ReferencedMethods(body).ToHashSet(StringComparer.Ordinal)));
        }

        _state.Method = null;
    }

    private void RequireCompatibleMethodReferences(MethodSymbol previous)
    {
        var dependent = _state.Definitions.FirstOrDefault(
            definition => definition.Name != previous.Name && definition.ReferencedMethods.Contains(previous.Name));
        if (dependent is not null)
        {
            throw new ReplException($"{(dependent.IsFamily ? "class" : "method")} {dependent.Name} references {previous}");
        }

        if (ReferencedMethods(_state.Cell).Contains(previous.Name))
        {
            throw new ReplException($"the cell references {previous}; .clear it before changing the signature");
        }
    }

    private static bool SameSignature(MethodSymbol first, MethodSymbol second) => first.Name == second.Name
        && SignatureSymbolIdentity.Equal(first, second);

    private void ReplaceMembers(
        DeclarationSymbol declaration,
        IEnumerable<FieldSymbol> fields,
        IEnumerable<MethodSymbol> methods)
    {
        var replacement = new DeclarationSymbol(
            declaration.Type, declaration.BaseType, declaration.Interfaces, declaration.GenericParameters,
            fields, methods, declaration.CanDefineForward)
        {
            IsPlaceholder = declaration.IsPlaceholder,
            Properties = declaration.Properties,
        };
        _state.Types.Add(SymbolRenderer.IlPath(declaration.Type), declaration.Type, replacement);
    }

    private static void RequireResolvedLabels(EditingBody body)
    {
        string[] pending = [.. body.ReferencedLabels.Except(body.Labels)];
        if (pending.Length > 0)
        {
            throw new ReplException($"labels referenced but never defined: {string.Join(", ", pending)} (define with 'NAME:')");
        }
    }

}
