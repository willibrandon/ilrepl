using System.Reflection.Emit;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

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
    private bool _braceSeen;
    private FlowResult<Type>? _analysis;
    private FlowResult<Type>? _analysisBeforeLine;
    private AnalysisLocation? _currentLocation;

    /// <summary>
    /// The converged states for the current accepted entries.
    /// </summary>
    internal FlowResult<Type> Analysis => _analysis ??= RuntimeFlowAnalysis.Run(this, _entries);

    /// <summary>
    /// The latest control-flow findings for the accepted body.
    /// </summary>
    public IReadOnlyList<AnalysisDiagnostic> Diagnostics => Analysis.Diagnostics;

    /// <summary>
    /// The current stack presentation, including unreachable and unknown source positions.
    /// </summary>
    public string StackText => RuntimeFlowAnalysis.Rules(Types).Render(
        _entries.Count > 0 ? Analysis.After[_entries.Count - 1] : Analysis.End);

    /// <summary>
    /// Refuses a proven stack or control-transfer error before emission.
    /// </summary>
    internal void RequireValidFlow()
    {
        if (Analysis.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error) is { } error)
        {
            throw new ReplException(error.Message) { Diagnostics = [error] };
        }
    }

    /// <summary>
    /// Requires valid flow with every referenced target and prefix resolved.
    /// </summary>
    /// <param name="hasImplicitReturn">Whether method emission adds a return after the accepted source.</param>
    internal void RequireCompleteFlow(bool hasImplicitReturn = false)
    {
        RequireValidFlow();
        if (Analysis.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Incomplete
            && (!hasImplicitReturn || diagnostic.Code != "FLOW021")) is { } pending)
        {
            throw new ReplException(pending.Message) { Diagnostics = [pending] };
        }
    }

    /// <summary>
    /// For a method body: true once the opening brace has been seen, on the header line or on its own.
    /// </summary>
    public bool BraceSeen => _braceSeen;

    /// <summary>
    /// Initializes an empty cell.
    /// </summary>
    /// <param name="resolver">The type resolver.</param>
    /// <param name="generics">The generic parameters in scope for <c>!!N</c>.</param>
    public CellState(TypeResolver resolver, GenericContext generics) : this(resolver, generics, [], null, false)
    {
    }

    /// <summary>
    /// Initializes an empty cell, or the body of a <c>.method</c> when a signature is given. A
    /// method body names its parameters with <c>ldarg</c>, owns its locals and labels, and checks
    /// <c>ret</c> against the declared return type.
    /// </summary>
    /// <param name="resolver">The type resolver.</param>
    /// <param name="generics">The generic parameters in scope for <c>!!N</c>.</param>
    /// <param name="methods">The session methods a call can name without a type.</param>
    /// <param name="signature">The method's signature, or null for the cell.</param>
    /// <param name="braceOpen">For a method body: true when the header line already carried the opening brace.</param>
    public CellState(TypeResolver resolver, GenericContext generics, IReadOnlyList<MethodSignature> methods, MethodSignature? signature, bool braceOpen)
        : this(resolver, generics, methods, signature, braceOpen, TypeTable.Empty, null)
    {
    }

    /// <summary>
    /// Initializes an empty cell, the body of a session method, or the body of a member of a
    /// type being written. An instance member has <c>this</c> at argument 0.
    /// </summary>
    /// <param name="resolver">The type resolver.</param>
    /// <param name="generics">The generic parameters in scope for <c>!N</c> and <c>!!N</c>.</param>
    /// <param name="methods">The session methods a call can name without a type.</param>
    /// <param name="signature">The method's signature, or null for the cell.</param>
    /// <param name="braceOpen">For a method body: true when the header line already carried the opening brace.</param>
    /// <param name="types">The session types a name can resolve to.</param>
    /// <param name="member">The type this body belongs to, or null for the cell and session methods.</param>
    public CellState(TypeResolver resolver, GenericContext generics, IReadOnlyList<MethodSignature> methods, MethodSignature? signature, bool braceOpen, TypeTable types, MemberContext? member)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(generics);
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(types);
        Resolver = resolver;
        Generics = generics;
        Methods = methods;
        Signature = signature;
        Types = types;
        Member = member;
        _braceSeen = braceOpen;
        if (member?.ThisType is { } thisType)
        {
            _arguments.Add(new ArgumentDeclaration(thisType, null, null, ""));
        }

        if (signature is not null)
        {
            _arguments.AddRange(signature.Parameters);
        }
    }

    /// <summary>
    /// The session types a name can resolve to.
    /// </summary>
    public TypeTable Types { get; }

    /// <summary>
    /// The type this body belongs to, or null for the cell and session methods.
    /// </summary>
    public MemberContext? Member { get; }

    /// <summary>
    /// True when this is the body of a member of a type being written.
    /// </summary>
    public bool IsMember => Member is not null;

    /// <summary>
    /// The <c>.override</c> lines written in this body.
    /// </summary>
    public IEnumerable<OverrideDeclaration> Overrides => _entries.Where(e => e.Override is not null).Select(e => e.Override!);

    /// <summary>
    /// The type resolver.
    /// </summary>
    public TypeResolver Resolver { get; }

    /// <summary>
    /// The generic parameters in scope.
    /// </summary>
    public GenericContext Generics { get; }

    /// <summary>
    /// The session methods a call can name without a type.
    /// </summary>
    public IReadOnlyList<MethodSignature> Methods { get; }

    /// <summary>
    /// The signature when this is a <c>.method</c> body; null for the cell.
    /// </summary>
    public MethodSignature? Signature { get; }

    /// <summary>
    /// True when this is a <c>.method</c> body rather than the cell.
    /// </summary>
    public bool IsMethod => Signature is not null;

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
    /// True when the last instruction ends its path (a return, throw, unconditional branch, or
    /// jump) so nothing falls off the end of the body. A trailing label or block boundary means
    /// the end is reachable.
    /// </summary>
    public bool LastInstructionEndsFlow
    {
        get
        {
            return Analysis.End is null;
        }
    }

    /// <summary>
    /// The parse context for the next line.
    /// </summary>
    public ParseContext Context => new(_locals, _arguments, Generics, Resolver, Methods, Types)
    {
        ThisIndex
        = Member?.ThisType is null ? -1 : 0,
        Scope = Scope
    };

    /// <summary>
    /// Checks that a <c>.method</c> body can close: every label is defined, and either the last
    /// instruction ends its path or the stack holds what the return type needs for an implied
    /// <c>ret</c> (nothing for <c>void</c>, exactly one compatible value otherwise).
    /// </summary>
    /// <exception cref="InvalidOperationException">This is the cell, not a method body.</exception>
    /// <exception cref="ReplException">The body cannot close as it stands.</exception>
    public void ValidateMethodEnd()
    {
        if (Signature is null)
        {
            throw new InvalidOperationException("only a method body can close");
        }

        RequireValidFlow();

        var pending = ReferencedLabels().Where(l => !_definedLabels.Contains(l)).Distinct().ToList();
        if (pending.Count > 0)
        {
            var suffix = pending.Count > 1 ? "s" : "";
            throw new ReplException(
                $"label{suffix} referenced but never defined: {string.Join(", ", pending)} (define with 'NAME:')");
        }

        if (LastInstructionEndsFlow || Member is { IsAbstract: true })
        {
            RequireCompleteFlow();
            return;
        }

        var name = Signature.Name;
        var returnType = Signature.ReturnType;
        if (returnType == typeof(void))
        {
            if (Stack.Count > 0)
            {
                throw new ReplException(
                    $"method {name} needs a ret before }}: the stack holds {Stack.Render()} but {name} returns void (pop it)");
            }

            RequireCompleteFlow(hasImplicitReturn: true);
            return;
        }

        var pretty = TypeNameFormatter.Pretty(returnType);
        if (Stack.Count == 0)
        {
            throw new ReplException($"method {name} needs a ret before }}: the stack is empty but {name} returns {pretty}");
        }

        if (Stack.Count > 1 || !RuntimeFlowAnalysis.Rules(Types).CanAssign(Stack.Top, returnType))
        {
            throw new ReplException($"method {name} needs a ret before }}: the stack holds {Stack.Render()} but {name} returns {pretty}");
        }

        RequireCompleteFlow(hasImplicitReturn: true);
    }

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
    /// Parses and records one line as typed, comments and all. Nothing changes when the line is rejected.
    /// </summary>
    /// <param name="line">The line: an instruction, labels, a block boundary, or a declaration.</param>
    /// <returns>What the line was.</returns>
    /// <exception cref="ReplException">The line is invalid in the current state.</exception>
    public LineResult Apply(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return Apply(NormalizedLine.FromText(InstructionParser.StripComments(line)));
    }

    /// <summary>
    /// Parses and records one line whose comments are already gone. Nothing changes when the line is rejected.
    /// </summary>
    /// <param name="normalized">The line: an instruction, labels, a block boundary, or a declaration.</param>
    /// <returns>What the line was.</returns>
    /// <exception cref="ReplException">The line is invalid in the current state.</exception>
    public LineResult Apply(NormalizedLine normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        if (normalized.Kind != SourceLineKind.Text)
        {
            return new LineResult(LineOutcome.Empty, null, null);
        }

        var line = normalized.Text;
        _currentLocation = Location(normalized);
        _analysisBeforeLine = _analysis;
        _analysis = null;
        var text = line;
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

            if (IsMethod && !_braceSeen && _entries.Count == 0)
            {
                _braceSeen = true;
                return new LineResult(LineOutcome.Empty, null, null);
            }

            throw new ReplException("unexpected '{'; open a protected region with .try {, or a method with .method");
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
            if (Member is { IsAbstract: true })
            {
                throw new ReplException($"abstract method {Signature!.Name} has no body; close it with }}");
            }

            AcceptEntry(new CellEntry { Kind = EntryKind.Labels, Source = line, Labels = labels,
                Location = Location(normalized) });
            _definedLabels.UnionWith(labels);
            return new LineResult(LineOutcome.Labels, null, null);
        }

        if (Member is { IsAbstract: true })
        {
            throw new ReplException($"abstract method {Signature!.Name} has no body; close it with }}");
        }

        var context = Context;
        var instruction = InstructionParser.Parse(rest, context);
        if (Member?.ThisType is { IsByRef: true } && instruction.ArgumentIndex == 0 && instruction.Op.Name is "ldarga" or "ldarga.s")
        {
            throw new ReplException($"this is already a {TypeNameFormatter.Pretty(Member.ThisType)} in a struct method; use ldarg.0");
        }

        CheckAccess(instruction);

        if (instruction.Op == OpCodes.Arglist && !IsVarArg)
        {
            throw new ReplException("arglist needs a vararg cell; add the .vararg directive first");
        }

        if (instruction.Op == OpCodes.Endfilter && (_frames.Count == 0 || _frames[^1] != BlockKind.Filter))
        {
            throw new ReplException("endfilter is only valid inside a filter block (} filter {)");
        }

        AcceptEntry(new CellEntry { Kind = EntryKind.Instruction, Source = line, Labels = labels, Instruction = instruction,
            Location = Location(normalized) });
        _definedLabels.UnionWith(labels);
        return new LineResult(LineOutcome.Instruction, _entries[^1].Instruction, null);
    }

    private AnalysisLocation Location(NormalizedLine line)
    {
        var start = line.Raw.Length - line.Raw.TrimStart().Length;
        return line.Location ?? new AnalysisLocation(Signature?.Name ?? "cell", _entries.Count, start, line.Raw.Length - start);
    }

    private void AcceptEntry(CellEntry entry)
    {
        entry.Location ??= _currentLocation;
        var candidate = _analysisBeforeLine is { } previous
            && RuntimeFlowAnalysis.TryAppend(this, previous, entry, out var appended)
            ? appended : RuntimeFlowAnalysis.Run(this, [.. _entries, entry]);
        _analysisBeforeLine = null;
        if (candidate.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error) is { } error)
        {
            throw new ReplException(error.Message) { Diagnostics = [error] };
        }

        _entries.Add(entry);
        _analysis = candidate;
        RefreshReturns();
        Stack.CopyFrom(candidate.After[_entries.Count - 1]);
    }

    private void RefreshReturns()
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.Instruction is not { Op.Name: "ret" } instruction)
            {
                continue;
            }

            var values = Analysis.Before[i]?.Values;
            var count = IsMethod ? Signature!.ReturnType == typeof(void) ? 0 : 1 : values?.Length ?? 0;
            var top = values?.LastOrDefault()?.Type;
            var lowered = new Instruction
            {
                Op = instruction.Op,
                Text = instruction.Text,
                RetPops = count,
                RetNull = !IsMethod && count == 0,
                RetBox = !IsMethod && top is { } type && (type.IsValueType || type.IsGenericParameter)
                    && type != typeof(NullReferenceMarker) && StackSimulator.BoxedType(type) is null ? type : null,
            };
            _entries[i] = new CellEntry
            {
                Kind = entry.Kind,
                Source = entry.Source,
                Location = entry.Location,
                Labels = entry.Labels,
                Instruction = lowered,
            };
        }
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
        if (IsMember)
        {
            switch (directive)
            {
                case ".override":
                    return ApplyOverride(rest, source);
                case ".param":
                    return ApplyParam(rest, source);
                case ".custom":
                    return ApplyCustom(rest, source);
                case ".field":
                case ".pack":
                case ".size":
                case ".property":
                case ".event":
                    throw new ReplException($"{directive} is not allowed inside a method; close method {Signature!.Name} with }} first");
                case ".class":
                    throw new ReplException($"a class cannot be declared inside a method; close method {Signature!.Name} with }} first");
                case ".vararg":
                    throw new ReplException("a member is made vararg on its header: .method public vararg ...");
                case ".typeparams":
                    throw new ReplException(".typeparams is not allowed inside a method; declare generic parameters on the header: Name<T>(...)");
                default:
                    break;
            }

            if (Member is { IsAbstract: true } && directive is ".locals" or ".try")
            {
                throw new ReplException($"abstract method {Signature!.Name} has no body; close it with }}");
            }
        }

        if (IsMethod)
        {
            switch (directive)
            {
                case ".args":
                    throw new ReplException(".args is not allowed inside a method; parameters come from the header");
                case ".vararg":
                    throw new ReplException("a session method cannot be vararg; close it with } and use .vararg on the cell");
                case ".typeparams":
                    throw new ReplException(".typeparams is not allowed inside a method; session methods are not generic");
                case ".typeargs":
                    throw new ReplException(".typeargs binds the cell's type parameters; close the method with } first");
                case ".method":
                    throw new ReplException($"a method is already open ({Signature!.Name}); close it with }} before defining another");
                case ".override":
                    throw new ReplException(".override is only valid in a method of a .class; session methods are static");
                default:
                    break;
            }
        }
        else
        {
            switch (directive)
            {
                case ".field":
                case ".pack":
                case ".size":
                case ".property":
                case ".event":
                case ".override":
                case ".custom":
                    throw new ReplException($"{directive} belongs inside a .class block (open one with .class Name {{)");
                case ".param":
                    throw new ReplException(".param belongs inside a method");
                case ".data":
                case ".namespace":
                case ".export":
                case ".vtfixup":
                    throw new ReplException(directive == ".namespace"
                        ? ".namespace is not supported; write the dotted name on .class instead (.class public Geometry.Point)"
                        : $"{directive} is not supported");
                default:
                    break;
            }
        }

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

                AcceptEntry(new CellEntry { Kind = EntryKind.Block, Source = source, Block = BlockKind.Try });
                _frames.Add(BlockKind.Try);
                return new LineResult(LineOutcome.Block, null, "try");

            case ".maxstack":
            case ".typeparams":
            case ".typeargs":
                return new LineResult(LineOutcome.Empty, null, null);

            default:
                throw new ReplException($"unknown directive '{directive}'; expected .locals, .args, .typeparams, .typeargs, .vararg, .method, .class, .field, .try, or .maxstack");
        }
    }

    private LineResult ApplyOverride(string rest, string source)
    {
        var declaration = OverrideParser.ParseInBody(rest, Context, Signature!, source);
        _entries.Add(new CellEntry { Kind = EntryKind.Override, Source = source, Override = declaration });
        return new LineResult(LineOutcome.Override, null, "overrides " + declaration.TargetDescription);
    }

    private LineResult ApplyParam(string rest, string source)
    {
        // .param [N] [= constant]: N is 1 for the first parameter and 0 for the return value.
        var s = rest.Trim();
        if (!s.StartsWith('['))
        {
            throw new ReplException("usage: .param [1] = int32(5)  (1 is the first parameter, 0 the return value)");
        }

        var close = s.IndexOf(']', StringComparison.Ordinal);
        if (close < 0 || !int.TryParse(s[1..close].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            throw new ReplException("usage: .param [1] = int32(5)  (1 is the first parameter, 0 the return value)");
        }

        var parameters = Signature!.Parameters;
        if (index < 0 || index > parameters.Count)
        {
            throw new ReplException($"{Signature.Name} has {parameters.Count} parameter(s); .param takes 0 (the return value) to {parameters.Count}");
        }

        var after = s[(close + 1)..].Trim();
        object? value = null;
        var hasDefault = false;
        if (after.Length > 0)
        {
            if (!after.StartsWith('='))
            {
                throw new ReplException($"unexpected '{after}' after .param [{index}]");
            }

            if (index == 0)
            {
                throw new ReplException("the return value cannot have a default");
            }

            value = ConstantParser.Parse(after[1..], parameters[index - 1].Type, $"parameter {index}");
            hasDefault = true;
        }

        _entries.Add(new CellEntry { Kind = EntryKind.Param, Source = source, ParamIndex = index, ParamDefault = value, ParamHasDefault = hasDefault });
        return new LineResult(LineOutcome.Param, null, hasDefault ? $"param {index} = {ConstantText.Describe(value)}" : $"param {index}");
    }

    private LineResult ApplyCustom(string rest, string source)
    {
        var attribute = CustomAttributeParser.Parse(rest, Context, source);
        // A .custom after .param [N] applies to that parameter, as do the ones that follow it;
        // any other line ends the run and a .custom applies to the method again.
        int? target = null;
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            if (entry.Kind == EntryKind.Param)
            {
                target = entry.ParamIndex;
                break;
            }

            if (entry.Kind != EntryKind.Custom)
            {
                break;
            }
        }

        _entries.Add(new CellEntry { Kind = EntryKind.Custom, Source = source, Custom = attribute, ParamIndex = target });
        return new LineResult(LineOutcome.Custom, null, "custom " + attribute.Describe() + (target is { } t ? $" on parameter {t}" : ""));
    }

    /// <summary>
    /// The scope accesses in this body are judged from.
    /// </summary>
    public AccessScope Scope => Member?.Scope ?? (Signature is null ? AccessScope.Cell : new AccessScope(null, "method " + Signature.Name));

    /// <summary>
    /// Judges every member and type an instruction mentions from this body's scope.
    /// </summary>
    private void CheckAccess(Instruction instruction)
    {
        var scope = Scope;
        switch (instruction.Operand)
        {
            case System.Reflection.FieldInfo field:
                if (field.IsLiteral && instruction.Op.Name is "ldsfld" or "ldsflda" or "stsfld")
                {
                    throw new ReplException($"{field.Name} is a literal; it has no storage, so {instruction.Op.Name} would fail with MissingFieldException at run time. Load its value instead{LiteralHint(field)}");
                }

                MemberAccess.CheckType(field.FieldType, scope, Types);
                CheckExactAccess(RuntimeFieldSignatures.TypeOf(field), scope);
                MemberAccess.CheckField(field, scope, Types);
                break;
            case ResolvedMethod { Method: not null } method:
                if (method.Declared?.ExactSymbol is { } exact)
                {
                    CheckExactAccess(exact.ReturnType, scope);
                    foreach (var parameter in exact.Parameters)
                    {
                        CheckExactAccess(parameter.Type, scope);
                    }
                }

                MemberAccess.CheckMethod(method, scope, Types);
                break;
            case Type type:
                MemberAccess.CheckType(type, scope, Types);
                break;
            default:
                break;
        }
    }

    private void CheckExactAccess(TypeSymbol? exact, AccessScope scope)
    {
        if (exact is null)
        {
            return;
        }

        foreach (var type in RuntimeSymbolTypes.Materialized(exact))
        {
            MemberAccess.CheckType(type, scope, Types);
        }
    }

    /// <summary>
    /// Judges every member access in the body again, once the declarations it referenced ahead
    /// of their headers are complete.
    /// </summary>
    /// <param name="types">The table with the final declarations.</param>
    /// <exception cref="ReplException">An access is not allowed.</exception>
    public void RecheckAccess(TypeTable types)
    {
        ArgumentNullException.ThrowIfNull(types);
        foreach (var entry in _entries)
        {
            switch (entry.Instruction?.Operand)
            {
                case System.Reflection.FieldInfo field:
                    MemberAccess.CheckField(field, Scope, types);
                    break;
                case ResolvedMethod { Method: not null } method:
                    MemberAccess.CheckMethod(method, Scope, types);
                    break;
                default:
                    break;
            }
        }
    }

    private string LiteralHint(System.Reflection.FieldInfo field)
    {
        object? value = null;
        if (Types.TryGetMembers(field.DeclaringType!, out var own))
        {
            value = own.FindField(field.Name)?.Declaration.DefaultValue;
        }
        else if (field.DeclaringType is not System.Reflection.Emit.TypeBuilder)
        {
            value = field.GetRawConstantValue();
        }

        return value switch
        {
            int i => $": ldc.i4 {i.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            long l => $": ldc.i8 {l.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            string text => $": ldstr \"{text}\"",
            Enum e => $": ldc.i4 {System.Convert.ToInt64(e, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            _ => "",
        };
    }

    private LineResult ApplyBlock(string text, string source)
    {
        if (text == "}" && _frames.Count == 0 && IsMethod)
        {
            ValidateMethodEnd();
            return new LineResult(LineOutcome.MethodEnd, null, null);
        }

        var previous = _entries.Count == 0 ? null : _entries[^1].Instruction?.Op;
        var transition = BlockTransitionParser.Parse(text, _frames, previous);
        var catchType = transition.CatchType is { } catchText
            ? catchText.Length == 0 ? typeof(object) : TypeParser.Parse(catchText, Context)
            : null;
        AcceptEntry(new CellEntry { Kind = EntryKind.Block, Source = source, Block = transition.Kind, CatchType = catchType });
        if (transition.Kind == BlockKind.End)
        {
            _frames.RemoveAt(_frames.Count - 1);
        }
        else
        {
            _frames[^1] = transition.Kind;
        }

        var message = transition.Kind == BlockKind.Catch ? "catch " + TypeNameFormatter.Pretty(catchType) : transition.Message;
        return new LineResult(LineOutcome.Block, null, message);
    }

    private List<LocalDeclaration> ParseLocals(string spec)
    {
        var scope = new RuntimeBindingScope(Context);
        var adapter = new RuntimeBindingAdapter(scope);
        return [.. VariableDeclarationParser.ParseLocals(spec, scope)
            .Select(local => new LocalDeclaration(adapter.ToType(local.Type), local.Name, local.IsPinned)
            {
                ExactType = RuntimeSymbolTypes.RequiresExact(local.Type) ? local.Type : null,
            })];
    }

    private List<ArgumentDeclaration> ParseArguments(string spec)
    {
        var scope = new RuntimeBindingScope(Context);
        var adapter = new RuntimeBindingAdapter(scope);
        return [.. VariableDeclarationParser.ParseArguments(spec, scope)
            .Select(argument => RuntimeArgumentMaterializer.Materialize(argument, adapter))];
    }

    private string DescribeLocals() =>
        string.Join(", ", _locals.Select((l, i) =>
            $"{i}:{(l.ExactType is null ? TypeNameFormatter.Pretty(l.Type) : SymbolRenderer.Pretty(l.ExactType))}"
            + $"{(l.IsPinned ? " pinned" : "")} {l.Name ?? ""}".TrimEnd()));

    private string DescribeArguments() =>
        string.Join(", ", _arguments.Select((a, i) =>
            $"{i}:{(a.ExactType is null ? TypeNameFormatter.Pretty(a.Type) : SymbolRenderer.Pretty(a.ExactType))} "
            + $"{a.Name ?? ""} = {a.ValueText}".Replace("  ", " ", StringComparison.Ordinal)));
}
