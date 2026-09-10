using IlRepl.Protocol;

namespace IlRepl.Engine.Binding;

public sealed partial class EditingSession
{
    /// <summary>
    /// Analyzes the full document while retaining declaration visibility at the caret.
    /// </summary>
    /// <param name="request">The unsent document and caret.</param>
    /// <param name="cancellationToken">Cancels replay and graph analysis.</param>
    /// <returns>The caret view and source analysis tied to the captured session.</returns>
    public ValueTask<AnalysisReply> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken = default) =>
        AnalyzeCoreAsync(request, false, cancellationToken);

    private FlowState<TypeSymbol>? _caretFlow;

    /// <summary>
    /// The reusable presentation for the last completely analyzed document.
    /// </summary>
    internal AnalyzedDocument? AnalyzedDocument { get; private set; }

    /// <summary>
    /// Supplies completion with the stack that the whole document brings to its caret.
    /// </summary>
    internal async ValueTask<EditingView> WithFlowAsync(EditingView view, IReadOnlyList<string> lines, int line, int caret,
        CancellationToken cancellationToken)
    {
        var reply = await AnalyzeCoreAsync(new AnalysisRequest(lines, line, caret, 0), true, cancellationToken).ConfigureAwait(false);
        return view with
        {
            Stack = _caretFlow?.Values?.Select(value => value.Type).ToArray() ?? [],
            ThisSlots = _caretFlow?.Values?.Select(value => value.IsThis).ToArray() ?? [],
            StackKind = reply.Stack?.Kind ?? AnalyzedStackKind.Unknown,
        };
    }

    private async ValueTask<AnalysisReply> AnalyzeCoreAsync(AnalysisRequest request, bool fromCaret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Line);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Line, request.Lines.Count);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = _state;
        var skipped = _skipped.ToArray();
        _state = fromCaret ? _state.Clone() : _initial.Clone();
        _skipped.Clear();
        _analysisBodies.Clear();
        _analyzingDocument = true;
        var caretBody = _state.Body;
        var caretPosition = caretBody.FlowNodes.Count;
        var hasBody = true;
        var positions = new List<(long Body, int Node, bool HasBody)>();
        AnalyzedDocument = null;
        try
        {
            for (var line = fromCaret ? request.Line : 0; line <= request.Lines.Count && !_state.Ended; line++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var declaration = false;
                if (line < request.Lines.Count)
                {
                    var comment = _state.InBlockComment;
                    var kind = CilLexer.Classify(request.Lines[line], ref comment, out var source);
                    declaration = kind == SourceLineKind.Text && IsDeclarationLine(source);
                }
                var bodyPosition = (_state.Body.LabelSpace, _state.Body.FlowNodes.Count,
                    !declaration && (_state.Method is not null || _state.OpenTypes.Count == 0));
                positions.Add(bodyPosition);
                _analysisBodies.TryAdd(_state.Body.LabelSpace, _state.Body);
                if (line == request.Line)
                {
                    caretBody = _state.Body;
                    caretPosition = caretBody.FlowNodes.Count;
                    hasBody = bodyPosition.Item3;
                }

                if (line == request.Lines.Count)
                {
                    break;
                }

                ApplyLine(request.Lines[line], line);
                if (line % 16 == 15)
                {
                    await Task.Yield();
                }
            }

            if (_analysisBodies.TryGetValue(caretBody.LabelSpace, out var finalBody))
            {
                caretBody = finalBody;
            }

            _analysisBodies.TryAdd(caretBody.LabelSpace, caretBody);
            foreach (var body in _analysisBodies.Values)
            {
                body.Analysis = await SymbolFlowAnalysis.RunAsync(body, Scope(body), cancellationToken).ConfigureAwait(false);
            }

            var result = caretBody.Analysis!;
            var incoming = result.Before[Math.Min(caretPosition, result.Before.Length - 1)];
            _caretFlow = incoming;
            var stack = new AnalyzedStack(incoming?.Kind ?? AnalyzedStackKind.Unreachable,
                incoming?.Values?.Select(value => EditingStack.Name(value.Type)).ToArray() ?? [], result.Incomplete);
            var diagnostics = _analysisBodies.Values.SelectMany(body => body.Analysis?.Diagnostics ?? [])
                .Where(diagnostic => diagnostic.Location.Line >= 0
                    && (diagnostic.Code != "FLOW004" || !_skipped.Any(line => line.Line == diagnostic.Location.Line))).ToList();
            foreach (var refused in _skipped)
            {
                var (kind, message) = RefusalDiagnostic(refused);
                diagnostics.Add(new AnalysisDiagnostic("FLOW008", kind, message,
                    new AnalysisLocation("document", refused.Line, refused.Text.Length - refused.Text.TrimStart().Length,
                        refused.Text.Trim().Length), []));
            }

            var instruction = caretPosition < caretBody.FlowNodes.Count
                && caretBody.FlowNodes[caretPosition].Location.Line == request.Line
                && caretBody.FlowNodes[caretPosition] is { Instruction: not null, Synthetic: false };
            var reply = new AnalysisReply(request.DocumentVersion, Revision, _identity, 0,
                hasBody ? stack : null, instruction,
                diagnostics.OrderBy(diagnostic => diagnostic.Location.Line).ThenBy(diagnostic => diagnostic.Code).ToArray());
            if (!fromCaret)
            {
                var presentations = positions.Select((position, line) =>
                {
                    var body = _analysisBodies[position.Body];
                    var analysis = body.Analysis!;
                    var state = analysis.Before[Math.Min(position.Node, analysis.Before.Length - 1)];
                    var display = position.HasBody ? new AnalyzedStack(state?.Kind ?? AnalyzedStackKind.Unreachable,
                        state?.Values?.Select(value => EditingStack.Name(value.Type)).ToArray() ?? [], analysis.Incomplete) : null;
                    var beforeInstruction = position.Node < body.FlowNodes.Count && body.FlowNodes[position.Node].Location.Line == line
                        && body.FlowNodes[position.Node] is { Instruction: not null, Synthetic: false };
                    return (display, beforeInstruction);
                }).ToArray();
                AnalyzedDocument = new AnalyzedDocument([.. request.Lines], reply, presentations);
            }

            return reply;
        }
        finally
        {
            _state = previous;
            _skipped.Clear();
            _skipped.AddRange(skipped);
            _analysisBodies.Clear();
            _analyzingDocument = false;
            _documentLine = -1;
            _documentRaw = "";
        }
    }

    private static readonly string[] s_declarationDirectives =
    [
        ".method", ".class", ".field", ".property", ".event", ".locals", ".args", ".param", ".custom",
        ".override", ".pack", ".size", ".typeparams", ".typeargs", ".maxstack",
    ];

    private static bool IsDeclarationLine(string line)
    {
        var text = line.TrimStart();
        return s_declarationDirectives.Any(directive => IsDirective(text, directive));
    }


    private static (AnalysisDiagnosticKind Kind, string Message) RefusalDiagnostic(SkippedEditingLine refused)
    {
        var text = refused.Text.Trim();
        var first = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        const string unknownOpcode = "unknown opcode '";
        if (refused.Message.StartsWith(unknownOpcode, StringComparison.Ordinal))
        {
            var end = refused.Message.IndexOf('\'', unknownOpcode.Length);
            var mnemonic = end < 0 ? "" : refused.Message[unknownOpcode.Length..end];
            return InstructionParser.IsOpcodePrefix(mnemonic)
                ? (AnalysisDiagnosticKind.Incomplete, $"finish opcode '{mnemonic}'; Tab completes it")
                : (AnalysisDiagnosticKind.Error, refused.Message);
        }

        var kind = text == first || text.EndsWith("::", StringComparison.Ordinal) || text.EndsWith('(') || text.EndsWith('<')
            || text.EndsWith(',') || text.EndsWith('"') && text.Count(character => character == '"') % 2 != 0
            ? AnalysisDiagnosticKind.Incomplete : AnalysisDiagnosticKind.Error;
        return (kind, refused.Message);
    }

}
