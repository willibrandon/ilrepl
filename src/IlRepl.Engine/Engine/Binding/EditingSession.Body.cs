using System.Reflection.Emit;
using IlRepl.Protocol;

namespace IlRepl.Engine.Binding;

public sealed partial class EditingSession
{
    private void AddLine(string line)
    {
        var text = line.Trim();
        if (text.Length == 0)
        {
            return;
        }

        EditingTypeBlock[] enclosing = [.. _state.OpenTypes];
        if (_state.Accessor is not null)
        {
            AddAccessorLine(text);
        }
        else if (_state.Method is not null)
        {
            if (text == "}" && _state.Method.Frames.Count == 0)
            {
                CloseMethod();
            }
            else
            {
                AddBodyLine(text);
            }
        }
        else if (IsDirective(text, ".class"))
        {
            OpenType(text[6..].Trim(), text);
        }
        else if (IsDirective(text, ".method"))
        {
            OpenMethod(text[7..].Trim(), text);
        }
        else if (_state.OpenTypes.Count > 0)
        {
            AddTypeLine(text);
        }
        else
        {
            AddBodyLine(text);
        }

        foreach (var block in enclosing)
        {
            if (_state.OpenTypes.Contains(block))
            {
                block.Lines.Add(text);
            }
        }
    }

    private void AddBodyLine(string text)
    {
        var body = _state.Body;
        if (text.StartsWith('.'))
        {
            AddBodyDirective(text);
            return;
        }

        if (text == "{")
        {
            if (body.RegionBracePending)
            {
                body.Lines.Add(text);
                return;
            }

            if (body.Signature is not null && !body.BraceSeen && body.Lines.Count == 0)
            {
                body.BraceSeen = true;
                body.Lines.Add(text);
                return;
            }

            throw new ReplException("unexpected '{'; open a protected region with .try {, or a method with .method");
        }

        if (text.StartsWith('}') || StartsHandler(text))
        {
            AddHandler(text, body);
            return;
        }

        var (labels, rest) = InstructionParser.SplitLabels(text);
        foreach (var label in labels)
        {
            if (body.Labels.Contains(label))
            {
                throw new ReplException($"label '{label}' is already defined");
            }
        }

        if (body.Signature is { IsAbstract: true } abstractMethod)
        {
            throw new ReplException($"abstract method {abstractMethod.Name} has no body; close it with }}");
        }

        if (rest.Length > 0)
        {
            var scope = Scope();
            var instruction = SymbolBinder.BindInstruction(CilSyntaxParser.ParseInstruction(rest), scope);
            if (instruction.Op == OpCodes.Ret)
            {
                if (body.Frames.Count > 0)
                {
                    throw new ReplException("ret is not allowed inside a protected region; use leave to a label after it");
                }

            }

            CheckInstruction(instruction, body, scope);
            AddFlowNode(body, new FlowNode<TypeSymbol>(FlowLocation(body, text), text)
            {
                Instruction = FlowView(instruction, body, scope),
                Labels = labels,
                Targets = instruction.Operand.Kind switch
                {
                    OperandKind.Label => [(string)instruction.Operand.Value!],
                    OperandKind.Labels => ((IEnumerable<string>)instruction.Operand.Value!).ToArray(),
                    _ => [],
                },
            }, scope);
            body.Instructions.Add(instruction);
            if (instruction.Operand is { Kind: OperandKind.Label, Value: string target })
            {
                body.ReferencedLabels.Add(target);
            }
            else if (instruction.Operand is { Kind: OperandKind.Labels, Value: IEnumerable<string> targets })
            {
                body.ReferencedLabels.UnionWith(targets);
            }

            scope.CommitDeclarations();
        }
        else
        {
            AddFlowNode(body, new FlowNode<TypeSymbol>(FlowLocation(body, text), text) { Labels = labels }, Scope());
        }

        body.Labels.UnionWith(labels);
        body.RegionBracePending = false;
        body.ParameterTarget = null;
        body.Lines.Add(text);
    }

    private void AddHandler(string text, EditingBody body)
    {
        var transition = BlockTransitionParser.Parse(text, body.Frames, body.Instructions.LastOrDefault()?.Op);
        var caught = transition.CatchType is not null
            ? transition.CatchType.Length == 0 ? TypeSymbol.Object : BindType(transition.CatchType)
            : null;
        if (caught is not null)
        {
            body.MetadataTypes.Add(caught);
        }
        if (transition.Kind == BlockKind.End)
        {
            body.Frames.RemoveAt(body.Frames.Count - 1);
        }
        else
        {
            body.Frames[^1] = transition.Kind;
        }

        AddFlowNode(body, new FlowNode<TypeSymbol>(FlowLocation(body, text), text)
        {
            Block = transition.Kind,
            CatchType = caught,
        }, Scope());
        body.RegionBracePending = transition.Kind != BlockKind.End;
        body.ParameterTarget = null;
        body.Lines.Add(text);
    }

