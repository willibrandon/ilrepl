using Hex1b.Documents;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Keeps contextual help read-only and ties every action to the source and analysis it needs.
/// </summary>
public sealed class PromptHelp
{
    private string _text = "";
    private long _version;
    private int _caret;
    private long _revision;
    private long _assemblies;
    private AnalysisReply? _analysis;
    private CompletionSnapshot? _completion;
    private int _selection;
    private PaletteMode _palette;
    private bool _ready;
    private string? _catalogUrl;
    private bool _hasDiagnostic;
    private IReadOnlyList<TranscriptLine> _details = [];
    private List<HelpAction> _actions = [];

    /// <summary>
    /// The first wrapped content row shown in the viewport.
    /// </summary>
    public int Scroll { get; set; }

    /// <summary>
    /// The action selected by Tab or Shift+Tab.
    /// </summary>
    public int SelectedAction { get; private set; }

    /// <summary>
    /// The available current-document sources and documentation link.
    /// </summary>
    public IReadOnlyList<HelpAction> Actions => _actions;

    /// <summary>
    /// Names the instruction whose explanation is displayed.
    /// </summary>
    public string Heading { get; private set; } = "help";

    /// <summary>
    /// Opens contextual help without changing the editor or its completion selection.
    /// </summary>
    public static void Open(PromptState state, IReadOnlyList<CompletionItem> catalog)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Help = new PromptHelp();
        state.Help.Refresh(state, catalog);
    }

    /// <summary>
    /// Closes help without editing source or changing the selection and undo history.
    /// </summary>
    public static void Close(PromptState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Help = null;
        state.DocumentationTargetChanged?.Invoke(null, state.HelpInputSequence, false);
    }

    /// <summary>
    /// Validates the source for every action and the current binding context for session-dependent evidence.
    /// </summary>
    public bool IsCurrent(PromptState state) => MatchesSource(state)
        && (_catalogUrl is not null && !_hasDiagnostic || MatchesAnalysis(state));

    /// <summary>
    /// Keeps catalogue links usable during analysis refresh while source actions require current evidence.
    /// </summary>
    public bool IsActionCurrent(PromptState state, int index) => index >= 0 && index < _actions.Count && MatchesSource(state)
        && (IsCurrent(state) || _actions[index].Url is { } url && url == _catalogUrl);

    /// <summary>
    /// Replaces help only with fresh diagnostic, completion, or caret facts, in that order.
    /// </summary>
    public void Refresh(PromptState state, IReadOnlyList<CompletionItem> catalog)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        state.Analyzer?.Refresh(state);
        if (IsCurrent(state) && MatchesAnalysis(state))
        {
            PublishTarget(state);
            return;
        }

        var diagnostic = PromptDiagnostics.Visible(state).FirstOrDefault(item => PromptDiagnostics.AtCaret(item, state)
            && item.Kind == AnalysisDiagnosticKind.Error)
            ?? PromptDiagnostics.Visible(state).FirstOrDefault(item => PromptDiagnostics.AtCaret(item, state));
        var candidates = PromptWidget.Candidates(state, catalog);
        var completion = candidates.Count > 0 ? candidates[Math.Clamp(state.SelectedIndex, 0, candidates.Count - 1)] : null;
        var help = diagnostic?.Explanation?.Instruction ?? completion?.InstructionHelp ?? state.Analysis?.InstructionHelp;
        if (diagnostic is null && help is null)
        {
            diagnostic = PromptDiagnostics.Current(state);
            help = diagnostic?.Explanation?.Instruction;
        }
        if (diagnostic is null && help is null && (state.Analyzer?.IsPending == true || state.Requester?.IsPending == true))
        {
            _ready = false;
            PublishTarget(state);
            return;
        }

        var sameSource = _text == state.Text && _version == state.Editor.Document.Version
            && _caret == state.Editor.Cursor.Position.Value;
        _text = state.Text;
        _version = state.Editor.Document.Version;
        _caret = state.Editor.Cursor.Position.Value;
        _revision = state.HelpRevision;
        _assemblies = state.HelpAssemblyVersion;
        _analysis = state.Analysis;
        _completion = state.Completions;
        _selection = state.SelectedIndex;
        _palette = state.Palette;
        _ready = true;
        _hasDiagnostic = diagnostic is not null;
        // A completion's catalogue link stays valid even when its accompanying incomplete-opcode diagnostic needs refreshing.
        _catalogUrl = help is not null && ReferenceEquals(help, completion?.InstructionHelp)
            && catalog.Any(item => ReferenceEquals(item, completion)) ? help.DocumentationUrl : null;
        Heading = help is null ? "help" : "help · " + help.Mnemonic;
        var details = new List<TranscriptLine>();
        var actions = new List<HelpAction>();
        if (diagnostic is not null)
        {
            var display = PromptDiagnostics.Display(state)!;
            details.Add(TranscriptLine.Of(LineKind.Info, display.Text, display.Style));
            details.AddRange(PromptHelpContent.Diagnostic(diagnostic, state.Tokenizer));
            var sources = diagnostic.Explanation is { } explanation
                ? explanation.Conflicts.SelectMany(conflict => conflict.Producers)
                    .Concat(explanation.Incoming.Select(path => path.Source))
                    .Concat(explanation.Incoming.SelectMany(path => path.Values).SelectMany(value => value.Producers))
                : [];
            foreach (var source in sources.Distinct().Where(source => source.Kind == AnalysisSourceKind.Document
                && source.Location.Offset is null && source.Location.Line >= 0 && source.Location.Line < state.LineCount))
            {
                actions.Add(new HelpAction("Go to " + DiagnosticFormatter.Source(source), source));
            }
        }

        if (help is not null)
        {
            if (diagnostic is not null)
            {
                details.Add(TranscriptLine.Of(LineKind.Info, ""));
            }
            if (!ReferenceEquals(help, diagnostic?.Explanation?.Instruction))
            {
                details.Add(new TranscriptLine(LineKind.Info, state.Tokenizer.Spans(help.Syntax)));
            }
            details.Add(PromptHelpContent.Effect(help.StackEffect));
            details.Add(TranscriptLine.Of(LineKind.Info, help.Explanation));
            details.AddRange(help.Notes.Select(note => TranscriptLine.Of(LineKind.Info, "• " + note, SpanStyle.Dim)));
            actions.Add(new HelpAction(help.DocumentationUrl, Url: help.DocumentationUrl));
        }
        else if (diagnostic is null)
        {
            details.Add(TranscriptLine.Of(LineKind.Info,
                "Move the caret to an instruction or select an opcode or operand to see its help."));
        }

        var sameTargets = sameSource && _actions.SequenceEqual(actions);
        _details = details;
        _actions = actions;
        if (!sameTargets)
        {
            Scroll = 0;
            SelectedAction = 0;
        }
        PublishTarget(state);
    }

    /// <summary>
    /// Wraps all help content, preserving complete signatures and a copyable documentation URL.
    /// </summary>
    public IReadOnlyList<(string Text, int Action)> Lines(int width)
        => Rows(width).Select(row => (row.Line.PlainText, row.Action)).ToArray();

    /// <summary>
    /// Folds styled content and actions with the transcript's word wrapping and hanging indentation.
    /// </summary>
    public IReadOnlyList<(TranscriptLine Line, int Action)> Rows(int width)
    {
        var rows = new List<(TranscriptLine, int)>();
        foreach (var detail in _details)
        {
            Add(detail, -1);
        }

        if (_actions.Count > 0)
        {
            Add(TranscriptLine.Of(LineKind.Info, ""), -1);
        }
        for (var index = 0; index < _actions.Count; index++)
        {
            var style = index == SelectedAction ? SpanStyle.TopType : SpanStyle.Member;
            var indent = Math.Min(2, Math.Max(0, width - 1));
            var first = true;
            foreach (var spans in TranscriptLineFolder.Fold([new(_actions[index].Label, style)], Math.Max(1, width - indent)))
            {
                var prefix = first && index == SelectedAction ? "❯ "[..indent] : new string(' ', indent);
                rows.Add((new TranscriptLine(LineKind.Info, [new(prefix, style), .. spans]), index));
                first = false;
            }
        }

        return rows;

        void Add(TranscriptLine line, int action) => rows.AddRange(TranscriptLineFolder.Fold(line.Spans, Math.Max(1, width))
            .Select(spans => (new TranscriptLine(line.Kind, spans), action)));
    }

    /// <summary>
    /// Cycles action selection and brings the complete target into the viewport.
    /// </summary>
    public void MoveAction(PromptState state, bool backwards, int width, int height)
    {
        if (_actions.Count == 0)
        {
            return;
        }

        var selected = (SelectedAction + _actions.Count + (backwards ? -1 : 1)) % _actions.Count;
        if (!IsActionCurrent(state, selected))
        {
            return;
        }
        SelectedAction = selected;
        var rows = Lines(width);
        var target = Enumerable.Range(0, rows.Count).First(index => rows[index].Action == SelectedAction);
        Scroll = Math.Clamp(target, 0, Math.Max(0, rows.Count - Math.Max(1, height)));
        PublishTarget(state);
    }

    /// <summary>
    /// Opens the selected documentation or moves to a producer in the current document.
    /// </summary>
    public void Activate(PromptState state)
    {
        if (!IsActionCurrent(state, SelectedAction))
        {
            return;
        }

        var action = _actions[SelectedAction];
        if (action.Url is { } url)
        {
            state.OpenDocumentation?.Invoke(url);
        }
        else if (action.Source is { Kind: AnalysisSourceKind.Document } source)
        {
            var document = state.Editor.Document;
            var line = source.Location.Line + 1;
            var column = Math.Clamp(source.Location.Start, 0, document.GetLineText(line).Length) + 1;
            state.Editor.SetCursorPosition(document.PositionToOffset(new DocumentPosition(line, column)));
            Close(state);
        }
    }

    private void PublishTarget(PromptState state) => state.DocumentationTargetChanged?.Invoke(
        IsActionCurrent(state, SelectedAction) ? _actions[SelectedAction].Url : null, state.HelpInputSequence, true);

    private bool MatchesSource(PromptState state) => ReferenceEquals(state.Help, this) && _ready && !state.Busy && _text == state.Text
        && _version == state.Editor.Document.Version && _caret == state.Editor.Cursor.Position.Value
        && _selection == state.SelectedIndex && _palette == state.Palette;

    private bool MatchesAnalysis(PromptState state) =>
        (_revision, _assemblies) == (state.CurrentHelpIdentity?.Invoke() ?? (state.HelpRevision, state.HelpAssemblyVersion))
        && ReferenceEquals(_analysis, state.Analysis) && ReferenceEquals(_completion, state.Completions);
}
