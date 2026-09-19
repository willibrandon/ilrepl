using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Retains editable source transitions independently of scrollback and reconstructs them without execution.
/// </summary>
public sealed partial class ReplCore
{
    private readonly List<SessionEntry> _sourceEntries = [];
    private readonly List<SessionCell> _sourceCells = [];
    private readonly List<SessionReference> _references = [];
    private readonly Dictionary<string, SessionAsset> _assets = new(StringComparer.Ordinal);
    private SessionDocument _document = new();
    private SessionRuntime? _recordedRuntime;
    private string? _typeArgumentsSource;
    private SessionEntry? _groupedEntry;
    private readonly List<string> _groupedSource = [];
    private int _groupedEntryIndex;

    /// <summary>
    /// The preserved reference requests in source order.
    /// </summary>
    public IReadOnlyList<SessionReference> References => _references;

    /// <summary>
    /// Routes local file references through the execution host's verified dependency tooling when available.
    /// </summary>
    public bool ReferenceActions { get; set; }

    /// <summary>
    /// The argument, local, and generic declarations required to reconstruct the current cell without runtime values.
    /// </summary>
    public string[] PendingInputDeclarations => Session.DeclarationLines
        .Concat(_typeArgumentsSource is { } typeArguments ? [typeArguments] : []).ToArray();

    /// <summary>
    /// Captures editable source and historical output with a matching frontend editor snapshot.
    /// </summary>
    /// <param name="editor">The unsent editor document.</param>
    /// <returns>The portable source document.</returns>
    public SessionDocument CaptureSession(SessionEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _cancellationToken.ThrowIfCancellationRequested();
        FlushSourceEntry();
        return _document with
        {
            Runtime = _recordedRuntime ??= new SessionRuntime
            {
                IlreplVersion = typeof(ReplCore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion ?? "",
                Framework = "net10.0",
                Description = RuntimeInformation.FrameworkDescription,
                Rid = RuntimeInformation.RuntimeIdentifier,
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Culture = CultureInfo.CurrentCulture.Name,
            },
            Entries = [.. _sourceEntries],
            Cells = [.. _sourceCells],
            Editor = editor,
            References = [.. _references],
            Assets = [.. _assets.Values],
        };
    }

    /// <summary>
    /// Reconstructs a validated document in a fresh core without replaying commands or executing cells.
    /// </summary>
    /// <param name="document">The validated source document.</param>
    /// <returns>Source and dependency findings that did not discard the document.</returns>
    public string[] ReopenSession(SessionDocument document)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        SessionCodec.Validate(document);
        _cancellationToken.ThrowIfCancellationRequested();
        if (_sourceEntries.Count != 0 || Session.Submissions != 0)
        {
            throw new ReplException("session open requires a fresh execution host");
        }

        _document = document;
        _recordedRuntime = document.Runtime;
        _references.AddRange(document.References);
        foreach (var asset in document.Assets)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _assets.Add(asset.Hash, asset);
        }

