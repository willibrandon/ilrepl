using System.Diagnostics;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Validates, compiles, and executes a terminal session while retaining its declarations and accepted source.
/// </summary>
/// <remarks>
/// One REPL session: the resolver, the declarations and methods that persist across cells, and
/// the cell currently being written. Lines are validated as they arrive; <see cref="Run"/>
/// compiles and executes the cell and clears it. While a <c>.method</c> block is open, lines go
/// to the method instead, and closing it compiles the method into an assembly of its own, binds
/// it into its trampoline, and completes the submission. The session's records are read and
/// written only by the thread that drives it; code a cell started on other threads reaches
/// trampolines and types, never these records.
/// </remarks>
public sealed partial class Session
{
    private readonly List<string> _declarationLines = [];
    private readonly List<string> _bodyLines = [];
    private readonly List<string> _typeParameterNames = [];
    private readonly List<SessionMethod> _methods = [];
    private readonly WeakReference<MethodSignature[]?> _methodSignatures = new(null);
    private CellState _cell;
    private OpenMethodBlock? _open;
    private long _completionRevision;

    /// <summary>
    /// Initializes a session with a fresh resolver.
    /// </summary>
    public Session() : this(new TypeResolver())
    {
    }

    /// <summary>
    /// Initializes a session with a shared resolver.
    /// </summary>
    /// <param name="resolver">The type resolver.</param>
    public Session(TypeResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        Resolver = resolver;
        _cell = new CellState(resolver, GenericContext.Empty);
    }

    /// <summary>
    /// The type resolver.
    /// </summary>
    public TypeResolver Resolver { get; }

    /// <summary>
    /// The validated body currently being written, used by the stack echo, listings and status bar.
    /// </summary>
    public CellState State => _openMember?.State ?? _open?.State ?? _cell;

    /// <summary>
    /// The validated state of the cell itself, whether or not a method block is open.
    /// </summary>
    public CellState Cell => _cell;

    /// <summary>
    /// An inspection context over committed types that cannot declare members or mutate an open family.
    /// </summary>
    public ParseContext InspectionContext
    {
        get
        {
            var types = _typeTable.Clone();
            types.Forward = null;
            return new ParseContext([], [], State.Context.Generics, Resolver, Signatures(), types) { Inspecting = true };
        }
    }

    /// <summary>
    /// The signature of the method block being typed, or null when lines go to the cell.
    /// </summary>
    public MethodSignature? OpenMethod => _openMember?.Signature ?? _open?.Signature;

    /// <summary>
    /// The methods defined with <c>.method</c>, in definition order. A redefinition keeps its place.
    /// </summary>
    public IReadOnlyList<SessionMethod> Methods => _methods;

    /// <summary>
    /// The declaration lines (<c>.locals</c>, <c>.args</c>, <c>.typeparams</c>, <c>.vararg</c>) that persist across cells.
    /// </summary>
    public IReadOnlyList<string> DeclarationLines => _declarationLines;

    /// <summary>
    /// The body lines of the current cell.
    /// </summary>
    public IReadOnlyList<string> BodyLines => _bodyLines;

    /// <summary>
    /// The names declared with <c>.typeparams</c>.
    /// </summary>
    public IReadOnlyList<string> TypeParameterNames => _typeParameterNames;

    /// <summary>
    /// The types bound with <c>.typeargs</c> for the next run, or null.
    /// </summary>
    public IReadOnlyList<Type>? TypeArguments { get; private set; }

    /// <summary>
    /// How many cells have run.
    /// </summary>
    public int CellsRun { get; private set; }

    /// <summary>
    /// The number of completed runs and committed declaration blocks, excluding rejected or abandoned blocks.
    /// </summary>
    public int Submissions { get; private set; }

    /// <summary>
    /// The generation of irreversible session transitions, used to reject obsolete rollback marks.
    /// </summary>
    public long Generation { get; private set; }

    /// <summary>
    /// The revision of every change that can affect completion, including lexical state and rollback.
    /// </summary>
    public long CompletionRevision
    {
        get => _completionRevision;
        private set
        {
            _completionRevision = value;
            CompletionChanged?.Invoke();
        }
    }

