using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Analyzes complete edit drafts against their retained declaring context.
/// </summary>
public sealed partial class EditingSession
{
    private void OpenEdit(string argument)
    {
        if (!argument.TrimEnd().EndsWith('{'))
        {
            return;
        }

        RequireNoOpenBlock();
        var (reference, alias) = MethodEditReference.Split(argument.Trim()[..^1]);
        var name = alias ?? reference;
        var edit = _seed.Edits.FirstOrDefault(edit => edit.Name == name && (edit.Reference == reference || edit.Name == reference));
        if (edit is null)
        {
            var scope = Scope(inspecting: true);
            var syntax = CilSyntaxParser.ParseMethodReference(reference);
            var bound = SymbolBinder.BindMethodReference(syntax, scope, wantConstructor: false);
            var method = bound.Definition ?? bound.Method;
            if (method.DeclaringType is { } declaring)
            {
                method = scope.Methods(declaring.DefinitionOrSelf, method.Name)
                    .FirstOrDefault(candidate => candidate.Definition == method.Definition) ?? method;
            }

            edit = new EditingMethodEdit(name, reference, method, 0);
        }

        _state.BeforeEdit = _state.Clone();
        _state.Edit = edit;
        _state.EditMethodClosed = false;
        foreach (var (path, type) in edit.ContextTypes)
        {
            _state.Types.Add(path, type, null);
        }
    }

    private void AddEditLine(string text)
    {
        if (_state.EditMethodClosed && text == "}")
        {
            _state.Types = _state.BeforeEdit!.Types.Clone();
            _state.Edit = null;
            _state.BeforeEdit = null;
            _state.EditMethodClosed = false;
            _state.BindingRefreshRequired = true;
            return;
        }

        if (_state.EditMethodClosed || !IsDirective(text, ".method"))
        {
            throw new ReplException("an edit must contain one complete .method definition");
        }

        var original = _state.Edit!.Method;
        var owner = original.DeclaringType;
        var kind = owner?.IsValueTypeShape == true ? TypeKind.Struct : owner?.IsInterface == true ? TypeKind.Interface : TypeKind.Class;
        var header = new TypeHeader(owner?.Attributes ?? TypeAttributes.Public, kind, true, TypeLayoutKind.Auto,
            owner?.Namespace ?? "", owner?.Name ?? "Cell", [], null, [], true, false);
        var scope = Scope();
        var generics = new SymbolGenericContext(owner is null ? [] : scope.GenericArgumentsOf(owner),
            original.GenericParameters.Select(parameter => parameter.AsType).ToArray());
        var method = MethodDeclarationParser.ParseMember(text[7..].Trim(), scope.WithGenerics(generics), header,
            out var opens, out var closes, out _, names => names.Length == generics.MethodArguments.Count
                ? generics.MethodArguments.ToArray()
                : throw new ReplException("an edit must preserve the method's generic parameter count"));
        if (method.Name != original.Name || method.IsStatic != original.IsStatic || closes)
        {
            throw new ReplException("an edit must preserve the method name and instance/static calling convention");
        }

        method = method.WithDefinition(original.Definition, owner);
        var body = new EditingBody
        {
            Before = _state.Clone(),
            Signature = method,
            Header = text,
            BraceSeen = opens,
            IsVarArg = method.IsVarArg,
            LabelSpace = _state.NextBody++,
            Access = new AccessContext(owner, "edit " + _state.Edit.Name),
            Generics = generics,
        };
        if (owner is not null && !method.IsStatic)
        {
            var receiver = generics.TypeArguments.Count == 0 ? owner : TypeSymbol.Construct(owner, generics.TypeArguments);
            body.Arguments.Add(new VariableSymbol(owner.IsValueTypeShape ? TypeSymbol.ByRef(receiver) : receiver, "this", false));
            body.ThisIndex = 0;
        }

        body.Arguments.AddRange(method.Parameters.Select(parameter => new VariableSymbol(parameter.Type, parameter.Name, false)
        {
            ExactType = parameter.ExactType,
        }));
        _state.Method = body;
    }
}