        Session.DeferActivation = true;
        var problems = new List<string>();
        PrepareReferenceImages(document, problems);
        foreach (var entry in document.Entries)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RestoreTrackedEntry(entry);
            }
            catch (Exception exception) when (exception is ReplException or IOException or ArgumentException
                or TypeLoadException or BadImageFormatException)
            {
                problems.Add($"cell {entry.Number}: {exception.Message}");
            }
        }

        _sourceEntries.AddRange(document.Entries);
        _sourceCells.AddRange(document.Cells);
        Transcript.Clear();
        return [.. problems.Distinct(StringComparer.Ordinal)];
    }

    private HandleResult RecordInput(string line, AnalysisLocation? location)
    {
        ArgumentNullException.ThrowIfNull(line);
        var comment = Session.InBlockComment;
        var kind = CilLexer.Classify(line, ref comment, out var text);
        var normalized = new NormalizedLine(line, text, kind, Session.InBlockComment);
        var operation = ReplLineDispatcher.Classify(normalized, Session.OpenMethod is not null,
            Session.State.HasPendingLabels, Session.State.OpenBlockDepth > 0);
        var number = CellNumber;
        var inputDeclarations = PendingInputDeclarations;
        var editing = _editBlock is not null;
        var runs = !editing && (operation.Kind == ReplLineKind.RetRuns || operation.Command == ".run"
            || (operation.Kind == ReplLineKind.Blank && !Session.State.IsEmpty && Session.OpenDepth == 0));
        var output = new List<TranscriptLine>();
        void Collect(TranscriptLine added)
        {
            if (added.Kind is LineKind.Output or LineKind.Result or LineKind.Error)
            {
                output.Add(added);
            }
        }

        Transcript.LineAdded += Collect;
        HandleResult result;
        try
        {
            result = HandleCore(line, location);
        }
        finally
        {
            Transcript.LineAdded -= Collect;
        }

        if (result.SessionAction is not null)
        {
            return result;
        }

        SessionEntryKind? entryKind = editing && text is not (".clear" or ".reset" or ".undo" or ".u")
            ? SessionEntryKind.EditSource : runs && CellNumber != number ? SessionEntryKind.Run
            : operation.Kind != ReplLineKind.Command ? SessionEntryKind.Source : operation.Command switch
            {
                ".clear" => SessionEntryKind.Clear,
                ".reset" => SessionEntryKind.Reset,
                ".undo" or ".u" => SessionEntryKind.Undo,
                ".edit" => SessionEntryKind.Edit,
                ".load" => SessionEntryKind.Reference,
                _ => null,
            };

        if (!result.Succeeded && CellNumber == number)
        {
            entryKind = SessionEntryKind.Rejected;
        }

        string[] retained = [];
        if (entryKind is { } acceptedKind)
        {
            var entry = new SessionEntry { Number = number, Kind = acceptedKind, Source = [line] };
            if (acceptedKind == SessionEntryKind.Edit)
            {
                var edit = _editBlock is { } block ? Session.Edits.First(edit => edit.Name == block.Name)
                    : Session.Edits.First(edit => edit.Name == result.EditDocument!.Name);
                entry = entry with { Reference = edit.Name, Edit = CaptureEdit(edit) };
            }
            else if (acceptedKind == SessionEntryKind.Reference)
            {
                entry = entry with { Reference = _references[^1].Identity };
            }

            AppendSourceEntry(entry);
            retained = TrackSource(entry, number, editing || acceptedKind == SessionEntryKind.Edit, normalized.InBlockCommentBefore);
        }

        if (result.Succeeded && text.StartsWith(".typeargs", StringComparison.Ordinal))
        {
            _typeArgumentsSource = line;
        }

        if (result.Succeeded && operation.Command == ".reset")
        {
            _typeArgumentsSource = null;
        }

        if (CellNumber != number)
        {
            _sourceCells.Add(new SessionCell
            {
                Number = number,
                Kind = runs ? "cell" : "definition",
                Source = retained,
                Inputs = inputDeclarations,
                State = result.Succeeded ? "succeeded" : "failed",
                Output = [.. output],
            });
        }

        return result;
    }

    private void AppendSourceEntry(SessionEntry entry)
    {
        if (entry.Kind is SessionEntryKind.Source or SessionEntryKind.EditSource && _sourceEntries.Count != 0
            && _groupedEntry is { } previous && ReferenceEquals(_sourceEntries[^1], previous) && previous.Kind == entry.Kind
            && previous.Number == entry.Number)
        {
            _groupedSource.AddRange(entry.Source);
            return;
        }

        FlushSourceEntry();
        _sourceEntries.Add(entry);
        _groupedEntry = entry.Kind is SessionEntryKind.Source or SessionEntryKind.EditSource ? entry : null;
        _groupedEntryIndex = _sourceEntries.Count - 1;
        _groupedSource.Clear();
        if (_groupedEntry is not null)
        {
            _groupedSource.AddRange(entry.Source);
        }
    }

    private void FlushSourceEntry()
    {
        if (_groupedEntry is not { } entry || entry.Source.Length == _groupedSource.Count)
        {
            return;
        }

        _groupedEntry = entry with { Source = [.. _groupedSource] };
        _sourceEntries[_groupedEntryIndex] = _groupedEntry;
    }

    private void RestoreEntry(SessionEntry entry)
    {
        if (entry.Kind is not (SessionEntryKind.Source or SessionEntryKind.EditSource))
        {
            RestoreEntryComments(entry);
        }

        switch (entry.Kind)
        {
            case SessionEntryKind.Source:
                foreach (var line in entry.Source)
                {
                    var normalized = Session.Normalize(line);
                    if (normalized.Kind is not (SourceLineKind.Blank or SourceLineKind.Comment))
                    {
                        Session.AddLine(normalized);
                        if (normalized.Text.StartsWith(".typeargs", StringComparison.Ordinal))
                        {
                            _typeArgumentsSource = line;
                        }
                    }
                }

                break;
            case SessionEntryKind.Run:
                Session.RestoreRunBoundary();
                break;
            case SessionEntryKind.Clear:
                _ = Command(".clear", "");
                break;
            case SessionEntryKind.Reset:
                _editBlock = null;
                Session.Reset();
                _typeArgumentsSource = null;
                break;
            case SessionEntryKind.Undo:
                _ = Command(".undo", "");
                break;
            case SessionEntryKind.Rollback:
                if (entry.Mark is { } mark)
                {
                    _editBlock = null;
                    _ = Session.Rollback(mark with { Generation = Session.Generation });
                }

                break;
            case SessionEntryKind.Edit:
                if (entry.Edit is not { } snapshot)
                {
                    throw new ReplException("invalid edit source transition");
                }

                var existing = Session.Edits.FirstOrDefault(edit => edit.Name == snapshot.Name);
                var restored = existing ?? RestoreEditSnapshot(snapshot);
                restored.Fingerprint = snapshot.Fingerprint;
                if (snapshot.OpensBlock)
                {
                    _editBlock = new OpenEditBlock(restored.Name);
                    Session.EditInputChanged();
                }

                break;
            case SessionEntryKind.EditSource:
                if (_editBlock is null)
                {
                    throw new ReplException("the original for this edit is unavailable");
                }

                foreach (var line in entry.Source)
                {
                    _ = ContinueEdit(Session.Normalize(line));
                }

                break;
            case SessionEntryKind.Reference:
                RestoreReference(_references.Single(reference => reference.Identity == entry.Reference));
                break;
            case SessionEntryKind.Rejected:
                break;
            default:
                throw new ReplException("unsupported source transition");
        }
    }

    private void RestoreEntryComments(SessionEntry entry)
    {
        foreach (var line in entry.Source)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var normalized = Session.Normalize(line);
            if (entry.Kind == SessionEntryKind.Rejected)
            {
                Session.Forget(normalized);
            }
        }
    }

    private void CaptureAssemblyReference(string request, Assembly assembly)
    {
        request = UnquotePath(request);
        var assets = new List<SessionReferenceAsset>();
        if (Session.Resolver.TryGetImage(assembly, out var image))
        {
            var hash = SessionCodec.Hash(image);
            _assets.TryAdd(hash, new SessionAsset { Hash = hash, Image = image });
            assets.Add(new SessionReferenceAsset
            {
                Name = assembly.FullName!,
                Hash = hash,
                Path = File.Exists(request) ? Path.GetFullPath(request) : null,
                Mvid = assembly.ManifestModule.ModuleVersionId.ToString(),
            });
        }

        var reference = new SessionReference { Request = request, Version = assembly.GetName().Version?.ToString(), Assets = [.. assets] };
        _references.Add(reference);
    }

    private void RestoreReference(SessionReference reference) => RestoreReference(reference, new HashSet<string>(StringComparer.Ordinal));

    private void RestoreReference(SessionReference reference, HashSet<string> visited)
    {
        if (!visited.Add(reference.Identity))
        {
            return;
        }

        if (reference.Frameworks.FirstOrDefault(name => name != "Microsoft.NETCore.App") is { } framework)
        {
            throw new ReplException($"reference '{reference.Request}' requires shared framework {framework}; "
                + "this host runs on Microsoft.NETCore.App");
        }

        foreach (var dependency in reference.Dependencies)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            RestoreReference(_references.Single(candidate => candidate.Identity == dependency), visited);
        }

        if (reference.Assets.Length == 0)
        {
            if (reference.Origin == "assembly" && !reference.Request.Contains(Path.DirectorySeparatorChar)
                && !reference.Request.Contains(Path.AltDirectorySeparatorChar))
            {
                try
                {
                    Session.Resolver.Load(reference.Request);
                }
                catch (ReplException exception)
                {
                    throw new ReplException(exception.Message + "; " + DependencyRecoveryHint(), exception);
                }

                Session.AdvanceGeneration();
                return;
            }

            if (reference.Origin == "package")
            {
                return;
            }

            throw new ReplException($"reference '{reference.Request}' has no verified assets; " + DependencyRecoveryHint());
        }

        if (reference.Assets.All(asset => asset.Kind == "reference"))
        {
            throw new ReplException($"reference '{reference.Request}' contains only reference assemblies; "
                + "an implementation assembly is required for execution");
        }

        foreach (var native in reference.Assets.Where(asset => asset.Kind == "native"))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (native.Path is not { } path || !File.Exists(path))
            {
                throw new ReplException($"native dependency '{native.Name}' is unavailable on this runtime; "
                    + (Options.SupportsDependencyRestore ? DependencyRecoveryHint() : "run this experiment in terminal ilrepl"));
            }

            Session.Resolver.RegisterNative(Path.GetFileName(native.Name), path);
        }

        foreach (var asset in reference.Assets.Where(asset => asset.Kind is "managed" or "satellite"))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            byte[] image;
            if (_assets.TryGetValue(asset.Hash, out var embedded))
            {
                image = embedded.Image;
            }
            else if (asset.Path is { } path && File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                if (stream.Length > SessionCodec.FileLimit)
                {
                    throw new ReplException($"reference '{reference.Request}' exceeds the 64 MiB file limit");
                }

                image = new byte[checked((int)stream.Length)];
                stream.ReadExactly(image);
                if (SessionCodec.Hash(image) != asset.Hash)
                {
                    throw new ReplException($"reference '{reference.Request}' changed; " + DependencyRecoveryHint(changed: true));
                }
            }
            else
            {
                throw new ReplException($"reference '{reference.Request}' is missing; " + DependencyRecoveryHint());
            }

            Session.Resolver.LoadImage(image, reference.Origin == "assembly" ? asset.Path : null);
        }

        Session.AdvanceGeneration();
    }
}