    /// <summary>
    /// Invalidates cached completion state immediately after any semantic mutation under the session gate.
    /// </summary>
    internal event Action? CompletionChanged;

    /// <summary>
    /// Whether a <c>/*</c> comment is open at the end of the last line normalized.
    /// </summary>
    public bool InBlockComment { get; private set; }

    /// <summary>
    /// The combined number of currently open declaration, accessor and exception-region braces.
    /// </summary>
    public int OpenDepth
    {
        get
        {
            var depth = State.OpenBlockDepth;
            for (var type = _openType; type is not null; type = type.Enclosing)
            {
                if (type.BraceSeen)
                {
                    depth++;
                }
            }

            if (_openMember is { State.BraceSeen: true })
            {
                depth++;
            }

            if (_openAccessor is { BraceSeen: true })
            {
                depth++;
            }

            if (_open is { State.BraceSeen: true })
            {
                depth++;
            }

            return depth;
        }
    }

    /// <summary>
    /// Strips comments, carries multiline comment state and classifies the remaining input.
    /// </summary>
    /// <param name="raw">The line as typed.</param>
    /// <returns>The line without its comments.</returns>
    public NormalizedLine Normalize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var before = InBlockComment;
        var state = before;
        var kind = CilLexer.Classify(raw, ref state, out var text);
        InBlockComment = state;
        if (state != before)
        {
            CompletionRevision++;
        }

