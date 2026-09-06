using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// The parsed contents of a cell, built up one line at a time. It is replayable: the session
/// keeps a copy for validation and echo, and the compiler builds a fresh one against the real
/// method so generic parameters bind to the method being emitted.
/// </summary>
public sealed class CellState
{
    private readonly List<LocalDeclaration> _locals = [];
    private readonly List<ArgumentDeclaration> _arguments = [];
    private readonly List<CellEntry> _entries = [];
    private readonly HashSet<string> _definedLabels = new(StringComparer.Ordinal);
    private readonly List<BlockKind> _frames = [];

    /// <summary>
    /// Initializes an empty cell.
    /// </summary>
    /// <param name="resolver">The type resolver.</param>
    /// <param name="generics">The generic parameters in scope for <c>!!N</c>.</param>
    public CellState(TypeResolver resolver, GenericContext generics)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(generics);
        Resolver = resolver;
        Generics = generics;
    }

    /// <summary>
    /// The type resolver.
    /// </summary>
    public TypeResolver Resolver { get; }

    /// <summary>
    /// The generic parameters in scope.
    /// </summary>
    public GenericContext Generics { get; }

    /// <summary>
    /// The declared locals.
    /// </summary>
    public IReadOnlyList<LocalDeclaration> Locals => _locals;

    /// <summary>
    /// The declared arguments.
    /// </summary>
    public IReadOnlyList<ArgumentDeclaration> Arguments => _arguments;

    /// <summary>
    /// The accepted entries, in order.
    /// </summary>
    public IReadOnlyList<CellEntry> Entries => _entries;

    /// <summary>
    /// The simulated evaluation stack after the last entry.
    /// </summary>
    public StackSimulator Stack { get; } = new();

    /// <summary>
    /// True when the cell was marked <c>.vararg</c>.
    /// </summary>
    public bool IsVarArg { get; private set; }

    /// <summary>
    /// The labels defined so far.
    /// </summary>
    public IReadOnlySet<string> DefinedLabels => _definedLabels;

    /// <summary>
    /// How many exception-handling regions are open.
    /// </summary>
    public int OpenBlockDepth => _frames.Count;

    /// <summary>
    /// True when a branch targets a label that has not been defined yet.
    /// </summary>
    public bool HasPendingLabels => ReferencedLabels().Any(l => !_definedLabels.Contains(l));

    /// <summary>
    /// True when the cell body has no instructions, labels, or blocks.
    /// </summary>
    public bool IsEmpty => !_entries.Any(e => e.Kind is EntryKind.Instruction or EntryKind.Labels or EntryKind.Block);

    /// <summary>
    /// The number of instructions in the cell.
    /// </summary>
    public int InstructionCount => _entries.Count(e => e.Instruction is not null);

    /// <summary>
    /// True when any inline <c>ret</c> returns a value.
    /// </summary>
    public bool ReturnsValue => _entries.Any(e => e.Instruction is { Op.Name: "ret", RetPops: 1 });

    /// <summary>
    /// The parse context for the next line.
    /// </summary>
    public ParseContext Context => new(_locals, _arguments, Generics, Resolver);

    /// <summary>
    /// The labels referenced by branches and switch tables.
    /// </summary>
    /// <returns>The label names, possibly repeated.</returns>
    public IEnumerable<string> ReferencedLabels()
    {
        foreach (var e in _entries)
        {
            if (e.Instruction?.Kind == OperandKind.Label)
            {
                yield return (string)e.Instruction.Operand!;
            }
            else if (e.Instruction?.Kind == OperandKind.Labels)
            {
                foreach (var l in (string[])e.Instruction.Operand!)
                {
                    yield return l;
                }
            }
        }
    }

    /// <summary>
    /// Parses and records one line. Nothing changes when the line is rejected.
    /// </summary>
    /// <param name="line">The line: an instruction, labels, a block boundary, or a declaration.</param>
    /// <returns>What the line was.</returns>
    /// <exception cref="ReplException">The line is invalid in the current state.</exception>
    public LineResult Apply(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = InstructionParser.StripComments(line).Trim();
        if (text.Length == 0)
        {
            return new LineResult(LineOutcome.Empty, null, null);
        }

        if (text.StartsWith('.'))
        {
            return ApplyDirective(text, line);
        }

        if (text == "{")
        {
            if (_entries.Count > 0 && _entries[^1].Kind == EntryKind.Block && _entries[^1].Block != BlockKind.End)
            {
                return new LineResult(LineOutcome.Empty, null, null);
            }

            throw new ReplException("unexpected '{'; open a protected region with .try {");
        }

        if (text.StartsWith('}') || StartsWithHandlerKeyword(text))
        {
            return ApplyBlock(text, line);
        }

        var (labels, rest) = InstructionParser.SplitLabels(text);
        foreach (var l in labels)
        {
            if (_definedLabels.Contains(l))
            {
                throw new ReplException($"label '{l}' is already defined");
            }
        }

        if (rest.Length == 0)
        {
            _entries.Add(new CellEntry { Kind = EntryKind.Labels, Source = line, Labels = labels });
            _definedLabels.UnionWith(labels);
            return new LineResult(LineOutcome.Labels, null, null);
        }

        var context = Context;
        var instruction = InstructionParser.Parse(rest, context);
        if (instruction.Op == OpCodes.Ret)
        {
            instruction = InlineRet(instruction.Text);
        }

        if (instruction.Op == OpCodes.Arglist && !IsVarArg)
        {
            throw new ReplException("arglist needs a vararg cell; add the .vararg directive first");
        }

        if (instruction.Op == OpCodes.Endfilter && (_frames.Count == 0 || _frames[^1] != BlockKind.Filter))
        {
            throw new ReplException("endfilter is only valid inside a filter block (} filter {)");
        }

        var next = Stack.Clone();
        next.Apply(instruction, context);

        _entries.Add(new CellEntry { Kind = EntryKind.Instruction, Source = line, Labels = labels, Instruction = instruction });
        _definedLabels.UnionWith(labels);
        Stack.CopyFrom(next);
        return new LineResult(LineOutcome.Instruction, instruction, null);
    }

    private static bool StartsWithHandlerKeyword(string text) =>
        text.StartsWith("catch", StringComparison.Ordinal) || text.StartsWith("filter", StringComparison.Ordinal)
        || text.StartsWith("finally", StringComparison.Ordinal) || text.StartsWith("fault", StringComparison.Ordinal)
        || text.StartsWith("handler", StringComparison.Ordinal);

    private LineResult ApplyDirective(string text, string source)
    {
        var space = text.IndexOfAny([' ', '\t', '(']);
        var directive = space < 0 ? text : text[..space];
        var rest = space < 0 ? "" : text[space..].Trim();
        switch (directive)
        {
            case ".locals":
            {
                var declared = ParseLocals(rest);
                _locals.AddRange(declared);
                _entries.Add(new CellEntry { Kind = EntryKind.Locals, Source = source, Locals = declared });
                return new LineResult(LineOutcome.Locals, null, DescribeLocals());
            }

            case ".args":
            {
                var declared = ParseArguments(rest);
                _arguments.AddRange(declared);
                _entries.Add(new CellEntry { Kind = EntryKind.Arguments, Source = source, Arguments = declared });
                return new LineResult(LineOutcome.Arguments, null, DescribeArguments());
            }

            case ".vararg":
                IsVarArg = true;
                _entries.Add(new CellEntry { Kind = EntryKind.VarArg, Source = source });
                return new LineResult(LineOutcome.VarArg, null, "the cell method now uses the vararg calling convention");

            case ".try":
                if (rest is not ("" or "{"))
                {
                    throw new ReplException("the label form of .try is not supported; use blocks: .try { ... } catch T { ... }");
                }

                _frames.Add(BlockKind.Try);
                _entries.Add(new CellEntry { Kind = EntryKind.Block, Source = source, Block = BlockKind.Try });
                Stack.ApplyBlock(BlockKind.Try, null);
                return new LineResult(LineOutcome.Block, null, "try");

            case ".maxstack":
            case ".typeparams":
            case ".typeargs":
                return new LineResult(LineOutcome.Empty, null, null);

            default:
                throw new ReplException($"unknown directive '{directive}'; expected .locals, .args, .typeparams, .typeargs, .vararg, .try, or .maxstack");
        }
    }

    private LineResult ApplyBlock(string text, string source)
    {
        var rest = text.StartsWith('}') ? text[1..].Trim() : text;
        if (rest.Length == 0)
        {
            if (_frames.Count == 0)
            {
                throw new ReplException("unexpected '}': no protected region is open");
            }

            var top = _frames[^1];
            if (top == BlockKind.Try)
            {
                throw new ReplException("a .try needs a handler before it closes: } catch T {, } finally {, } fault {, or } filter {");
            }

            if (top == BlockKind.Filter)
            {
                throw new ReplException("a filter needs a handler block before the region closes: } handler {");
            }

            _frames.RemoveAt(_frames.Count - 1);
            _entries.Add(new CellEntry { Kind = EntryKind.Block, Source = source, Block = BlockKind.End });
            Stack.ApplyBlock(BlockKind.End, null);
            return new LineResult(LineOutcome.Block, null, "end of protected region");
        }

        if (rest.EndsWith('{'))
        {
            rest = rest[..^1].Trim();
        }

        if (_frames.Count == 0)
        {
            throw new ReplException($"'{rest}' needs an open .try region");
        }

        var current = _frames[^1];
        BlockKind kind;
        Type? catchType = null;
        string message;
        if (rest.StartsWith("catch", StringComparison.Ordinal))
        {
            if (current == BlockKind.Filter)
            {
                throw new ReplException("a filter needs a handler block (} handler {) before the next handler");
            }

            var typeText = rest[5..].Trim();
            catchType = typeText.Length == 0 ? typeof(object) : TypeParser.Parse(typeText, Context);
            kind = BlockKind.Catch;
            message = "catch " + TypeNameFormatter.Pretty(catchType);
        }
        else if (rest == "filter")
        {
            if (current == BlockKind.Filter)
            {
                throw new ReplException("a filter needs a handler block (} handler {) before the next handler");
            }

            kind = BlockKind.Filter;
            message = "filter (end it with endfilter, then } handler {)";
        }
        else if (rest == "handler")
        {
            if (current != BlockKind.Filter)
            {
                throw new ReplException("'} handler {' is only valid after a filter block");
            }

            if (_entries.Count == 0 || _entries[^1].Instruction?.Op != OpCodes.Endfilter)
            {
                throw new ReplException("a filter must end with endfilter, leaving one int32 on the stack");
            }

            kind = BlockKind.FilterHandler;
            message = "filter handler";
        }
        else if (rest == "finally")
        {
            if (current == BlockKind.Filter)
            {
                throw new ReplException("a filter needs a handler block (} handler {) before the next handler");
            }

            kind = BlockKind.Finally;
            message = "finally";
        }
        else if (rest == "fault")
        {
            if (current == BlockKind.Filter)
            {
                throw new ReplException("a filter needs a handler block (} handler {) before the next handler");
            }

            kind = BlockKind.Fault;
            message = "fault";
        }
        else
        {
            throw new ReplException($"unknown handler '{rest}'; expected catch T, filter, handler, finally, or fault");
        }

        if (kind != BlockKind.FilterHandler && current is BlockKind.Finally or BlockKind.Fault)
        {
            throw new ReplException("finally and fault must be the last handler of a region");
        }

        _frames[^1] = kind;
        _entries.Add(new CellEntry { Kind = EntryKind.Block, Source = source, Block = kind, CatchType = catchType });
        Stack.ApplyBlock(kind, catchType);
        return new LineResult(LineOutcome.Block, null, message);
    }

    private Instruction InlineRet(string text)
    {
        if (Stack.Count > 1)
        {
            throw new ReplException($"the stack must hold 0 or 1 value at ret, but has {Stack.Count}: {Stack.Render()}  (pop, or stloc into a local)");
        }

        var top = Stack.Top;
        if (top is { IsByRef: true } || top is { IsPointer: true })
        {
            throw new ReplException($"cannot return a {StackSimulator.Name(top)} from the cell; load through it first (ldind/ldobj)");
        }

        if (_frames.Count > 0)
        {
            throw new ReplException("ret is not allowed inside a protected region; use leave to exit it first");
        }

        return new Instruction
        {
            Op = OpCodes.Ret,
            Text = text,
            RetPops = Stack.Count,
            RetBox = top is { IsValueType: true } && top != typeof(NullReferenceMarker) ? top : null,
        };
    }

    private List<LocalDeclaration> ParseLocals(string spec)
    {
        var s = TypeParser.Normalize(spec).Trim();
        if (s.StartsWith("init", StringComparison.Ordinal) && (s.Length == 4 || !char.IsLetterOrDigit(s[4])))
        {
            s = s[4..].Trim();
        }

        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            s = s[1..^1];
        }

        if (s.Trim().Length == 0)
        {
            throw new ReplException("usage: .locals init (int32 x, string s)");
        }

        var context = Context;
        var declared = new List<LocalDeclaration>();
        foreach (var raw in TypeParser.SplitTopLevel(s))
        {
            var part = raw;
            if (part.StartsWith('['))
            {
                var close = part.IndexOf(']', StringComparison.Ordinal);
                if (close < 0)
                {
                    throw new ReplException($"bad local declaration '{raw}'");
                }

                part = part[(close + 1)..].Trim();
            }

            var pos = 0;
            var type = TypeParser.ParseAt(part, ref pos, context, out var pinned);
            var name = part[pos..].Trim();
            if (name.StartsWith('\'') && name.EndsWith('\'') && name.Length > 2)
            {
                name = name[1..^1];
            }

            if (name.Length > 0 && !InstructionParser.IsIdentifier(name))
            {
                throw new ReplException($"bad local name '{name}'");
            }

            if (name.Length > 0 && (_locals.Any(l => l.Name == name) || declared.Any(l => l.Name == name)))
            {
                throw new ReplException($"local '{name}' is already declared");
            }

            if (type == typeof(void))
            {
                throw new ReplException("a local cannot be void");
            }

            declared.Add(new LocalDeclaration(type, name.Length == 0 ? null : name, pinned));
        }

        return declared;
    }

    private List<ArgumentDeclaration> ParseArguments(string spec)
    {
        var s = TypeParser.Normalize(spec).Trim();
        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            s = s[1..^1];
        }

        if (s.Trim().Length == 0)
        {
            throw new ReplException("usage: .args (int32 x = 5, string s = \"hi\")");
        }

        var context = Context;
        var declared = new List<ArgumentDeclaration>();
        foreach (var part in TypeParser.SplitTopLevel(s))
        {
            var equals = IndexOfTopLevelEquals(part);
            var declaration = equals < 0 ? part : part[..equals].Trim();
            var literal = equals < 0 ? null : part[(equals + 1)..].Trim();

            var pos = 0;
            var type = TypeParser.ParseAt(declaration, ref pos, context, out _);
            var name = declaration[pos..].Trim();
            if (name.StartsWith('\'') && name.EndsWith('\'') && name.Length > 2)
            {
                name = name[1..^1];
            }

            if (name.Length > 0 && !InstructionParser.IsIdentifier(name))
            {
                throw new ReplException($"bad argument name '{name}'");
            }

            if (name.Length > 0 && (_arguments.Any(a => a.Name == name) || declared.Any(a => a.Name == name)))
            {
                throw new ReplException($"argument '{name}' is already declared");
            }

            if (type == typeof(void))
            {
                throw new ReplException("an argument cannot be void");
            }

            if (type.ContainsGenericParameters)
            {
                throw new ReplException("arguments cannot use the cell's generic parameters; pass a concrete type");
            }

            object? value;
            if (literal is not null)
            {
                value = ValueLiteralParser.Parse(literal, type);
            }
            else if (type.IsValueType)
            {
                value = Activator.CreateInstance(type);
            }
            else
            {
                value = null;
            }

            declared.Add(new ArgumentDeclaration(type, name.Length == 0 ? null : name, value, literal ?? (type.IsValueType ? "default" : "null")));
        }

        return declared;
    }

    private static int IndexOfTopLevelEquals(string s)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '<':
                case '[':
                case '(':
                    depth++;
                    break;
                case '>':
                case ']':
                case ')':
                    depth--;
                    break;
                case '=' when depth == 0:
                    return i;
                default:
                    break;
            }
        }

        return -1;
    }

    private string DescribeLocals() =>
        string.Join(", ", _locals.Select((l, i) => $"{i}:{TypeNameFormatter.Pretty(l.Type)}{(l.IsPinned ? " pinned" : "")} {l.Name ?? ""}".TrimEnd()));

    private string DescribeArguments() =>
        string.Join(", ", _arguments.Select((a, i) => $"{i}:{TypeNameFormatter.Pretty(a.Type)} {a.Name ?? ""} = {a.ValueText}".Replace("  ", " ", StringComparison.Ordinal)));
}
