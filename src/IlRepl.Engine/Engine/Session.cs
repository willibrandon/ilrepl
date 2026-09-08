using System.Diagnostics;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// One REPL session: the resolver, the declarations and methods that persist across cells, and
/// the cell currently being written. Lines are validated as they arrive; <see cref="Run"/>
/// compiles and executes the cell and clears it. While a <c>.method</c> block is open, lines go
/// to the method instead, and closing it compiles the method into an assembly of its own, binds
/// it into its trampoline, and completes the submission. The session's records are read and
/// written only by the thread that drives it; code a cell started on other threads reaches
/// trampolines and types, never these records.
/// </summary>
public sealed partial class Session
{
    private readonly List<string> _declarationLines = [];
    private readonly List<string> _bodyLines = [];
    private readonly List<string> _typeParameterNames = [];
    private readonly List<SessionMethod> _methods = [];
    private CellState _cell;
    private OpenMethodBlock? _open;

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
    /// The validated state of the body being written: the open method while a <c>.method</c>
    /// block is open, otherwise the cell. The stack echo, listings, and the status bar read this.
    /// </summary>
    public CellState State => _openMember?.State ?? _open?.State ?? _cell;

    /// <summary>
    /// The validated state of the cell itself, whether or not a method block is open.
    /// </summary>
    public CellState Cell => _cell;

    /// <summary>
    /// A context for looking a member up without changing anything: the committed type table
    /// alone, with no prototype of a class being written laid over it and no callback that could
    /// declare a member or a nested type ahead of its declaration. A family being redefined
    /// resolves to its accepted generation; a class with no accepted generation is not there at all.
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
    /// How many submissions have completed: a run, or a <c>.method</c> block that closed and
    /// committed. The prompt numbers the next one. A rejected close or an abandoned block does not count.
    /// </summary>
    public int Submissions { get; private set; }

    /// <summary>
    /// Changes whenever something happens that forgetting lines cannot undo: a run, a commit, an
    /// undo, an abandon, a clear, a reset, a loaded assembly, or bound type parameters. A
    /// <see cref="SessionMark"/> from an earlier generation cannot be rolled back to.
    /// </summary>
    public long Generation { get; private set; }

    /// <summary>
    /// Whether a <c>/*</c> comment is open at the end of the last line normalized.
    /// </summary>
    public bool InBlockComment { get; private set; }

