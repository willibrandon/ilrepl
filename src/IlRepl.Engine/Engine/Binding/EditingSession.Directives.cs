using System.Reflection;

namespace IlRepl.Engine.Binding;

public sealed partial class EditingSession
{
    private void AddBodyDirective(string text)
    {
        var separator = text.IndexOfAny([' ', '\t', '(']);
        var directive = separator < 0 ? text : text[..separator];
        var rest = separator < 0 ? "" : text[separator..].Trim();
        var body = _state.Body;
        if (body.Signature is { } method)
        {
            if (directive is ".args" or ".typeparams" or ".typeargs" or ".vararg")
            {
                throw new ReplException($"{directive} is not allowed inside a method; parameters come from the header");
            }

            if (directive == ".method")
            {
                throw new ReplException($"a method is already open ({method.Name}); close it with }} before defining another");
            }

            if (method.IsAbstract && directive is ".locals" or ".try")
            {
                throw new ReplException($"abstract method {method.Name} has no body; close it with }}");
            }
        }

        var scope = Scope();
        if (directive is not (".param" or ".custom"))
        {
            body.ParameterTarget = null;
        }

        switch (directive)
        {
            case ".override":
                if (body.Signature is not { DeclaringType: not null } implementing)
                {
                    throw new ReplException(".override is only valid in a method of a .class; session methods are static");
                }

                body.Overrides.Add(OverrideBinding.InBody(rest, scope, implementing));
                break;
            case ".param":
                AddParameter(rest, body, scope);
                break;
            case ".custom":
                if (body.Signature is not { DeclaringType: not null })
                {
                    throw new ReplException(".custom belongs inside a .class block (open one with .class Name {)");
                }

                body.MetadataTypes.AddRange(CustomAttributeBinding.Parse(rest, scope).ReferencedTypes());
                break;
            case ".locals":
                body.Locals.AddRange(VariableDeclarationParser.ParseLocals(rest, scope));
                break;
            case ".args":
                body.Arguments.AddRange(VariableDeclarationParser.ParseArguments(rest, scope)
                    .Select(argument => new VariableSymbol(argument.Type, argument.Name, false)));
                break;
            case ".vararg":
                body.IsVarArg = true;
                break;
            case ".typeparams":
            {
                var names = CellGenericBinding.Parameters(rest,
                    body.Generics.MethodArguments.Select(parameter => parameter.Name).ToArray(),
                    body.Instructions.Count == 0 && body.Labels.Count == 0 && body.Frames.Count == 0);
                _state.TypeArguments = null;

                var identity = body.Generics.MethodArguments.Count == 0 ? NextDefinition() : body.Generics.MethodArguments[0].Owner;
                var first = body.Generics.MethodArguments.Count;
                body.Generics = new SymbolGenericContext([], [.. body.Generics.MethodArguments,
                    .. names.Select((name, index) => TypeSymbol.Parameter(
                        identity, true, first + index, name, GenericParameterAttributes.None))]);
                break;
            }
            case ".typeargs":
            {
                _state.TypeArguments = CellGenericBinding.Arguments(rest,
                    body.Generics.MethodArguments.Select(parameter => parameter.Name).ToArray(),
                    scope.WithGenerics(SymbolGenericContext.Empty));

                break;
            }
            case ".try":
                if (rest is not ("" or "{"))
                {
                    throw new ReplException("the label form of .try is not supported; use blocks: .try { ... } catch T { ... }");
                }

                body.Frames.Add(BlockKind.Try);
                body.Stack.Clear();
                body.EndsFlow = false;
                body.RegionBracePending = true;
                break;
            case ".maxstack":
                break;
            default:
                throw new ReplException($"unknown or misplaced directive '{directive}'");
        }

        scope.CommitDeclarations();
        body.Lines.Add(text);
        if (_state.Method is null && directive is ".locals" or ".args" or ".vararg" or ".typeparams")
        {
            _state.CellDeclarations.Add(text);
        }
    }

    private static string Unwrap(string text) => text.StartsWith('(') && text.EndsWith(')') ? text[1..^1].Trim() : text;

    private static void AddParameter(string text, EditingBody body, SnapshotBindingScope scope)
    {
        if (body.Signature is not { DeclaringType: not null } method)
        {
            throw new ReplException(".param belongs inside a member method");
        }

        var close = text.IndexOf(']', StringComparison.Ordinal);
        if (!text.StartsWith('[') || close < 0 || !int.TryParse(text.AsSpan(1, close - 1), out var index))
        {
            throw new ReplException("usage: .param [1] = int32(5)  (1 is the first parameter, 0 the return value)");
        }

        if (index < 0 || index > method.Parameters.Count)
        {
            throw new ReplException($"{method.Name} has {method.Parameters.Count} parameters; .param takes 0 to that count");
        }

        var after = text[(close + 1)..].Trim();
        if (after.Length > 0)
        {
            if (!after.StartsWith('='))
            {
                throw new ReplException($"unexpected '{after}' after .param [{index}]");
            }

            if (index == 0)
            {
                throw new ReplException("the return value cannot have a default");
            }

            LiteralBindingRules.Constant(after[1..], method.Parameters[index - 1].Type, scope, "parameter " + index);
        }

        body.ParameterTarget = index;
    }
}
