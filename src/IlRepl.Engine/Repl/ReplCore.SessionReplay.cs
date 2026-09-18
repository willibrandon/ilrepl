using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Explicitly replays typed experiment source after validating it in an inactive runtime.
/// </summary>
public sealed partial class ReplCore
{
    /// <summary>
    /// Validates an entire experiment, then executes selected cells in source order in this fresh core.
    /// </summary>
    /// <param name="document">The experiment to execute.</param>
    /// <param name="numbers">Selected executable prompt numbers, or none for run-all.</param>
    /// <param name="cancellationToken">Cancels between source and execution boundaries.</param>
    /// <returns>The execution outcome with all experiment source retained.</returns>
    public HandleResult RunSession(SessionDocument document, IReadOnlyList<int> numbers, CancellationToken cancellationToken)
    {
        SessionCodec.Validate(document);
        ArgumentNullException.ThrowIfNull(numbers);
        cancellationToken.ThrowIfCancellationRequested();
        if (_sourceEntries.Count != 0 || Session.Submissions != 0)
        {
            throw new ReplException("session run requires a fresh execution host");
        }

        var selected = new HashSet<int>();
        foreach (var number in numbers)
        {
            if (!selected.Add(number))
            {
                throw new ReplException($"cell {number} was selected more than once");
            }
        }

        using var validation = new ReplCore(new Session { DeferActivation = true }, ColdOptions());
        validation._cancellationToken = cancellationToken;
        var diagnostics = validation.ReopenSession(document);
        if (diagnostics.Length != 0)
        {
            throw new ReplException("the experiment cannot run until its source and dependencies resolve:\n"
                + string.Join('\n', diagnostics));
        }

        // Editor text is source supplied for this explicit run, never an administrative command script.
        if (document.Editor.Lines.Any(line => !string.IsNullOrWhiteSpace(line)))
        {
            foreach (var line in document.Editor.Lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                validation.AppendReplaySource(line);
            }
        }

        if (validation._editBlock is not null)
        {
            throw new ReplException("the current edit is incomplete; close it before running the experiment");
        }

        validation.RequireNoOpenBlock();
        if (!validation.Session.Cell.IsEmpty)
        {
            validation.AppendReplayRun("ret");
        }

        var replay = validation.CaptureSession(new SessionEditor());
        var executable = replay.Entries.Where(entry => entry.Kind == SessionEntryKind.Run).Select(entry => entry.Number).ToHashSet();
        foreach (var number in numbers)
        {
            if (!executable.Contains(number))
            {
                throw new ReplException($"cell {number} is not a retained executable cell; use .session cells");
            }
        }

        // Validate every historical run boundary too; reopening itself intentionally permits unfinished source.
        using (var preflight = new ReplCore(new Session { DeferActivation = true }, ColdOptions()))
        {
            preflight._cancellationToken = cancellationToken;
            preflight._references.AddRange(replay.References);
            foreach (var asset in replay.Assets)
            {
                preflight._assets.Add(asset.Hash, asset);
            }

            var problems = new List<string>();
            preflight.PrepareReferenceImages(replay, problems);
            if (problems.Count != 0)
            {
                throw new ReplException(string.Join('\n', problems));
            }

            foreach (var entry in replay.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Kind == SessionEntryKind.Run)
                {
                    CellCompiler.RequireComplete(preflight.Session);
                }

                preflight.RestoreTrackedEntry(entry);
            }
        }

        _document = replay;
        _sourceEntries.AddRange(replay.Entries);
        _sourceCells.AddRange(replay.Cells);
        _references.AddRange(replay.References);
        foreach (var asset in replay.Assets)
        {
            _assets.Add(asset.Hash, asset);
        }

        Session.DeferActivation = true;
        var referenceProblems = new List<string>();
        PrepareReferenceImages(replay, referenceProblems);
        if (referenceProblems.Count != 0)
        {
            throw new ReplException(string.Join('\n', referenceProblems));
        }
        foreach (var entry in replay.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Kind != SessionEntryKind.Run || (selected.Count != 0 && !selected.Contains(entry.Number)))
            {
                RestoreTrackedEntry(entry);
                continue;
            }

