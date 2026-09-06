using System.Diagnostics;

namespace IlRepl.Engine;

/// <summary>
/// One REPL session: the resolver, the declarations that persist across cells, and the cell
/// currently being written. Lines are validated as they arrive; <see cref="Run"/> compiles and
/// executes the cell and clears it.
/// </summary>
public sealed class Session
{
    private readonly List<string> _declarationLines = [];
    private readonly List<string> _bodyLines = [];
    private readonly List<string> _typeParameterNames = [];

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
        State = new CellState(resolver, GenericContext.Empty);
    }

    /// <summary>
    /// The type resolver.
    /// </summary>
    public TypeResolver Resolver { get; }

    /// <summary>
    /// The validated state of the current cell, used for the stack echo and listings.
    /// </summary>
    public CellState State { get; private set; }

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
    /// Adds a line to the cell. Declarations are kept across runs; everything else belongs to the current cell.
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

        var result = State.Apply(line);
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
    /// Removes the last body line.
    /// </summary>
    /// <returns>True when a line was removed.</returns>
    public bool Undo()
    {
        if (_bodyLines.Count == 0)
        {
            return false;
        }

        _bodyLines.RemoveAt(_bodyLines.Count - 1);
        Rebuild();
        return true;
    }

    /// <summary>
    /// Drops the cell body and keeps the declarations.
    /// </summary>
    public void ClearCell()
    {
        _bodyLines.Clear();
        Rebuild();
    }

    /// <summary>
    /// Drops the cell body, the declarations, and any bound type arguments.
    /// </summary>
    public void Reset()
    {
        _bodyLines.Clear();
        _declarationLines.Clear();
        _typeParameterNames.Clear();
        TypeArguments = null;
        Rebuild();
    }

    /// <summary>
    /// Compiles and runs the cell, then clears the body. Declarations stay.
    /// </summary>
    /// <returns>The result.</returns>
    /// <exception cref="ReplException">The cell is incomplete or the runtime rejected it.</exception>
    /// <exception cref="CellException">The cell threw.</exception>
    public CellResult Run()
    {
        var isVoid = State.Stack.Count == 0 && !State.ReturnsValue;
        var compiled = CellCompiler.Compile(this);
        var typeArguments = TypeArguments;
        ClearCell();
        CellsRun++;

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
        }

        return new CellResult(value, isVoid, stopwatch.Elapsed, capture.StandardOutput, capture.StandardError);
    }

    /// <summary>
    /// Writes the current cell to disk as an assembly with a static <c>IlRepl.Cell.Run</c> method. The cell is kept.
    /// </summary>
    /// <param name="path">The output path.</param>
    /// <exception cref="ReplException">The cell is incomplete or the runtime rejected it.</exception>
    public void Save(string path) => CellCompiler.Save(this, path);

    /// <summary>
    /// Renders the current cell as ILAsm source.
    /// </summary>
    /// <returns>The ILAsm text.</returns>
    public string ToIlAsm() => IlAsmRenderer.Render(this);

    private void DeclareTypeParameters(string spec)
    {
        if (!State.IsEmpty)
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

        var context = new ParseContext([], [], GenericContext.Empty, Resolver);
        var types = TypeParser.SplitTopLevel(s).Select(t => TypeParser.Parse(t, context)).ToArray();
        if (types.Length != _typeParameterNames.Count)
        {
            throw new ReplException($"expected {_typeParameterNames.Count} type argument(s) for ({string.Join(", ", _typeParameterNames)}), got {types.Length}");
        }

        TypeArguments = types;
    }

    private void Rebuild()
    {
        var generics = new GenericContext([], PrototypeGenerics.Create(_typeParameterNames));
        var state = new CellState(Resolver, generics);
        foreach (var line in _declarationLines)
        {
            state.Apply(line);
        }

        foreach (var line in _bodyLines)
        {
            state.Apply(line);
        }

        State = state;
    }
}
