using IlRepl.Protocol;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Replays unsent input into managed symbols using the same syntax, binding and transitions as the live session.
/// </summary>
public sealed partial class EditingSession : IDisposable
{
    private static long s_sessionIdentity;
    private EditingSeed _seed;
    private readonly long _identity = -Interlocked.Increment(ref s_sessionIdentity);
    private readonly List<SkippedEditingLine> _skipped = [];
    private EditingState _initial;
    private EditingState _state;
    private string[] _prefix = [];
    private long _nextDefinition;
    private bool _disposed;

    /// <summary>
    /// Captures a live session without compiling definitions or evaluating argument values.
    /// </summary>
    /// <param name="session">The session, under the caller's session gate.</param>
    public EditingSession(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _seed = session.CaptureEditingSeed();
        _state = EmptyState();
        try
        {
            _state.Methods.AddRange(_seed.Snapshot.SessionMethods);
            _state.Definitions.AddRange(_seed.Definitions);
            foreach (var declaration in _seed.CellDeclarations)
            {
                AddLine(declaration);
            }

            foreach (var line in _seed.CellLines)
            {
                AddLine(line);
            }

            foreach (var line in _seed.OpenLines)
            {
                AddLine(line);
            }

            _state.InBlockComment = _seed.InBlockComment;
            _state.TypeArguments = _seed.TypeArguments;
            _initial = _state.Clone();
        }
        catch
        {
            _seed.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The live semantic revision captured by this editing session.
    /// </summary>
    public long Revision => _seed.Revision;

    /// <summary>
    /// Replays the prefix above the caret, reusing the previous prefix when it is unchanged.
    /// </summary>
    /// <param name="lines">The complete document.</param>
    /// <param name="caretLine">The line whose prefix context is requested.</param>
    /// <param name="inspecting">Whether the view names accepted generations for inspection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The independent editing context.</returns>
    public EditingView Speculate(
        IReadOnlyList<string> lines,
        int caretLine,
        bool inspecting = false,
        CancellationToken cancellationToken = default)
    {
        foreach (var _ in ReplayPrefix(lines, caretLine, cancellationToken))
        {
        }

        return View(inspecting);
    }

    /// <summary>
    /// Replays the same prefix cooperatively so a browser can process input and cancellation between batches.
    /// </summary>
    /// <param name="lines">The complete document.</param>
    /// <param name="caretLine">The line whose prefix context is requested.</param>
    /// <param name="inspecting">Whether the view names accepted generations for inspection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The independent editing context.</returns>
    public async ValueTask<EditingView> SpeculateAsync(
        IReadOnlyList<string> lines, int caretLine, bool inspecting = false, CancellationToken cancellationToken = default)
    {
        var processed = 0;
        foreach (var _ in ReplayPrefix(lines, caretLine, cancellationToken))
        {
            if (++processed % 16 == 0)
            {
                await Task.Yield();
            }
        }

        return View(inspecting);
    }

    private IEnumerable<int> ReplayPrefix(IReadOnlyList<string> lines, int caretLine, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentOutOfRangeException.ThrowIfNegative(caretLine);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(caretLine, lines.Count);
        var extends = _prefix.Length <= caretLine;
        for (var i = 0; extends && i < _prefix.Length; i++)
        {
            extends = string.Equals(_prefix[i], lines[i], StringComparison.Ordinal);
        }

        var start = extends ? _prefix.Length : 0;
        if (!extends)
        {
            _state = _initial.Clone();
            _skipped.Clear();
        }

        var complete = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = start; i < caretLine && !_state.Ended; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplyLine(lines[i], i);
                yield return i;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _prefix = [.. lines.Take(caretLine)];
            complete = true;
        }
        finally
        {
            if (!complete)
            {
                _state = _initial.Clone();
                _prefix = [];
                _skipped.Clear();
            }
        }
    }

    /// <summary>
    /// Releases metadata leases and every managed preview checkpoint.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _seed.Dispose();
        _seed = null!;
        _initial = null!;
        _state = null!;
        _prefix = [];
        _skipped.Clear();
    }

