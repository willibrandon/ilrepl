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
    private bool _braceSeen;

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
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                switch (entry.Kind)
                {
                    case EntryKind.Instruction:
                        return entry.Instruction!.EndsFlow;
                    case EntryKind.Block:
                    case EntryKind.Labels:
                        return false;
                    default:
                        continue;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The parse context for the next line.
    /// </summary>
    public ParseContext Context => new(_locals, _arguments, Generics, Resolver, Methods, Types) { ThisIndex = Member?.ThisType is null ? -1 : 0 };

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

        var pending = ReferencedLabels().Where(l => !_definedLabels.Contains(l)).Distinct().ToList();
        if (pending.Count > 0)
        {
            throw new ReplException($"label{(pending.Count > 1 ? "s" : "")} referenced but never defined: {string.Join(", ", pending)} (define with 'NAME:')");
        }

        if (LastInstructionEndsFlow || Member is { IsAbstract: true })
        {
            return;
        }

        var name = Signature.Name;
        var returnType = Signature.ReturnType;
        if (returnType == typeof(void))
        {
            if (Stack.Count > 0)
            {
                throw new ReplException($"method {name} needs a ret before }}: the stack holds {Stack.Render()} but {name} returns void (pop it)");
            }

            return;
        }

        var pretty = TypeNameFormatter.Pretty(returnType);
        if (Stack.Count == 0)
        {
            throw new ReplException($"method {name} needs a ret before }}: the stack is empty but {name} returns {pretty}");
        }

        if (Stack.Count > 1 || !StackCompatibility.CanReturn(Stack.Top, returnType, Types))
        {
            throw new ReplException($"method {name} needs a ret before }}: the stack holds {Stack.Render()} but {name} returns {pretty}");
        }
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

            _entries.Add(new CellEntry { Kind = EntryKind.Labels, Source = line, Labels = labels });
            _definedLabels.UnionWith(labels);
            return new LineResult(LineOutcome.Labels, null, null);
        }

        if (Member is { IsAbstract: true })
        {
            throw new ReplException($"abstract method {Signature!.Name} has no body; close it with }}");
        }

        var context = Context;
        var instruction = InstructionParser.Parse(rest, context);
        if (instruction.Op == OpCodes.Ret)
        {
            instruction = InlineRet(instruction.Text);
        }

        if (Member?.ThisType is { IsByRef: true } && instruction.ArgumentIndex == 0 && instruction.Op.Name is "ldarga" or "ldarga.s")
        {
            throw new ReplException($"this is already a {TypeNameFormatter.Pretty(Member.ThisType)} in a struct method; use ldarg.0");
        }

        CheckInitOnlyStore(instruction);
        CheckAccess(instruction);

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

                _frames.Add(BlockKind.Try);
                _entries.Add(new CellEntry { Kind = EntryKind.Block, Source = source, Block = BlockKind.Try });
                Stack.ApplyBlock(BlockKind.Try, null);
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
        // A .custom right after .param [N] applies to that parameter; otherwise to the method.
        var last = _entries.LastOrDefault();
        var target = last?.Kind == EntryKind.Param ? last.ParamIndex : null;
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
                MemberAccess.CheckType(field.FieldType, scope, Types);
                MemberAccess.CheckField(field, scope, Types);
                break;
            case ResolvedMethod { Method: not null } method:
                MemberAccess.CheckMethod(method, scope, Types);
                break;
            case Type type:
                MemberAccess.CheckType(type, scope, Types);
                break;
            default:
                break;
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

    private void CheckInitOnlyStore(Instruction instruction)
    {
        if (instruction.Operand is not System.Reflection.FieldInfo field || !field.Attributes.HasFlag(System.Reflection.FieldAttributes.InitOnly))
        {
            return;
        }

        var name = instruction.Op.Name;
        if (name == "stsfld")
        {
            if (Signature?.Name == ".cctor" && SameDeclaringType(field.DeclaringType))
            {
                return;
            }

            throw new ReplException($"{field.Name} is a static initonly field; it can only be stored in {TypeNameFormatter.Pretty(field.DeclaringType)}'s .cctor (ECMA II.16.1.2)");
        }

        if (name != "stfld")
        {
            return;
        }

        var allowed = Signature is not null && SameDeclaringType(field.DeclaringType)
            && (Signature.Name == ".ctor" || Signature.ReturnRequiredModifiers.Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit"))
            && Stack.IsThisAt(Stack.Count - 2);
        if (!allowed)
        {
            throw new ReplException($"{field.Name} is initonly; it can only be stored through this in {TypeNameFormatter.Pretty(field.DeclaringType)}'s constructors or init accessors (ECMA II.16.1.2)");
        }
    }

    private bool SameDeclaringType(Type? declaring)
    {
        if (declaring is null || Member is null)
        {
            return false;
        }

        var definition = declaring.IsGenericType && !declaring.IsGenericTypeDefinition ? declaring.GetGenericTypeDefinition() : declaring;
        return ReferenceEquals(definition, Member.Owner);
    }

    private LineResult ApplyBlock(string text, string source)
    {
        var rest = text.StartsWith('}') ? text[1..].Trim() : text;
        if (rest.Length == 0)
        {
            if (_frames.Count == 0)
            {
                if (IsMethod)
                {
                    // The innermost open construct is the method itself. Nothing is recorded:
                    // the session commits the body once the close is validated.
                    ValidateMethodEnd();
                    return new LineResult(LineOutcome.MethodEnd, null, null);
                }

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
        if (_frames.Count > 0)
        {
            throw new ReplException("ret is not allowed inside a protected region; use leave to exit it first");
        }

        if (IsMethod)
        {
            return MethodRet(text);
        }

        if (Stack.Count > 1)
        {
            throw new ReplException($"the stack must hold 0 or 1 value at ret, but has {Stack.Count}: {Stack.Render()}  (pop, or stloc into a local)");
        }

        var top = Stack.Top;
        if (top is { IsByRef: true } || top is { IsPointer: true })
        {
            throw new ReplException($"cannot return a {StackSimulator.Name(top)} from the cell; load through it first (ldind/ldobj)");
        }

        return new Instruction
        {
            Op = OpCodes.Ret,
            Text = text,
            RetPops = Stack.Count,
            RetBox = top is { IsValueType: true } && top != typeof(NullReferenceMarker) ? top : null,
            RetNull = Stack.Count == 0,
        };
    }

    private Instruction MethodRet(string text)
    {
        var name = Signature!.Name;
        var returnType = Signature.ReturnType;
        if (returnType == typeof(void))
        {
            if (Stack.Count > 0)
            {
                throw new ReplException($"ret in void method {name} needs an empty stack but found {Stack.Render()} (pop first)");
            }

            return new Instruction { Op = OpCodes.Ret, Text = text, RetPops = 0 };
        }

        var pretty = TypeNameFormatter.Pretty(returnType);
        if (Stack.Count == 0)
        {
            throw new ReplException($"ret needs {pretty} on the stack but the stack is empty");
        }

        if (Stack.Count > 1)
        {
            throw new ReplException($"ret needs exactly one {pretty} on the stack but found {Stack.Render()} (pop, or stloc into a local)");
        }

        var top = Stack.Top;
        if (!StackCompatibility.CanReturn(top, returnType, Types))
        {
            var boxable = top is { IsValueType: true } && top != typeof(NullReferenceMarker)
                && !returnType.IsValueType && !returnType.IsByRef && !returnType.IsPointer;
            throw new ReplException($"ret needs {pretty} on the stack but found {StackSimulator.Name(top)}" + (boxable ? " (box it first)" : ""));
        }

        return new Instruction { Op = OpCodes.Ret, Text = text, RetPops = 1 };
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
            MemberAccess.CheckType(type, Scope, Types);
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
                // A zeroed element, so no constructor or type initializer of the type runs.
                value = Array.CreateInstance(type, 1).GetValue(0);
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
