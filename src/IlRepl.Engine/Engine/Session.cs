using System.Diagnostics;

namespace IlRepl.Engine;

/// <summary>
/// One REPL session: the resolver, the declarations and methods that persist across cells, and
/// the cell currently being written. Lines are validated as they arrive; <see cref="Run"/>
/// compiles and executes the cell and clears it. While a <c>.method</c> block is open, lines go
/// to the method instead, and closing it commits the method and completes the submission.
/// </summary>
public sealed class Session
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
    public CellState State => _open?.State ?? _cell;

    /// <summary>
    /// The validated state of the cell itself, whether or not a method block is open.
    /// </summary>
    public CellState Cell => _cell;

    /// <summary>
    /// The signature of the method block being typed, or null when lines go to the cell.
    /// </summary>
    public MethodSignature? OpenMethod => _open?.Signature;

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
    /// Adds a line. Declarations are kept across runs; a <c>.method</c> header opens a block that
    /// takes the following lines until <c>}</c>; everything else belongs to the current cell.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>What the line was.</returns>
    /// <exception cref="ReplException">The line is invalid.</exception>
    public LineResult AddLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = InstructionParser.StripComments(line).Trim();
        if (text.Length == 0)
        {
            return new LineResult(LineOutcome.Empty, null, null);
        }

        if (_open is not null)
        {
            return AddMethodLine(line);
        }

        if (text.StartsWith(".method", StringComparison.Ordinal) && (text.Length == ".method".Length || !char.IsLetter(text[".method".Length])))
        {
            return OpenBlock(text[".method".Length..], line);
        }

        if (text.StartsWith(".typeparams", StringComparison.Ordinal))
        {
            DeclareTypeParameters(text[".typeparams".Length..]);
            _declarationLines.Add(line);
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
                _declarationLines.Add(line);
                break;
            default:
                _bodyLines.Add(line);
                break;
        }

        return result;
    }

    /// <summary>
    /// Removes the last line: of the open method block, or of the cell body. Removing a method
    /// header abandons the block. Committed methods and declarations are not undone.
    /// </summary>
    /// <returns>True when a line was removed.</returns>
    public bool Undo()
    {
        if (_open is not null)
        {
            if (_open.BodyLines.Count == 0)
            {
                _open = null;
                return true;
            }

            _open.BodyLines.RemoveAt(_open.BodyLines.Count - 1);
            _open.State = ReplayOpenBody(_open);
            return true;
        }

        if (_bodyLines.Count == 0)
        {
            return false;
        }

        _bodyLines.RemoveAt(_bodyLines.Count - 1);
        Rebuild();
        return true;
    }

    /// <summary>
    /// Drops the open method block without committing it. The cell and the methods are untouched.
    /// </summary>
    /// <returns>True when a block was open.</returns>
    public bool AbandonMethod()
    {
        if (_open is null)
        {
            return false;
        }

        _open = null;
        return true;
    }

    /// <summary>
    /// Drops the cell body and keeps the declarations and the methods.
    /// </summary>
    public void ClearCell()
    {
        _bodyLines.Clear();
        Rebuild();
    }

    /// <summary>
    /// Drops the cell body, the declarations, the methods, any open method block, and any bound type arguments.
    /// </summary>
    public void Reset()
    {
        _bodyLines.Clear();
        _declarationLines.Clear();
        _typeParameterNames.Clear();
        _methods.Clear();
        _open = null;
        TypeArguments = null;
        Rebuild();
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
    public void Save(string path) => CellCompiler.Save(this, path);

    /// <summary>
    /// Renders the session methods and the current cell as ILAsm source.
    /// </summary>
    /// <returns>The ILAsm text.</returns>
    public string ToIlAsm() => IlAsmRenderer.Render(this);

    private LineResult OpenBlock(string spec, string line)
    {
        var signatures = Signatures();
        var headerContext = new ParseContext([], [], GenericContext.Empty, Resolver, signatures);
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

        if (replacing is not null)
        {
            // Dependents bind to the signature, which is known now. Checking here, rather than at
            // the closing brace, means a refused redefinition costs nothing to recover from. A
            // reference already bound to the old signature is checked directly, because a replay
            // would happily rebind an abbreviated reference such as "ldftn F" to the new one.
            foreach (var other in _methods)
            {
                if (ReferenceEquals(other, replacing))
                {
                    continue;
                }

                RequireCompatibleReferences(other.State, "method " + other.Signature.Name, signature, "(the previous definition stays)");
                try
                {
                    ReplayMethod(other, table);
                }
                catch (ReplException ex)
                {
                    throw new ReplException($"cannot redefine {signature.Name} as {signature.Describe()}: method {other.Signature.Name} would no longer compile: {ex.Message}  (the previous definition stays)", ex);
                }
            }

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
            State = new CellState(Resolver, GenericContext.Empty, table, signature, braceOpen),
        };
        return new LineResult(LineOutcome.MethodStart, null, "method " + signature.DescribeWithNames());
    }

    private LineResult AddMethodLine(string line)
    {
        var open = _open!;
        var result = open.State.Apply(line);
        if (result.Outcome == LineOutcome.MethodEnd)
        {
            return CloseBlock();
        }

        if (result.Outcome != LineOutcome.Empty)
        {
            open.BodyLines.Add(line);
        }

        return result;
    }

    private LineResult CloseBlock()
    {
        var open = _open!;
        var name = open.Signature.Name;
        var candidate = new SessionMethod(open.Signature, open.HeaderLine, [.. open.BodyLines], open.State);

        // Rebuild every dependent against the committed table. The header already checked them;
        // this pass produces the states that are kept, and catches a .load that changed
        // resolution in between.
        var committed = new List<SessionMethod>();
        var changed = new List<string> { name };
        foreach (var existing in _methods)
        {
            if (ReferenceEquals(existing, open.Replacing))
            {
                committed.Add(candidate);
                continue;
            }

            if (open.Replacing is null)
            {
                committed.Add(existing);
                continue;
            }

            RequireCompatibleReferences(existing.State, "method " + existing.Signature.Name, open.Signature, "(the previous definition stays)");
            try
            {
                committed.Add(existing with { State = ReplayMethod(existing, open.Signatures) });
                changed.Add(existing.Signature.Name);
            }
            catch (ReplException ex)
            {
                throw new ReplException($"cannot replace {name}: method {existing.Signature.Name} would no longer compile: {ex.Message}  (the previous definition stays)", ex);
            }
        }

        if (open.Replacing is null)
        {
            committed.Add(candidate);
        }

        if (open.Replacing is not null)
        {
            RequireCompatibleReferences(_cell, "the cell body", open.Signature, "(.clear the cell first, or keep the signature)");
        }

        CellState cell;
        try
        {
            cell = BuildCell(open.Signatures);
        }
        catch (ReplException ex)
        {
            throw new ReplException($"cannot {(open.Replacing is null ? "define" : "replace")} {name}: the cell body would no longer compile: {ex.Message}  (.clear the cell first)", ex);
        }

        CellCompiler.ValidateMethods(committed, changed);

        _methods.Clear();
        _methods.AddRange(committed);
        _cell = cell;
        _open = null;
        Submissions++;
        return new LineResult(LineOutcome.MethodEnd, null, open.Replacing is null ? $"end of method {name}" : $"replaced method {name}");
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

        var context = new ParseContext([], [], GenericContext.Empty, Resolver, Signatures());
        var types = TypeParser.SplitTopLevel(s).Select(t => TypeParser.Parse(t, context)).ToArray();
        if (types.Length != _typeParameterNames.Count)
        {
            throw new ReplException($"expected {_typeParameterNames.Count} type argument(s) for ({string.Join(", ", _typeParameterNames)}), got {types.Length}");
        }

        TypeArguments = types;
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

    private static bool SameSignature(MethodSignature a, MethodSignature b) =>
        MemberResolver.TypesEqual(a.ReturnType, b.ReturnType)
        && a.Parameters.Count == b.Parameters.Count
        && a.ParameterTypes.Zip(b.ParameterTypes).All(pair => MemberResolver.TypesEqual(pair.First, pair.Second));

    private List<MethodSignature> Signatures() => _methods.Select(m => m.Signature).ToList();

    private void Rebuild() => _cell = BuildCell(Signatures());

    private CellState BuildCell(IReadOnlyList<MethodSignature> table)
    {
        var generics = new GenericContext([], PrototypeGenerics.Create(_typeParameterNames));
        var state = new CellState(Resolver, generics, table, null, false);
        foreach (var line in _declarationLines)
        {
            state.Apply(line);
        }

        foreach (var line in _bodyLines)
        {
            state.Apply(line);
        }

        return state;
    }

    private CellState ReplayMethod(SessionMethod method, IReadOnlyList<MethodSignature> table)
    {
        var state = ReplayBody(method.Signature, method.BodyLines, table);
        state.ValidateMethodEnd();
        return state;
    }

    private CellState ReplayOpenBody(OpenMethodBlock open) => ReplayBody(open.Signature, open.BodyLines, open.Signatures);

    private CellState ReplayBody(MethodSignature signature, IReadOnlyList<string> lines, IReadOnlyList<MethodSignature> table)
    {
        // The opening brace is never stored, so a replay starts as if it had been seen.
        var state = new CellState(Resolver, GenericContext.Empty, table, signature, braceOpen: true);
        foreach (var line in lines)
        {
            state.Apply(line);
        }

        return state;
    }
}