    /// <summary>
    /// How many opening braces the session has seen and not yet closed: open protected regions,
    /// the open method, the open member and accessor, and every open type block, together. A
    /// header still waiting for its brace on the next line has not opened one yet.
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
    /// Removes the comments from a line, carrying an open <c>/* */</c> from line to line, and says
    /// what is left: something to parse, a blank line, or a comment and nothing else. This is the
    /// only place a comment is removed; everything after it sees the text.
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
        return new NormalizedLine(raw, text, kind, before);
    }

    /// <summary>
    /// Puts the comment state back to where a line found it, for a line the session refused: a
    /// refused line changes nothing, the <c>/*</c> it may have opened included.
    /// </summary>
    /// <param name="line">The refused line.</param>
    public void Forget(NormalizedLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        InBlockComment = line.InBlockCommentBefore;
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
    /// Adds a line. Declarations are kept across runs; a <c>.method</c> header opens a block that
    /// takes the following lines until <c>}</c>; everything else belongs to the current cell. A
    /// comment or a blank line changes nothing.
    /// </summary>
    /// <param name="line">The line, its comments already removed.</param>
    /// <returns>What the line was.</returns>
    /// <exception cref="ReplException">The line is invalid.</exception>
    public LineResult AddLine(NormalizedLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Kind != SourceLineKind.Text)
        {
            return new LineResult(LineOutcome.Empty, null, null);
        }

        var text = line.Text;
        if (_openType is not null)
        {
            return AddTypeLine(line);
        }

        if (_open is not null)
        {
            return AddMethodLine(line);
        }

        if (IsClassDirective(text))
        {
            return OpenTypeBlock(text[".class".Length..], text);
        }

        if (text.StartsWith(".method", StringComparison.Ordinal) && (text.Length == ".method".Length || !char.IsLetter(text[".method".Length])))
        {
            return OpenBlock(text[".method".Length..], text);
        }

        if (text.StartsWith(".typeparams", StringComparison.Ordinal))
        {
            DeclareTypeParameters(text[".typeparams".Length..]);
            _declarationLines.Add(text);
            return new LineResult(LineOutcome.TypeParameters, null, "type parameters: " + string.Join(", ", _typeParameterNames.Select(n => "!!" + n)));
        }

        if (text.StartsWith(".typeargs", StringComparison.Ordinal))
        {
            BindTypeArguments(text[".typeargs".Length..]);
            return new LineResult(LineOutcome.TypeArguments, null, "type arguments: " + string.Join(", ", TypeArguments!.Select(TypeNameFormatter.Pretty)));
        }

        var result = _cell.Apply(line);
        switch (result.Outcome)
        {
            case LineOutcome.Empty:
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
    /// Where the session stands, taken before a block is sent so the block can be withdrawn with
    /// <see cref="Rollback"/> if a line of it is refused.
    /// </summary>
    /// <returns>The mark.</returns>
    public SessionMark Mark() => new(Generation, _bodyLines.Count, _declarationLines.Count, _open?.BodyLines.Count, _openType?.Outermost.Lines.Count, InBlockComment, BraceSeen: _open?.State.BraceSeen ?? true);

    /// <summary>
    /// Withdraws every line accepted since the mark: a method or class opened since is abandoned,
    /// one that was already open is cut back to the lines it had, and the cell is rebuilt from the
    /// lines it had, so every earlier instruction, region, and declaration stays. Nothing is
    /// re-run. Refused when something ran, committed, or was discarded since the mark, because
    /// forgetting lines cannot undo that.
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
        return true;
    }

    /// <summary>
    /// Records a change forgetting lines cannot undo, such as a loaded assembly.
    /// </summary>
    internal void AdvanceGeneration() => Generation++;

    /// <summary>
    /// Removes the last line: of the open method block, or of the cell body. Removing a method
    /// header abandons the block. Committed methods and declarations are not undone.
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
            }

            return undone;
        }

        if (_open is not null)
        {
            if (_open.BodyLines.Count == 0)
            {
                _open = null;
                Generation++;
                return true;
            }

            _open.BodyLines.RemoveAt(_open.BodyLines.Count - 1);
            _open.State = ReplayOpenBody(_open);
            Generation++;
            return true;
        }

        if (_bodyLines.Count == 0)
        {
            return false;
        }

        _bodyLines.RemoveAt(_bodyLines.Count - 1);
        Rebuild();
        Generation++;
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
            return true;
        }

        if (_open is null)
        {
            return false;
        }

        _open = null;
        Generation++;
        return true;
    }

    /// <summary>
    /// Drops the whole open type family without committing it. The cell, the methods, and the
    /// accepted types are untouched.
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
    }

    /// <summary>
    /// Compiles and runs the cell, then clears the body. Declarations and methods stay.
    /// </summary>
    /// <returns>The result.</returns>
    /// <exception cref="ReplException">The cell is incomplete, a method block is open, or the runtime rejected it.</exception>
    /// <exception cref="CellException">The cell threw.</exception>
    public CellResult Run()
    {
        var isVoid = _cell.Stack.Count == 0 && !_cell.ReturnsValue;
        var compiled = CellCompiler.Compile(this);
        var typeArguments = TypeArguments;
        ClearCell();
        CellsRun++;
        Submissions++;

        using var capture = new ConsoleCapture();
        var stopwatch = Stopwatch.StartNew();
        object? value;
        try
        {
            value = compiled.Invoke(typeArguments);
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
    /// Writes the current cell and the session methods to disk as an assembly with a static
    /// <c>IlRepl.Cell.Run</c> method. The cell is kept.
    /// </summary>
    /// <param name="path">The output path.</param>
    /// <exception cref="ReplException">The cell is incomplete, a method block is open, or the runtime rejected it.</exception>
    public void Save(string path) => AssemblyExporter.Save(this, path);

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
        var replacing = _methods.FirstOrDefault(m => m.Signature.Name == signature.Name);
        var table = new List<MethodSignature>(signatures);
        var index = table.FindIndex(s => s.Name == signature.Name);
        if (index < 0)
        {
            table.Add(signature);
        }
        else
        {
            table[index] = signature;
        }

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
                    RequireCompatibleReferences(other.State, "method " + other.Signature.Name, signature, "(the previous definition stays)");
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
                throw new ReplException($"cannot redefine {signature.Name} as {signature.Describe()}: the cell body would no longer compile: {ex.Message}  (.clear the cell first, or keep the signature)", ex);
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
                    RequireCompatibleReferences(existing.State, "method " + existing.Signature.Name, open.Signature, "(the previous definition stays)");
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
            throw new ReplException($"cannot {(replacing is null ? "define" : "replace")} {name}: the cell body would no longer compile: {ex.Message}  (.clear the cell first)", ex);
        }

        // Phase A: everything that can fail. A same-signature replacement keeps its trampoline,
        // so every caller already bound to it sees the new body; a new signature is a new
        // identity, and nothing references it yet.
        var sameSignature = replacing is not null && !_rebuilding && SameSignature(replacing.Signature, open.Signature);
        var trampoline = sameSignature ? replacing!.Trampoline : MethodTrampoline.Create(open.Signature);
        var trampolines = _methods.Where(m => !ReferenceEquals(m, replacing)).ToDictionary(m => m.Signature.Name, m => m.Trampoline, StringComparer.Ordinal);
        trampolines[name] = trampoline;
        CompiledMethodVersion version;
        try
        {
            version = DefinitionCompiler.CompileMethod(open.Signature, open.State, trampoline, trampolines, MethodPreparation.IsSupported);
        }
        catch
        {
            if (!sameSignature)
            {
                SessionAssemblies.Release(trampoline.Definition);
            }

            throw;
        }

        if (!sameSignature)
        {
            trampoline.Bind(version.Implementation);
        }

        // Phase B: one reference store and the record swap. Neither can fail.
        if (sameSignature)
        {
            trampoline.Bind(version.Implementation);
        }

        var committed = new SessionMethod(open.Signature, open.HeaderLine, [.. open.BodyLines], open.State, trampoline, version) { Order = sameSignature ? replacing!.Order : Submissions };
        var index = replacing is null ? -1 : _methods.IndexOf(replacing);
        if (index < 0)
        {
            _methods.Add(committed);
        }
        else
        {
            _methods[index] = committed;
        }

        _cell = cell;
        _open = null;
        Submissions++;
        Generation++;

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
        if (!_cell.IsEmpty)
        {
            throw new ReplException("declare .typeparams before the first instruction of the cell (or .clear first)");
        }

        var s = spec.Trim();
        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            s = s[1..^1];
        }

        var names = s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (names.Length == 0)
        {
            throw new ReplException("usage: .typeparams (T, U)");
        }

        foreach (var name in names)
        {
            if (!InstructionParser.IsIdentifier(name))
            {
                throw new ReplException($"bad type parameter name '{name}'");
            }

            if (_typeParameterNames.Contains(name) || names.Count(n => n == name) > 1)
            {
                throw new ReplException($"type parameter '{name}' is already declared");
            }
        }

        _typeParameterNames.AddRange(names);
        TypeArguments = null;
        Rebuild();
        Generation++;
    }

    private void BindTypeArguments(string spec)
    {
        if (_typeParameterNames.Count == 0)
        {
            throw new ReplException("the cell has no type parameters; declare them with .typeparams first");
        }

        var s = spec.Trim();
        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            s = s[1..^1];
        }

        var context = new ParseContext([], [], GenericContext.Empty, Resolver, Signatures(), _typeTable);
        var types = TypeParser.SplitTopLevel(s).Select(t => TypeParser.Parse(t, context)).ToArray();
        if (types.Length != _typeParameterNames.Count)
        {
            throw new ReplException($"expected {_typeParameterNames.Count} type argument(s) for ({string.Join(", ", _typeParameterNames)}), got {types.Length}");
        }

        TypeArguments = types;
        Generation++;
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
                        RequireCompatibleReferences(body, $"{declaration.KindWord} {declaration.FullName}", replacement, "(the previous definition stays)");
                    }
                }
            }
        }
    }

    private static void RequireCompatibleReferences(CellState state, string what, MethodSignature replacement, string hint)
    {
        foreach (var entry in state.Entries)
        {
            if (entry.Instruction?.Operand is ResolvedMethod { Definition: { } bound } && bound.Name == replacement.Name && !SameSignature(bound, replacement))
            {
                throw new ReplException($"cannot redefine {replacement.Name} as {replacement.Describe()}: {what} references {bound.Describe()}  {hint}");
            }
        }
    }

    private static bool SameSignature(MethodSignature a, MethodSignature b) => SignatureIdentity.Same(a, b);

    private List<MethodSignature> Signatures() => _methods.Select(m => m.Signature).ToList();

    private void Rebuild() => _cell = BuildCell(Signatures());

    private CellState BuildCell(IReadOnlyList<MethodSignature> table) => BuildCell(table, _typeTable);

    private CellState BuildCell(IReadOnlyList<MethodSignature> table, TypeTable types)
    {
        var generics = new GenericContext([], PrototypeGenerics.Create(_typeParameterNames));
        var state = new CellState(Resolver, generics, table, null, false, types, null);
        foreach (var line in _declarationLines)
        {
            state.Apply(NormalizedLine.FromText(line));
        }

        foreach (var line in _bodyLines)
        {
            state.Apply(NormalizedLine.FromText(line));
        }

        return state;
    }

    private CellState ReplayOpenBody(OpenMethodBlock open, bool? braceSeen = null) => ReplayBody(open.Signature, open.BodyLines, open.Signatures, braceSeen ?? open.State.BraceSeen);

    private CellState ReplayBody(MethodSignature signature, IReadOnlyList<string> lines, IReadOnlyList<MethodSignature> table, bool braceSeen = true)
    {
        // The opening brace is never stored: a replay starts with it seen unless told otherwise.
        var state = new CellState(Resolver, GenericContext.Empty, table, signature, braceSeen, _typeTable, null);
        foreach (var line in lines)
        {
            state.Apply(NormalizedLine.FromText(line));
        }

        return state;
    }
}
