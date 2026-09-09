using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Completes operands against a leased editing snapshot while keeping submission, runtime state and transcript untouched.
/// </summary>
public sealed partial class OperandCompleter : IDisposable
{
    private static readonly CaretClassifier Classifier = new(new CilTokenizer(CilVocabularyBuilder.Vocabulary));
    private readonly Session _session;
    private readonly Guid _identity = Guid.NewGuid();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CompletionContinuation> _continuations = new(StringComparer.Ordinal);
    private EditingSession? _editing;
    private EditingSession? _activeEditing;
    private CancellationTokenSource? _activeCancellation;
    private CompletionQuery? _query;
    private long[] _assemblies = [];
    private long _bindingEpoch;
    private bool _disposed;

    /// <summary>
    /// Initializes completion and subscribes to every semantic mutation of its owning session.
    /// </summary>
    /// <param name="session">The session, whose caller serializes real input with completion.</param>
    public OperandCompleter(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _session.CompletionChanged += Invalidate;
    }

    /// <summary>
    /// Returns a confirmed page for the full document and captured semantic context without submitting any input.
    /// </summary>
    /// <param name="request">The document, caret and optional paging token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The confirmed candidate page or a stale-cursor rejection.</returns>
    public async Task<CompletionReply> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var document = new CompletionDocumentKey(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshCatalog();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = cancellation;
            var token = cancellation.Token;
            token.ThrowIfCancellationRequested();
            if (request.Cursor is not null)
            {
                if (_query is not { } existing || existing.Cursor != request.Cursor
                    || !existing.Identity.Document.Equals(document) || existing.Identity.Revision != _session.CompletionRevision
                    || existing.Identity.BindingEpoch != _bindingEpoch)
                {
                    return CompletionReply.CursorRejected(_session.CompletionRevision, _bindingEpoch);
                }

                _activeEditing = _editing;
                return await PageAsync(existing, token).ConfigureAwait(false);
            }

            if (_editing is null)
            {
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                _editing = new EditingSession(_session);
            }

            _activeEditing = _editing;
            var view = await _editing.SpeculateAsync(document.Lines, document.Line, cancellationToken: token).ConfigureAwait(false);
            foreach (var source in view.Snapshot.Catalog.Sources)
            {
                await source.WarmIndexAsync(token).ConfigureAwait(false);
            }
            var site = Classifier.Classify(document.Lines[document.Line], document.Caret, view.InBlockComment);
            if (!site.IsOperand)
            {
                _query = null;
                PruneContinuations(document);
                return CompletionReply.Empty(_session.CompletionRevision, _bindingEpoch);
            }

            if (site.Owner is ".dis" or ".disassemble")
            {
                view = await _editing.SpeculateAsync(document.Lines, document.Line, inspecting: true,
                    cancellationToken: token).ConfigureAwait(false);
            }

            var identity = new CompletionQueryIdentity(_identity, _editing.Revision, _bindingEpoch, document, site);
            if (_query is { } previous && previous.Identity == identity)
            {
                previous.Position = 0;
                previous.Confirmed = 0;
                previous.Cursor = null;
                return await PageAsync(previous, token).ConfigureAwait(false);
            }

            _query = null;
            PruneContinuations(document);
            var query = await BuildQueryAsync(identity, view, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _query = query;
            return await PageAsync(query, token).ConfigureAwait(false);
        }
        catch
        {
            _query = null;
            throw;
        }
        finally
        {
            _activeCancellation = null;
            if (_activeEditing is { } active && !ReferenceEquals(active, _editing))
            {
                active.Dispose();
            }

            _activeEditing = null;
            _gate.Release();
        }
    }