        return new NormalizedLine(raw, text, kind, before);
    }

    /// <summary>
    /// Discards comments opened by refused code while preserving the closure of a preceding comment.
    /// </summary>
    /// <param name="line">The refused line.</param>
    public void Forget(NormalizedLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var retainedComment = line.Kind != SourceLineKind.Text && line.InBlockCommentBefore;
        if (InBlockComment != retainedComment)
        {
            CompletionRevision++;
        }

        InBlockComment = retainedComment;
    }

    /// <summary>
    /// Adds a line as typed. Its comments are removed first, see <see cref="Normalize"/>.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>What the line was.</returns>
    /// <exception cref="ReplException">The line is invalid.</exception>
    public LineResult AddLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var normalized = Normalize(line);
        try
        {
            return AddLine(normalized);
        }
        catch (ReplException)
        {
            Forget(normalized);
            throw;
        }
    }

    /// <summary>
    /// Adds a declaration or instruction to the current body, ignoring blank and comment-only lines.
    /// </summary>
    /// <param name="line">The line, its comments already removed.</param>
    /// <returns>What the line was.</returns>
    /// <exception cref="ReplException">The line is invalid.</exception>
    public LineResult AddLine(NormalizedLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        using var references = Resolver.EnterContext();
        if (line.Kind != SourceLineKind.Text)
        {
            return new LineResult(LineOutcome.Empty, null, null);
        }

        var accepted = AddTextLine(line);
        if (accepted.Outcome != LineOutcome.Empty)
        {
            CompletionRevision++;
        }

        return accepted;
    }

    private LineResult AddTextLine(NormalizedLine line)
    {
        var text = line.Text;
        if (_openType is not null)
        {
            var family = _openType.Outermost;
            var position = family.Lines.Count;
            var acceptedType = AddTypeLine(line);
            if (line.Location is { } location && family.Lines.Count > position)
            {
                _familySourceLocations[position] = location;
            }

            return acceptedType;
        }

        if (_open is not null)
        {
            return AddMethodLine(line);
        }

        if (IsClassDirective(text))
        {
            return OpenTypeBlock(text[".class".Length..], text);
        }

        if (text.StartsWith(".method", StringComparison.Ordinal)
            && (text.Length == ".method".Length || !char.IsLetter(text[".method".Length])))
        {
            return OpenBlock(text[".method".Length..], text);
        }

        if (text.StartsWith(".typeparams", StringComparison.Ordinal))
        {
            DeclareTypeParameters(text[".typeparams".Length..]);
            _declarationLines.Add(text);
            return new LineResult(LineOutcome.TypeParameters, null,
                "type parameters: " + string.Join(", ", _typeParameterNames.Select(n => "!!" + n)));
        }

        if (text.StartsWith(".typeargs", StringComparison.Ordinal))
        {
            BindTypeArguments(text[".typeargs".Length..]);
            return new LineResult(LineOutcome.TypeArguments, null,
                "type arguments: " + string.Join(", ", TypeArguments!.Select(TypeNameFormatter.Pretty)));
        }

        var previousEntries = _cell.Entries.Count;
        var result = _cell.Apply(line);
        switch (result.Outcome)
        {
            case LineOutcome.Empty:
                if (_cell.Entries.Count != previousEntries)
                {
                    _bodyLines.Add(text);
                }

                break;
            case LineOutcome.Locals:
            case LineOutcome.Arguments:
            case LineOutcome.VarArg:
                _declarationLines.Add(text);
                break;
            default:
                _bodyLines.Add(text);
                break;
        }

        return result;
    }

    /// <summary>
    /// Captures the session boundary from which an uncommitted submission can be withdrawn.
    /// </summary>
    /// <returns>The mark.</returns>
    public SessionMark Mark() =>
        new(Generation, _bodyLines.Count, _declarationLines.Count, _open?.BodyLines.Count, _openType?.Outermost.Lines.Count, InBlockComment,
        BraceSeen: _open?.State.BraceSeen ?? true);

    /// <summary>
    /// Withdraws uncommitted input since a mark, refusing if intervening irreversible changes prevent recovery.
    /// </summary>
    /// <param name="mark">The mark to return to.</param>
    /// <returns>True when the session is back at the mark.</returns>
    public bool Rollback(SessionMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        if (mark.Generation != Generation)
        {
            return false;
        }

        if (mark.OpenTypeLines is int typeLines)
        {
            if (_openType is { } family && family.Outermost.Lines.Count > typeLines)
            {
                var outermost = family.Outermost;
                var header = outermost.HeaderLine;
                var kept = outermost.Lines.Take(typeLines).ToList();
                _openType = null;
                _openMember = null;
                _openAccessor = null;
                ReplayFamily(header, kept);
            }
        }
        else if (_openType is not null)
        {
            AbandonTypeFamily();
        }

        if (mark.OpenMethodLines is int methodLines)
        {
            // The brace is not a body line, so a mark taken while the header still waited for
            // it is told apart by the flag: the body goes back to the mark's lines and the
            // method to waiting for its brace when that is where it stood.
            if (_open is { } open && (open.BodyLines.Count > methodLines || open.State.BraceSeen != mark.BraceSeen))
            {
                if (open.BodyLines.Count > methodLines)
                {
                    open.BodyLines.RemoveRange(methodLines, open.BodyLines.Count - methodLines);
                }

                open.State = ReplayOpenBody(open, mark.BraceSeen);
            }
        }
        else if (_open is not null)
        {
            _open = null;
        }

        if (_bodyLines.Count > mark.BodyLines || _declarationLines.Count > mark.DeclarationLines)
        {
            if (_bodyLines.Count > mark.BodyLines)
            {
                _bodyLines.RemoveRange(mark.BodyLines, _bodyLines.Count - mark.BodyLines);
            }

            if (_declarationLines.Count > mark.DeclarationLines)
            {
                _declarationLines.RemoveRange(mark.DeclarationLines, _declarationLines.Count - mark.DeclarationLines);
            }

            Rebuild();
        }

        InBlockComment = mark.InBlockComment;
        CompletionRevision++;
        return true;
    }

    /// <summary>
    /// Records a change forgetting lines cannot undo, such as a loaded assembly.
    /// </summary>
    internal void AdvanceGeneration()
    {
        Generation++;
        CompletionRevision++;
    }

    /// <summary>
    /// Removes the last uncommitted line, abandoning an open declaration when its header is removed.
    /// </summary>
    /// <returns>True when a line was removed.</returns>
    public bool Undo()
    {
        if (_openType is not null)
        {
            var undone = UndoTypeLine();
            if (undone)
            {
                Generation++;
                CompletionRevision++;
            }

            return undone;
        }

        if (_open is not null)
        {
            if (_open.BodyLines.Count == 0)
            {
                _open = null;
                Generation++;
                CompletionRevision++;
                return true;
            }

            _open.BodyLines.RemoveAt(_open.BodyLines.Count - 1);
            _open.State = ReplayOpenBody(_open);
            Generation++;
            CompletionRevision++;
            return true;
        }

        if (_bodyLines.Count == 0)
        {
            return false;
        }

        _bodyLines.RemoveAt(_bodyLines.Count - 1);
        Rebuild();
        Generation++;
        CompletionRevision++;
        return true;
    }

    /// <summary>
    /// Drops the open method block without committing it. The cell and the methods are untouched.
    /// </summary>
    /// <returns>True when a block was open.</returns>
    public bool AbandonMethod()
    {
        if (_openMember is { } member)
        {
            // The member's header and body are the last lines of the family; the family is
            // replayed without them, which also forgets the member's builder and signature.
            var outermost = _openType!.Outermost;
            var header = outermost.HeaderLine;
            var kept = outermost.Lines.Take(Math.Max(0, outermost.Lines.Count - member.BodyLines.Count - 1)).ToList();
            _openMember = null;
            _openAccessor = null;
            _openType = null;
            ReplayFamily(header, kept);
            Generation++;
            CompletionRevision++;
            return true;
        }

        if (_open is null)
        {
            return false;
        }

        _open = null;
        Generation++;
        CompletionRevision++;
        return true;
    }

    /// <summary>
    /// Abandons the open type family while preserving accepted definitions and the cell.
    /// </summary>
    /// <returns>True when a family was open.</returns>
    public bool AbandonType()
    {
        if (_openType is null)
        {
            return false;
        }

        AbandonTypeFamily();
        Generation++;
        CompletionRevision++;
        return true;
    }

    /// <summary>
    /// Drops the cell body and keeps the declarations and the methods.
    /// </summary>
    public void ClearCell()
    {
        _bodyLines.Clear();
        Rebuild();
        Generation++;
        CompletionRevision++;
    }

    /// <summary>
    /// Drops the cell body, the declarations, the methods, any open method block, and any bound type arguments.
    /// </summary>
    public void Reset()
    {
        _bodyLines.Clear();
        _declarationLines.Clear();
        _typeParameterNames.Clear();
        foreach (var method in _methods)
        {
            // The session drops its references; anything a user retained keeps its version alive.
            SessionAssemblies.Release(method.Version.Definition);
            SessionAssemblies.Release(method.Trampoline.Definition);
        }

        _methods.Clear();
        InvalidateSignatures();
        foreach (var edit in _edits)
        {
            if (edit.Baseline.Definition is { } baseline)
            {
                SessionAssemblies.Release(baseline);
            }

            if (edit.Current?.Definition is { } current)
            {
                SessionAssemblies.Release(current);
            }
        }

        _edits.Clear();
        _open = null;
        foreach (var type in _types)
        {
            if (type.Definition is { } definition)
            {
                SessionAssemblies.Release(definition);
            }
        }

        _types.Clear();
        _typeTable = new TypeTable();
        AbandonTypeFamily();
        TypeArguments = null;
        InBlockComment = false;
        Rebuild();
        Generation++;
        CompletionRevision++;
    }

    /// <summary>
    /// Compiles and runs the cell, then clears the body. Declarations and methods stay.
    /// </summary>
    /// <returns>The result.</returns>
    /// <exception cref="ReplException">The cell is incomplete, a method block is open, or the runtime rejected it.</exception>
    /// <exception cref="CellException">The cell threw.</exception>
    public CellResult Run() => RunCancellable(null, null, CancellationToken.None);

    /// <summary>
    /// Compiles cooperatively and marks the boundary before invoking arbitrary user IL.
    /// </summary>
    /// <param name="cancellationToken">Cancels preparation before invocation.</param>
    /// <param name="beforeInvoke">Publishes the transition into user execution.</param>
    /// <param name="output">Streams captured user output, when supplied.</param>
    /// <returns>The value and captured output from the cell.</returns>
    internal CellResult RunCancellable(Action? beforeInvoke, Action<string, bool>? output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var capture = new ConsoleCapture(output);
        var isVoid = _cell.Stack.Count == 0 && !_cell.ReturnsValue;
        var compiled = CellCompiler.CompileForExecution(this, cancellationToken);
        var stopwatch = new Stopwatch();
        object? value;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var typeArguments = TypeArguments;
            beforeInvoke?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            RecordActivation(compiled);
            Activate();
            var invocation = compiled with
            {
                ArgumentValues = compiled.InvocationArguments.Select(argument => argument.ExecutionValue())
                    .ToArray(),
            };

            ClearCell();
            CellsRun++;
            Submissions++;
            stopwatch.Start();
            value = invocation.Invoke(typeArguments);
        }
        catch (CellException exception)
        {
            exception.StandardOutput = capture.StandardOutput;
            exception.StandardError = capture.StandardError;
            throw;
        }
        catch (InvalidProgramException ex)
        {
            throw new ReplException("the JIT rejected the cell: " + ex.Message, ex);
        }
        finally
        {
            stopwatch.Stop();
            compiled.Release();
        }

        return new CellResult(value, isVoid, stopwatch.Elapsed, capture.StandardOutput, capture.StandardError);
    }

    /// <summary>
    /// Writes the session definitions and current cell to an assembly containing IlRepl.Cell.Run.
    /// </summary>
    /// <param name="path">The output path.</param>
    /// <exception cref="ReplException">The cell is incomplete, a method block is open, or the runtime rejected it.</exception>
    public void Save(string path) => AssemblyExporter.Save(this, path);

    /// <summary>
    /// Exports the current cell and declarations with cancellation before atomically replacing the destination.
    /// </summary>
    /// <param name="path">The destination assembly filename.</param>
    /// <param name="cancellationToken">Cancels export before publication.</param>
    internal void SaveCancellable(string path, CancellationToken cancellationToken)
        => AssemblyExporter.SaveCancellable(this, path, cancellationToken);

    /// <summary>
    /// Renders the session methods and the current cell as ILAsm source.
    /// </summary>
    /// <returns>The ILAsm text.</returns>
    public string ToIlAsm() => IlAsmRenderer.Render(this);

    private LineResult OpenBlock(string spec, string line)
    {
        var signatures = Signatures();
        var headerContext = new ParseContext([], [], GenericContext.Empty, Resolver, signatures, _typeTable);
        var signature = MethodHeaderParser.Parse(spec, headerContext, out var braceOpen);
        if (_edits.Any(edit => edit.Name == signature.Name))
        {
            throw new ReplException($"'{signature.Name}' already belongs to an edit; choose another method name");
        }

        var index = _methods.FindIndex(method => method.Signature.Name == signature.Name);
        var replacing = index < 0 ? null : _methods[index];
        var table = new MethodSignature[signatures.Length + (index < 0 ? 1 : 0)];
        signatures.CopyTo(table, 0);
        table[index < 0 ? signatures.Length : index] = signature;

        if (replacing is not null && !_rebuilding)
        {
            // Dependents bind to the signature, which is known now. Checking here, rather than at
            // the closing brace, means a refused redefinition costs nothing to recover from. A
            // dependent keeps the state it was accepted with; it is never parsed again, and a
            // same-signature replacement reaches it through the trampoline it already calls.
            foreach (var other in _methods)
            {
                if (!ReferenceEquals(other, replacing))
                {
                    RequireCompatibleReferences(other.State, "method " + other.Signature.Name, signature,
                        "(the previous definition stays)");
                }
            }

            RequireCompatibleTypeReferences(signature);
            RequireCompatibleReferences(_cell, "the cell body", signature, "(.clear the cell first, or keep the signature)");
            try
            {
                BuildCell(table);
            }
            catch (ReplException ex)
            {
                throw new ReplException(
                    $"cannot redefine {signature.Name} as {signature.Describe()}: the cell body would no longer compile: {ex.Message}  " +
                    $"(.clear the cell first, or keep the signature)", ex);
            }
        }

        _open = new OpenMethodBlock
        {
            HeaderLine = line,
            Signature = signature,
            Replacing = replacing,
            Signatures = table,
            State = new CellState(Resolver, GenericContext.Empty, table, signature, braceOpen, _typeTable, null),
        };

        return new LineResult(LineOutcome.MethodStart, null, "method " + signature.DescribeWithNames());
    }

    private LineResult AddMethodLine(NormalizedLine line)
    {
        var open = _open!;
        var result = open.State.Apply(line);
        if (result.Outcome == LineOutcome.MethodEnd)
        {
            return CloseBlock();
        }

        if (result.Outcome != LineOutcome.Empty)
        {
            open.BodyLines.Add(line.Text);
        }

        return result;
    }

    private LineResult CloseBlock()
    {
        var open = _open!;
        var name = open.Signature.Name;
        var replacing = open.Replacing;

        if (replacing is not null && !_rebuilding)
        {
            foreach (var existing in _methods)
            {
                if (!ReferenceEquals(existing, replacing))
                {
                    RequireCompatibleReferences(existing.State, "method " + existing.Signature.Name, open.Signature,
                        "(the previous definition stays)");
                }
            }

            RequireCompatibleTypeReferences(open.Signature);
            RequireCompatibleReferences(_cell, "the cell body", open.Signature, "(.clear the cell first, or keep the signature)");
        }

        if (_rebuilding)
        {
            // The version is compiled with the rest of the group once every member has replayed.
            _pendingMethods.Add(new PendingMethod(open.Signature, open.HeaderLine, [.. open.BodyLines], open.State, replacing));
            _open = null;
            return new LineResult(LineOutcome.MethodEnd, null, $"end of method {name}");
        }

        CellState cell;
        try
        {
            cell = BuildCell(open.Signatures);
        }
        catch (ReplException ex)
        {
            throw new ReplException(
                $"cannot {(replacing is null ? "define" : "replace")} {name}: the cell body would no longer compile: {ex.Message}  " +
                $"(.clear the cell first)", ex);
        }

        // Phase A: everything that can fail. A same-signature replacement keeps its trampoline,
        // so every caller already bound to it sees the new body; a new signature is a new
        // identity, and nothing references it yet.
        var sameSignature = replacing is not null && !_rebuilding && SameSignature(replacing.Signature, open.Signature);
        var trampoline = sameSignature ? replacing!.Trampoline : MethodTrampoline.Create(open.Signature);
        Dictionary<string, MethodTrampoline>? trampolines = null;
        var map = new EmitMap(signature =>
        {
            // Emission runs synchronously before the session records change. Bodies with no
            // session-method references do not need a copy of every existing trampoline.
            if (trampolines is null)
            {
                trampolines = _methods.Where(method => !ReferenceEquals(method, replacing))
                    .ToDictionary(method => method.Signature.Name, method => method.Trampoline, StringComparer.Ordinal);
                trampolines[name] = trampoline;
            }

            return trampolines.TryGetValue(signature.Name, out var target)
                ? target.Method
                : throw new ReplException($"no method '{signature.Name}' is bound in the session");
        });

        CompiledMethodVersion? version = null;
        Delegate? implementation = null;
        try
        {
            if (sameSignature && !DeferActivation)
            {
                trampoline.PrepareBinding();
            }

            version = DefinitionCompiler.CompileMappedMethod(open.Signature, open.State, trampoline, map,
                !DeferActivation && MethodPreparation.IsSupported);
            if (!DeferActivation)
            {
                implementation = version.Implementation;
            }
        }
        catch
        {
            if (version is not null)
            {
                SessionAssemblies.Release(version.Definition);
            }

            if (!sameSignature)
            {
                SessionAssemblies.Release(trampoline.Definition);
            }

            throw;
        }

        if (!sameSignature && !DeferActivation)
        {
            trampoline.Bind(implementation!);
        }

        // Phase B: one reference store and the record swap. Neither can fail.
        if (sameSignature && !DeferActivation)
        {
            trampoline.Bind(implementation!);
        }

        var committed = new SessionMethod(open.Signature, open.HeaderLine, [.. open.BodyLines], open.State, trampoline,
            version) { Order = sameSignature ? replacing!.Order : Submissions };
        var index = replacing is null ? -1 : _methods.IndexOf(replacing);
        if (index < 0)
        {
            _methods.Add(committed);
        }
        else
        {
            _methods[index] = committed;
        }

        InvalidateSignatures();
        _cell = cell;
        _open = null;
        Submissions++;
        Generation++;
        CompletionRevision++;

        if (replacing is not null && !_rebuilding)
        {
            SessionAssemblies.Release(replacing.Version.Definition);
            if (!sameSignature)
            {
                SessionAssemblies.Release(replacing.Trampoline.Definition);
            }
        }

        return new LineResult(LineOutcome.MethodEnd, null, replacing is null ? $"end of method {name}" : $"replaced method {name}");
    }

    private void DeclareTypeParameters(string spec)
    {
        var names = CellGenericBinding.Parameters(spec, _typeParameterNames, _cell.IsEmpty);

        _typeParameterNames.AddRange(names);
        TypeArguments = null;
        Rebuild();
        Generation++;
        CompletionRevision++;
    }

    private void BindTypeArguments(string spec)
    {
        var context = new ParseContext([], [], GenericContext.Empty, Resolver, Signatures(), _typeTable);
        var scope = new RuntimeBindingScope(context);
        var symbols = CellGenericBinding.Arguments(spec, _typeParameterNames, scope);
        var types = new RuntimeBindingAdapter(scope).ToTypes(symbols);

        TypeArguments = types;
        Generation++;
        CompletionRevision++;
    }

    private void RequireCompatibleTypeReferences(MethodSignature replacement)
    {
        foreach (var family in _types)
        {
            foreach (var declaration in family.Declaration.Family)
            {
                foreach (var method in declaration.Methods)
                {
                    if (method.Body is { } body)
                    {
                        RequireCompatibleReferences(body, $"{declaration.KindWord} {declaration.FullName}", replacement,
                            "(the previous definition stays)");
                    }
                }
            }
        }
    }

    private static void RequireCompatibleReferences(CellState state, string what, MethodSignature replacement, string hint)
    {
        foreach (var entry in state.Entries)
        {
            if (entry.Instruction?.Operand is ResolvedMethod { Definition: { } bound } && bound.Name == replacement.Name
                && !SameSignature(bound, replacement))
            {
                throw new ReplException(
                    $"cannot redefine {replacement.Name} as {replacement.Describe()}: {what} references {bound.Describe()}  {hint}");
            }
        }
    }

    private static bool SameSignature(MethodSignature a, MethodSignature b) => SignatureIdentity.Same(a, b);

    private MethodSignature[] Signatures()
    {
        var unchanged = _methodSignatures.TryGetTarget(out var signatures) && signatures.Length == _methods.Count;
        if (!unchanged)
        {
            signatures = _methods.Select(method => method.Signature).ToArray();
            // Contexts own their binding tables; memoization must not extend the lifetime of retired definition types.
            _methodSignatures.SetTarget(signatures);
        }

        return signatures!;
    }

    private void InvalidateSignatures() => _methodSignatures.SetTarget(null);

    private void Rebuild() => _cell = BuildCell(Signatures());

    private CellState BuildCell(IReadOnlyList<MethodSignature> table) => BuildCell(table, _typeTable);

    private CellState BuildCell(IReadOnlyList<MethodSignature> table, TypeTable types)
    {
        var generics = new GenericContext([], PrototypeGenerics.Create(_typeParameterNames));
        var state = new CellState(Resolver, generics, table, null, false, types, null);
        var declarationLocations = CaptureLocations(_declarationLines, _cell.Entries);
        for (var index = 0; index < _declarationLines.Count; index++)
        {
            state.Apply(NormalizedLine.FromText(_declarationLines[index]) with { Location = declarationLocations[index] });
        }

        var bodyLocations = CaptureLocations(_bodyLines, _cell.Entries);
        for (var index = 0; index < _bodyLines.Count; index++)
        {
            state.Apply(NormalizedLine.FromText(_bodyLines[index]) with { Location = bodyLocations[index] });
        }

        return state;
    }

    private CellState ReplayOpenBody(OpenMethodBlock open, bool? braceSeen = null) => ReplayBody(
        open.Signature, open.BodyLines, open.Signatures, CaptureLocations(open.BodyLines, open.State.Entries),
        braceSeen ?? open.State.BraceSeen);

    private CellState ReplayBody(
        MethodSignature signature,
        List<string> lines,
        IReadOnlyList<MethodSignature> table,
        AnalysisLocation?[] locations,
        bool braceSeen)
    {
        // The opening brace is never stored: a replay starts with it seen unless told otherwise.
        var state = new CellState(Resolver, GenericContext.Empty, table, signature, braceSeen, _typeTable, null);
        for (var index = 0; index < lines.Count; index++)
        {
            state.Apply(NormalizedLine.FromText(lines[index]) with { Location = locations[index] });
        }

        return state;
    }
}