    private static StackOperandView<TypeSymbol> FlowView(BoundInstruction instruction, EditingBody body, IBindingScope scope)
    {
        var view = EditingStack.View(instruction, scope);
        if (instruction.Operand.Method is { } called && body.Signature is { } current)
        {
            view = view with { MethodIsCurrentDefinition = called.Method.Definition == current.Definition };
        }

        if (instruction.Op == OpCodes.Jmp && instruction.Operand.Method is { } jump)
        {
            view = view with
            {
                JumpRestriction = JumpCompatibility.Problem(jump.Method, body.Signature, body.Arguments,
                    body.Generics.MethodArguments, body.IsVarArg, scope),
            };
        }

        if (instruction.Operand.Field is not { IsInitOnly: true } field || instruction.Op.Name is not ("stfld" or "stsfld"))
        {
            return view;
        }

        return view with
        {
            StoreRestriction = InstructionMemberRules.InitOnlyStoreProblem(field, instruction.Op.Name,
                body.Signature, body.Access.Type, true, scope.Pretty),
            ReceiverRestriction = InstructionMemberRules.InitOnlyStoreProblem(field, instruction.Op.Name,
                body.Signature, body.Access.Type, false, scope.Pretty),
        };
    }

    private static void CheckInstruction(BoundInstruction instruction, EditingBody body, SnapshotBindingScope scope)
    {
        var operand = instruction.Operand;
        var facts = AccessFacts.From(scope);
        var problem = operand.Type is { } type
            ? MemberEligibility.TypeVerdict(operand.ExactType ?? type, scope.Access, facts)
            : operand.Field is { } field
                ? MemberEligibility.TypeVerdict(field.ExactType ?? field.FieldType, scope.Access, facts)
                    ?? MemberEligibility.FieldVerdict(field, scope.Access, facts)
                : operand.Method is { } method
                    ? MemberEligibility.MethodVerdict(method.Method, scope.Access, facts,
                        exactGenericArguments: method.ExactGenericArguments,
                        exactOptionalParameterTypes: method.ExactOptionalParameterTypes) : null;
        if (problem is not null)
        {
            throw new ReplException(problem);
        }

        if (body.ThisIndex == 0 && body.Arguments[0].Type.Kind == TypeSymbolKind.ByRef
            && instruction.ArgumentIndex == 0 && instruction.Op.Name is "ldarga" or "ldarga.s")
        {
            throw new ReplException($"this is already a {SymbolRenderer.Pretty(body.Arguments[0].Type)}; use ldarg.0");
        }

        if (operand.Field is { IsLiteral: true } literal && instruction.Op.Name is "ldsfld" or "ldsflda" or "stsfld")
        {
            throw new ReplException($"{literal.Name} is a literal; it has no storage, so {instruction.Op.Name} would fail");
        }

        if (instruction.Op == OpCodes.Arglist && !body.IsVarArg)
        {
            throw new ReplException("arglist needs a vararg cell; add the .vararg directive first");
        }

        if (instruction.Op == OpCodes.Endfilter && (body.Frames.Count == 0 || body.Frames[^1] != BlockKind.Filter))
        {
            throw new ReplException("endfilter is only valid inside a filter block (} filter {)");
        }
    }

    private static void ValidateReturn(EditingBody body, IBindingScope scope)
    {
        if (body.Analysis?.Diagnostics.FirstOrDefault(d => d.Kind == AnalysisDiagnosticKind.Error) is { } error)
        {
            throw new ReplException(error.Message);
        }

        var count = body.Stack.Items.Count;
        if (body.Signature is not { } signature)
        {
            if (count > 1)
            {
                throw new ReplException($"ret needs at most one value; the stack holds {body.Stack.Render()}");
            }

            return;
        }

        var returnsVoid = SymbolIdentity.Equal(signature.ReturnType, TypeSymbol.Void);
        if (count != (returnsVoid ? 0 : 1)
            || (!returnsVoid && !SymbolFlowAnalysis.Rules(scope).CanAssign(body.Stack.Items[^1], signature.ReturnType)))
        {
            throw new ReplException(
                $"method {signature.Name} returns {SymbolRenderer.Pretty(signature.ReturnType)}; the stack holds {body.Stack.Render()}");
        }
    }

    private TypeSymbol BindType(string text) => SymbolBinder.BindType(CilSyntaxParser.ParseType(text), Scope()).Type;

    private AnalysisLocation FlowLocation(EditingBody body, string text)
    {
        if (_replayLocation is { } location)
        {
            return location;
        }

        var start = _documentRaw.Length - _documentRaw.TrimStart().Length;
        return new AnalysisLocation(body.LabelSpace.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _documentLine, start, _documentLine < 0 ? text.Length : _documentRaw.TrimEnd().Length - start);
    }

    private void AddFlowNode(EditingBody body, FlowNode<TypeSymbol> node, IBindingScope scope)
    {
        body.FlowNodes.Add(node);
        if (_analyzingDocument)
        {
            body.Analysis = null;
            _analysisBodies[body.LabelSpace] = body;
            return;
        }

        var analysis = SymbolFlowAnalysis.Run(body, scope);
        if (analysis.Diagnostics.FirstOrDefault(d => d.Kind == AnalysisDiagnosticKind.Error) is { } error)
        {
            body.FlowNodes.RemoveAt(body.FlowNodes.Count - 1);
            throw new ReplException(error.Message);
        }

        body.Analysis = analysis;
        body.Stack.CopyFrom(analysis.After[body.FlowNodes.Count - 1]);
        body.EndsFlow = analysis.End is null;
    }

    private static bool IsDirective(string text, string directive) => text.StartsWith(directive, StringComparison.Ordinal)
        && (text.Length == directive.Length || char.IsWhiteSpace(text[directive.Length]));

    private static bool StartsHandler(string text) => text.StartsWith("catch", StringComparison.Ordinal)
        || text.StartsWith("filter", StringComparison.Ordinal) || text.StartsWith("finally", StringComparison.Ordinal)
        || text.StartsWith("fault", StringComparison.Ordinal) || text.StartsWith("handler", StringComparison.Ordinal);
}