    /// <summary>
    /// Cancels active work and immediately drops idle snapshots, pages and selected definitions.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.CompletionChanged -= Invalidate;
        Invalidate();
    }

    private async ValueTask<CompletionQuery> BuildQueryAsync(
        CompletionQueryIdentity identity, EditingView view, CancellationToken cancellationToken)
    {
        var document = identity.Document;
        var site = identity.Site;
        var source = new CompletionCandidateSource(view, site, document.Lines[document.Line]);
        await source.PrepareAsync(cancellationToken).ConfigureAwait(false);
        var generics = new GenericCompletionBinding(view, source);
        var selected = SelectedGeneric(document, site, view);
        var arguments = site.Kind == CompletionSiteKind.TypeArgument ? generics.Arguments(document, site, selected) : [];
        var methods = site.Kind == CompletionSiteKind.Signature ? generics.Signatures(document, site, selected) : [];
        var labels = view.DefinedLabels.Concat(view.PendingLabels).ToHashSet(StringComparer.Ordinal);
        if (site.Kind == CompletionSiteKind.Label)
        {
            labels.UnionWith(_activeEditing!.ForwardLabels(document.Lines, document.Line, site, cancellationToken));
        }

        var comment = false;
        var suffix = CilLexer.StripComments(document.Lines[document.Line][site.ReplaceEnd..], ref comment).AsSpan().TrimStart();
        var hasElementSuffix = suffix.Length > 0 && (suffix[0] is '[' or '*' or '&'
            || suffix.StartsWith("modopt(", StringComparison.Ordinal) || suffix.StartsWith("modreq(", StringComparison.Ordinal));
        var candidates = await source.GatherAsync(arguments, methods, labels, hasElementSuffix, cancellationToken).ConfigureAwait(false);
        var prefix = site.Kind == CompletionSiteKind.Signature ? "" : CompletionCandidateSource.DecodePrefix(site.Prefix);
        var ranked = CandidateRanker.Rank(candidates, prefix, candidate => candidate.Rank, cancellationToken);
        return new CompletionQuery
        {
            Identity = identity, View = view, Candidates = ranked, Kind = KindOf(site),
            Types = new TypeSpeller((SnapshotBindingScope)view.Scope),
            Members = new MemberSpeller((SnapshotBindingScope)view.Scope),
            Owners = arguments.Select(argument => ArgumentHint(argument.Target, site.ArgumentIndex))
                .Distinct(StringComparer.Ordinal).ToArray(),
            AmbiguousNames = ranked.Where(candidate => candidate.Method is not null).GroupBy(candidate => candidate.Method!.Name)
                .Where(group => group.Select(candidate => candidate.Method!.DeclaringType).Distinct().Skip(1).Any())
                .Select(group => group.Key).ToHashSet(StringComparer.Ordinal),
        };
    }

    private async ValueTask<CompletionReply> PageAsync(CompletionQuery query, CancellationToken cancellationToken)
    {
        var items = new List<CompletionItem>();
        var examined = 0;
        while (query.Position < query.Candidates.Count && items.Count < CompletionReply.PageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = query.Candidates[query.Position++];
            if (Confirm(query, candidate) is { } item)
            {
                items.Add(item);
                query.Confirmed++;
            }

            if (++examined % 16 == 0)
            {
                await Task.Yield();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var hasMore = query.Position < query.Candidates.Count;
        query.Cursor = hasMore ? Guid.NewGuid().ToString("N") : null;
        return new CompletionReply(query.Kind, query.Identity.Site.ReplaceStart, query.Identity.Site.ReplaceLength,
            items, query.Cursor, hasMore ? query.Confirmed + query.Candidates.Count - query.Position : query.Confirmed,
            hasMore, query.Identity.Revision, query.Id, query.Identity.BindingEpoch, query.Owners);
    }

    private GenericCompletionTarget? SelectedGeneric(CompletionDocumentKey document, CompletionSite site, EditingView view)
    {
        var span = GenericArgumentSpan.At(document.Lines[document.Line], document.Caret, view.InBlockComment,
            afterClose: site.Kind == CompletionSiteKind.Signature);
        if (span is null)
        {
            return null;
        }

        foreach (var anchor in document.Anchors.OrderByDescending(anchor => anchor.Start))
        {
            if (anchor.Line == document.Line && anchor.End == span.Open + 1 && AnchorValid(document, anchor)
                && _continuations.TryGetValue(anchor.Token, out var continuation))
            {
                return continuation.Rebind(view);
            }
        }

        return null;
    }

    private void PruneContinuations(CompletionDocumentKey document)
    {
        var retained = document.Anchors.Where(anchor => AnchorValid(document, anchor))
            .Select(anchor => anchor.Token).ToHashSet(StringComparer.Ordinal);
        foreach (var token in _continuations.Keys.Where(token => !retained.Contains(token)).ToArray())
        {
            _continuations.Remove(token);
        }
    }

    private bool AnchorValid(CompletionDocumentKey document, ContinuationAnchor anchor) =>
        anchor.Line >= 0 && anchor.Line < document.Lines.Count && anchor.Start >= 0 && anchor.End >= anchor.Start
        && anchor.End <= document.Lines[anchor.Line].Length && _continuations.TryGetValue(anchor.Token, out var continuation)
        && continuation.Revision == _session.CompletionRevision && continuation.BindingEpoch == _bindingEpoch
        && document.Lines[anchor.Line].AsSpan(anchor.Start, anchor.End - anchor.Start).SequenceEqual(continuation.Text);

    private void RefreshCatalog()
    {
        var assemblies = AssemblySequence();
        if (!assemblies.SequenceEqual(_assemblies))
        {
            Invalidate();
            _assemblies = assemblies;
            _bindingEpoch++;
        }
    }

    private long[] AssemblySequence()
    {
        var assemblies = _session.Resolver.Assemblies
            .Concat(_session.Types.Where(type => type.Definition is not null).Select(type => type.Definition!.Assembly))
            .Concat(_session.Methods.SelectMany(method => new[]
                { method.Trampoline.Definition.Assembly, method.Version.Definition.Assembly })).Distinct().ToList();
        var seen = assemblies.ToHashSet();
        for (var index = 0; index < assemblies.Count; index++)
        {
            foreach (var target in RuntimeBindingObservations.Capture(assemblies[index]).Values)
            {
                if (RuntimeDefinitions.TypeOf(target.Definition) is { } observed && seen.Add(observed.Assembly))
                {
                    assemblies.Add(observed.Assembly);
                }
            }
        }

        return assemblies.SelectMany(assembly => new[]
            { RuntimeDefinitions.AssemblyInstance(assembly), RuntimeBindingObservations.VersionOf(assembly) }).ToArray();
    }

    private void Invalidate()
    {
        _activeCancellation?.Cancel();
        _query = null;
        _continuations.Clear();
        if (_editing is { } editing && !ReferenceEquals(editing, _activeEditing))
        {
            editing.Dispose();
        }

        _editing = null;
    }

    private static CompletionKind KindOf(CompletionSite site) => site.Kind switch
    {
        CompletionSiteKind.Type or CompletionSiteKind.MemberHead => CompletionKind.Types,
        CompletionSiteKind.Method or CompletionSiteKind.Constructor => CompletionKind.Members,
        CompletionSiteKind.Field => CompletionKind.Fields,
        CompletionSiteKind.Local => CompletionKind.Locals,
        CompletionSiteKind.Argument => CompletionKind.Arguments,
        CompletionSiteKind.Label => CompletionKind.Labels,
        CompletionSiteKind.GenericParameter => CompletionKind.GenericParameters,
        CompletionSiteKind.TypeArgument => CompletionKind.TypeArguments,
        CompletionSiteKind.Signature => CompletionKind.Signatures,
        _ => CompletionKind.None,
    };

    private static string ArgumentHint(GenericCompletionTarget target, int index) =>
        $"{index + 1} of {target.Parameters.Count} ({target.Parameters[index].Name}) of {target.Label}";
}