            Note($"running cell {entry.Number} from the saved source");
            var output = new List<TranscriptLine>();
            void Collect(TranscriptLine line)
            {
                if (line.Kind is LineKind.Output or LineKind.Result or LineKind.Error)
                {
                    output.Add(line);
                }
            }

            Transcript.LineAdded += Collect;
            var previousNumber = CellNumber;
            HandleResult result;
            try
            {
                RestoreEntryComments(entry);
                result = HandleNormalized(NormalizedLine.FromText(".run"));
            }
            finally
            {
                Transcript.LineAdded -= Collect;
                _ = TrackSource(entry, previousNumber, false);
            }

            var index = _sourceCells.FindIndex(cell => cell.Number == entry.Number);
            if (index >= 0)
            {
                _sourceCells[index] = _sourceCells[index] with
                {
                    State = result.Succeeded ? "succeeded" : "failed",
                    Output = [.. output],
                };
            }

            if (!result.Succeeded)
            {
                foreach (var pending in _sourceCells.Select((cell, position) => (cell, position))
                    .Where(pair => pair.cell.Number > entry.Number && pair.cell.Kind == "cell").ToArray())
                {
                    _sourceCells[pending.position] = pending.cell with { State = "unrun", Output = [] };
                }

                Session.DeferActivation = true;
                foreach (var remaining in replay.Entries.SkipWhile(candidate => candidate.Identity != entry.Identity).Skip(1))
                {
                    RestoreTrackedEntry(remaining);
                }

                Note($"stopped at cell {entry.Number}; all source remains available");
                return result;
            }
        }

        return new HandleResult(true, false);
    }

    private void AppendReplaySource(string line)
    {
        var number = CellNumber;
        var normalized = Session.Normalize(line);
        SessionEntryKind kind;
        if (_editBlock is not null)
        {
            _ = ContinueEdit(normalized);
            kind = SessionEntryKind.EditSource;
        }
        else
        {
            var operation = ReplLineDispatcher.Classify(normalized, Session.OpenMethod is not null,
                Session.State.HasPendingLabels, Session.State.OpenBlockDepth > 0);
            if (operation.Kind == ReplLineKind.RetRuns || operation.Command == ".run"
                || (operation.Kind == ReplLineKind.Blank && !Session.Cell.IsEmpty && Session.OpenDepth == 0))
            {
                AppendReplayRun(line);
                return;
            }

            if (operation.Kind == ReplLineKind.Command)
            {
                if (operation.Command != ".edit" || !operation.Argument.EndsWith('{'))
                {
                    throw new ReplException($"the draft contains '{operation.Command}'; submit commands separately before session run");
                }

                _ = Edit(operation.Argument);
                kind = SessionEntryKind.Edit;
            }
            else
            {
                Session.AddLine(normalized);
                if (normalized.Text.StartsWith(".typeargs", StringComparison.Ordinal))
                {
                    _typeArgumentsSource = line;
                }

                kind = SessionEntryKind.Source;
            }
        }

        var entry = new SessionEntry { Number = number, Kind = kind, Source = [line] };
        if (kind == SessionEntryKind.Edit)
        {
            var edit = _editBlock is { } block ? Session.Edits.First(edit => edit.Name == block.Name) : Session.Edits[^1];
            entry = entry with { Edit = CaptureEdit(edit), Reference = edit.Name };
        }

        AppendSourceEntry(entry);
        var retained = TrackSource(entry, number, kind is SessionEntryKind.Edit or SessionEntryKind.EditSource);
        if (CellNumber != number)
        {
            AddReplayCell(number, "definition", retained);
        }
    }

    private void AppendReplayRun(string line)
    {
        CellCompiler.RequireComplete(Session);
        var number = CellNumber;
        var entry = new SessionEntry { Number = number, Kind = SessionEntryKind.Run, Source = [line] };
        AppendSourceEntry(entry);
        Session.RestoreRunBoundary();
        AddReplayCell(number, "cell", TrackSource(entry, number, false));
    }

    private void AddReplayCell(int number, string kind, string[] source)
    {
        _sourceCells.Add(new SessionCell
        {
            Number = number,
            Kind = kind,
            State = kind == "definition" ? "succeeded" : "unrun",
            Source = source,
            Inputs = [.. Session.DeclarationLines.Concat(_typeArgumentsSource is { } typeArguments ? [typeArguments] : [])],
        });
    }
}
