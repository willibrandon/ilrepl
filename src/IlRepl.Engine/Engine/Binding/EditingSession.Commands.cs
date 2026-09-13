using System.Reflection.Emit;
namespace IlRepl.Engine.Binding;

public sealed partial class EditingSession
{
    private void ApplyCommand(string command, string? argument)
    {
        SessionTransitionRules.ValidateInput(command, argument);
        var transition = SessionTransitionRules.Of(command);
        if (SessionTransitionRules.RequiresNoOpenBlock(transition))
        {
            RequireNoOpenBlock();
        }

        switch (transition)
        {
            case SessionTransition.Run:
                RunBoundary();
                break;
            case SessionTransition.Quit:
                _state.Ended = true;
                break;
            case SessionTransition.Reset:
            {
                var empty = new SnapshotTypeTable(_identity, []);
                _state = new EditingState
                {
                    Types = empty, CommittedTypes = empty.Clone(),
                    NextBody = _state.NextBody + 1, Cell = new EditingBody { LabelSpace = _state.NextBody },
                };
                break;
            }
            case SessionTransition.Clear:
                Clear();
                break;
            case SessionTransition.Undo:
                Undo();
                break;
            case SessionTransition.Load:
                _state.BindingRefreshRequired = true;
                break;
            case SessionTransition.Save:
                break;
            case SessionTransition.Unknown:
                throw new ReplException($"unknown command '{command}'");
            case SessionTransition.None:
                break;
        }
    }

    private void RunBoundary()
    {
        RequireNoOpenBlock();
        if (_state.Cell.Instructions.Count == 0 && _state.Cell.Labels.Count == 0
            && !_state.Cell.Lines.Any(line => IsDirective(line, ".try")))
        {
            return;
        }

        if (_analyzingDocument)
        {
            AddFlowNode(_state.Cell, new FlowNode<TypeSymbol>(FlowLocation(_state.Cell, _documentRaw), _documentRaw)
            {
                Instruction = new StackOperandView<TypeSymbol> { Op = OpCodes.Ret },
                Synthetic = true,
            }, Scope());
        }
        else
        {
            RequireResolvedLabels(_state.Cell);
            if (!_state.Cell.EndsFlow)
            {
                ValidateReturn(_state.Cell, Scope());
            }
        }

        ClearCell();
    }

    private void RequireNoOpenBlock()
    {
        if (_state.Method?.Signature is { } method)
        {
            throw new ReplException($"method {method.Name} is still open; close it with }}");
        }

        if (_state.OpenTypes.LastOrDefault() is { } type)
        {
            throw new ReplException($"class {type.Path} is still open; close it with }}");
        }

        if (_state.Cell.Frames.Count > 0)
        {
            throw new ReplException("a protected region is still open; close it with }");
        }
    }

    private void Clear()
    {
        var target = SessionTransitionRules.ClearTargetOf(_state.Method is not null, _state.OpenTypes.Count > 0);
        if (target == ClearTarget.Method)
        {
            _state = _state.Method!.Before!.Clone();
        }
        else if (target == ClearTarget.Type)
        {
            _state = _state.OpenTypes[0].Before.Clone();
        }
        else
        {
            ClearCell();
        }
    }

    private void ClearCell()
    {
        var previous = _state.Cell;
        _state.Cell = new EditingBody
        {
            Generics = previous.Generics, IsVarArg = previous.IsVarArg, LabelSpace = _state.NextBody++,
        };
        _state.Cell.Locals.AddRange(previous.Locals);
        _state.Cell.Arguments.AddRange(previous.Arguments);
    }

    private void Undo()
    {
        if (_state.OpenTypes.FirstOrDefault() is { } family)
        {
            string[] retained = [.. family.Lines.SkipLast(1)];
            _state = family.Before.Clone();
            if (family.Lines.Count > 0)
            {
                AddLine(family.HeaderLine);
                foreach (var line in retained)
                {
                    AddLine(line);
                }
            }

            return;
        }

        if (_state.Method is { } method)
        {
            _state = method.Before!.Clone();
            if (method.Lines.Count > 0)
            {
                AddLine(method.Header!);
                foreach (var line in method.Lines.SkipLast(1))
                {
                    AddLine(line);
                }
            }

            return;
        }

        string[] bodyLines = [.. _state.Cell.Lines.Where(line => !IsCellDeclaration(line))];
        if (bodyLines.Length == 0)
        {
            return;
        }

        string[] lines = [.. _state.CellDeclarations, .. bodyLines[..^1]];
        var typeArguments = _state.TypeArguments;
        _state.Cell = new EditingBody { LabelSpace = _state.Cell.LabelSpace };
        _state.CellDeclarations.Clear();
        foreach (var line in lines)
        {
            AddLine(line);
        }

        _state.TypeArguments = typeArguments;
    }
}