    private EditingState EmptyState()
    {
        var types = _seed.Snapshot.Types.Clone();
        return new EditingState { Types = types, CommittedTypes = types.Clone() };
    }

    private DefinitionId NextDefinition() => DefinitionId.ForDeclaration(_identity, ++_nextDefinition);

    private SnapshotBindingScope Scope(EditingBody? body = null, bool inspecting = false, bool isolated = false)
    {
        body ??= _state.Body;
        var types = inspecting ? _state.CommittedTypes : _state.Types;
        if (isolated)
        {
            types = types.Clone();
        }
        var generics = body.Generics;
        var access = body.Access;
        if (_state.Method is null && _state.OpenTypes.LastOrDefault() is { } owner)
        {
            generics = new SymbolGenericContext(
                types.DeclarationOf(owner.Type)?.GenericParameters.Select(parameter => parameter.AsType).ToArray() ?? [], []);
            access = new AccessContext(owner.Type, "class " + owner.Path);
        }

        IReadOnlyList<MethodSymbol> methods = _state.Method is { Signature: { DeclaringType: null } method } && !inspecting
            ? [.. _state.Methods.Where(candidate => candidate.Name != method.Name), method] : _state.Methods;
        var context = _seed.Snapshot.WithContext(
            types, isolated ? [.. methods] : methods, generics,
            isolated ? [.. body.Locals] : body.Locals, isolated ? [.. body.Arguments] : body.Arguments,
            access, body.ThisIndex, inspecting);
        return new SnapshotBindingScope(context);
    }

    private EditingView View(bool inspecting)
    {
        var body = _state.Body;
        var scope = Scope(inspecting: inspecting, isolated: true);
        return new EditingView(
            scope.Snapshot, scope, _state.OpenTypes.LastOrDefault()?.Type, body.Signature,
            [.. body.Stack.Items], body.Instructions.LastOrDefault(),
            new HashSet<string>(body.Labels, StringComparer.Ordinal),
            body.ReferencedLabels.Except(body.Labels).ToHashSet(StringComparer.Ordinal),
            body.LabelSpace, _state.InBlockComment, [.. _skipped])
        {
            TypeArguments = _state.TypeArguments,
            ThisSlots = [.. Enumerable.Range(0, body.Stack.Items.Count).Select(body.Stack.IsThisAt)],
            OwnerKind = _state.OpenTypes.LastOrDefault()?.Header.Kind,
            DeclarationContext = DeclarationContext(),
        };
    }

    private string DeclarationContext()
    {
        var words = _state.Definitions.SelectMany(definition =>
            new[] { definition.IsFamily ? "type" : "method", definition.Name, definition.Header }.Concat(definition.Lines))
            .Concat(_state.CellDeclarations)
            .Concat(_state.OpenTypes.Take(1).SelectMany(block => new[] { block.HeaderLine }.Concat(block.Lines)))
            .Concat(_state.Method is { Header: { } header } ? [header] : []);
        return string.Concat(words.Select(word => word.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + word));
    }

    private void ApplyLine(string raw, int lineNumber)
    {
        var checkpoint = _state.Clone();
        var commentBefore = _state.InBlockComment;
        var commentAfter = commentBefore;
        var kind = CilLexer.Classify(raw, ref commentAfter, out var text);
        _state.InBlockComment = commentAfter;
        var normalized = new NormalizedLine(raw, text, kind, commentBefore);
        var operation = ReplLineDispatcher.Classify(
            normalized, _state.Method is not null, _state.Body.HasPendingLabels, _state.Body.Frames.Count > 0);
        try
        {
            switch (operation.Kind)
            {
                case ReplLineKind.Comment:
                    return;
                case ReplLineKind.Blank:
                    RunBoundary();
                    return;
                case ReplLineKind.RetRuns:
                    RunBoundary();
                    return;
                case ReplLineKind.Command:
                    ApplyCommand(operation.Command!, operation.Argument);
                    return;
                default:
                    AddLine(text);
                    return;
            }
        }
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            _state = checkpoint;
            _skipped.Add(new SkippedEditingLine(lineNumber, raw, exception.Message));
        }
    }
}
