using System.Globalization;
using System.Reflection;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// The REPL without a user interface: it takes lines, drives the <see cref="Session"/>, and
/// writes what happened to the <see cref="Transcript"/>. The terminal UI and batch mode both sit
/// on top of it. While a <c>.method</c> block is open, lines go to the method, and the cell
/// number advances when the block commits, as it does after a run.
/// </summary>
public sealed class ReplCore
{
    private static readonly CilTokenizer Tokenizer = new(CilVocabularyBuilder.Vocabulary);

    /// <summary>
    /// The dot-words the prompt takes as directives rather than commands.
    /// </summary>
    public static IReadOnlyList<string> Directives => ReplDirectives.Names;

    /// <summary>
    /// Initializes a REPL over a new session.
    /// </summary>
    public ReplCore() : this(new Session(), new ReplOptions())
    {
    }

    /// <summary>
    /// Initializes a REPL over the given session and options.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="options">The options.</param>
    public ReplCore(Session session, ReplOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        Session = session;
        Options = options;
        Transcript = new Transcript { MaxLines = options.MaxTranscriptLines };
    }

    /// <summary>
    /// The session.
    /// </summary>
    public Session Session { get; }

    /// <summary>
    /// The options.
    /// </summary>
    public ReplOptions Options { get; }

    /// <summary>
    /// The transcript.
    /// </summary>
    public Transcript Transcript { get; }

    /// <summary>
    /// The number of the cell being written, starting at 1. A run and a committed <c>.method</c>
    /// block each complete a cell.
    /// </summary>
    public int CellNumber => Session.Submissions + 1;

    /// <summary>
    /// The prompt for the current cell, for example <c>il[3]&gt; </c>.
    /// </summary>
    public string Prompt => $"il[{CellNumber.ToString(CultureInfo.InvariantCulture)}]> ";

    /// <summary>
    /// A snapshot of the session for the prompt and the status bar.
    /// </summary>
    public SessionStatus Status
    {
        get
        {
            var state = Session.State;
            return new SessionStatus(Prompt, CellNumber, state.Stack.Render(), state.Stack.Count, state.Locals.Count, state.InstructionCount, state.OpenBlockDepth, state.IsEmpty, Session.OpenMethod?.Name, Session.Methods.Count, Session.OpenType, Session.TypeCount, Session.Mark() with { EchoStack = Options.EchoStack, ShowTiming = Options.ShowTiming }, Session.OpenDepth);
        }
    }

    /// <summary>
    /// Completes the first word of a line.
    /// </summary>
    /// <param name="word">The word typed so far.</param>
    /// <returns>The candidates.</returns>
    public static IReadOnlyList<CompletionItem> Complete(string word) => Completer.Complete(word);

    /// <summary>
    /// Handles one line: an instruction, a directive, a command, or an empty line that runs the
    /// cell. Inside a <c>.method</c> block every line, <c>ret</c> included, goes to the method.
    /// Comments come off first, so a line that is only a comment is ignored wherever it appears,
    /// and a <c>/*</c> left open comments out the lines that follow until one closes it.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>Whether it succeeded and whether the user asked to leave.</returns>
    public HandleResult Handle(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var normalized = Session.Normalize(line);
        var commentOpen = normalized.InBlockCommentBefore;
        Transcript.Add(new TranscriptLine(LineKind.Input, [new TranscriptSpan(Prompt, SpanStyle.Prompt), .. Tokenizer.Spans(line, ref commentOpen, SpanStyle.Input)]));

        try
        {
            var operation = ReplLineDispatcher.Classify(normalized, Session.OpenMethod is not null, Session.State.HasPendingLabels, Session.State.OpenBlockDepth > 0);
            switch (operation.Kind)
            {
                case ReplLineKind.Comment:
                    return new HandleResult(true, false);
                case ReplLineKind.Blank:
                    RequireNoOpenBlock();
                    if (!Session.State.IsEmpty)
                    {
                        Run();
                    }

                    return new HandleResult(true, false);
                case ReplLineKind.Command:
                    return Command(operation.Command!, operation.Argument);
                case ReplLineKind.RetInMethod:
                    // ret returns from the method; only the closing brace ends the block.
                    Session.AddLine(normalized);
                    if (Options.EchoStack)
                    {
                        EchoStack();
                    }

                    return new HandleResult(true, false);
                case ReplLineKind.RetInline:
                    Session.AddLine(NormalizedLine.FromText("ret"));
                    Note("ret inside the cell (a forward label or a block is still open)");
                    return new HandleResult(true, false);
                case ReplLineKind.RetRuns:
                    Run();
                    return new HandleResult(true, false);
                default:
                    break;
            }

            var result = Session.AddLine(normalized);
            switch (result.Outcome)
            {
                case LineOutcome.Instruction:
                    if (Options.EchoStack)
                    {
                        EchoStack();
                    }

                    break;
                case LineOutcome.Locals:
                    Note("locals: " + result.Message);
                    break;
                case LineOutcome.Arguments:
                    Note("args: " + result.Message);
                    break;
                case LineOutcome.Block:
                    Note(result.Message ?? "block");
                    if (Options.EchoStack)
                    {
                        EchoStack();
                    }

                    break;
                case LineOutcome.TypeParameters:
                case LineOutcome.TypeArguments:
                case LineOutcome.VarArg:
                case LineOutcome.MethodStart:
                case LineOutcome.MethodEnd:
                case LineOutcome.TypeStart:
                case LineOutcome.TypeEnd:
                case LineOutcome.Field:
                case LineOutcome.Accessor:
                case LineOutcome.Override:
                case LineOutcome.Layout:
                case LineOutcome.Custom:
                case LineOutcome.Param:
                    Note(result.Message ?? "");
                    break;
                default:
                    break;
            }

            return new HandleResult(true, false);
        }
        catch (ReplException ex)
        {
            Session.Forget(normalized);
            Error(ex.Message);
            return new HandleResult(false, false);
        }
        catch (Exception ex) when (ex is not (CellException or OperationCanceledException))
        {
            // A line must never take the session down with it; the host keeps serving.
            Session.Forget(normalized);
            Error("unexpected " + ex.GetType().Name + ": " + ex.Message);
            return new HandleResult(false, false);
        }
        catch (CellException ex)
        {
            var inner = ex.InnerException ?? ex;
            Transcript.Add(new TranscriptLine(LineKind.Error,
            [
                new TranscriptSpan("  threw ", SpanStyle.Error),
                new TranscriptSpan(inner.GetType().FullName ?? inner.GetType().Name, SpanStyle.Type),
                new TranscriptSpan(": " + inner.Message),
            ]));
            if (inner.InnerException is { } deeper)
            {
                Transcript.Add(LineKind.Error, "    caused by " + deeper.GetType().Name + ": " + deeper.Message, SpanStyle.Dim);
            }

            return new HandleResult(false, false);
        }
    }

    /// <summary>
    /// Withdraws the lines accepted since a mark was taken, so a block a line of which was refused
    /// can come back to the editor whole and be sent again from where the session stood before it.
    /// Nothing that ran, committed, or was discarded since the mark is undone; when any of that
    /// happened, nothing is withdrawn and the result says so.
    /// </summary>
    /// <param name="mark">The mark to return to.</param>
    /// <returns>Whether the session is back at the mark.</returns>
    public HandleResult Rollback(SessionMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        var method = Session.OpenMethod?.Name;
        var type = Session.OpenType;
        if (!Session.Rollback(mark))
        {
            Note("nothing withdrawn: the session has run, committed, or discarded something since the block began");
            return new HandleResult(false, false);
        }

        // A toggle the block carried, .quiet or .time, goes back with it: the block is sent again whole.
        Options.EchoStack = mark.EchoStack;
        Options.ShowTiming = mark.ShowTiming;

        if (type is not null && Session.OpenType is null)
        {
            Note($"class {type} abandoned; the block is back in the editor");
        }
        else if (method is not null && Session.OpenMethod is null)
        {
            Note($"method {method} abandoned; the block is back in the editor");
        }
        else
        {
            Note("lines withdrawn; the block is back in the editor");
        }

        return new HandleResult(true, false);
    }

    private void RequireNoOpenBlock()
    {
        if (Session.OpenMethod is { } open)
        {
            throw new ReplException($"method {open.Name} is still open; close it with }}");
        }

        if (Session.OpenType is { } type)
        {
            throw new ReplException($"class {type} is still open; close it with }}");
        }
    }

    private void Run()
    {
        RequireNoOpenBlock();
        if (Session.State.IsEmpty && Session.State.Stack.Count == 0)
        {
            Note("(empty cell)");
            return;
        }

        var result = Session.Run();
        AddOutput(result.StandardOutput, SpanStyle.Output);
        AddOutput(result.StandardError, SpanStyle.Error);

        var spans = new List<TranscriptSpan> { new("  = ", SpanStyle.Dim) };
        if (result.IsVoid)
        {
            spans.Add(new TranscriptSpan("(void)", SpanStyle.Dim));
        }
        else
        {
            spans.AddRange(ValueFormatter.FormatWithType(result.Value));
        }

        if (Options.ShowTiming)
        {
            spans.Add(new TranscriptSpan("   " + Elapsed(result.Elapsed), SpanStyle.Dim));
        }

        Transcript.Add(new TranscriptLine(LineKind.Result, spans));
    }

    private void AddOutput(string text, SpanStyle style)
    {
        if (text.Length == 0)
        {
            return;
        }

        foreach (var line in text.TrimEnd('\n', '\r').Split('\n'))
        {
            Transcript.Add(LineKind.Output, line.TrimEnd('\r'), style);
        }
    }

    private static string Elapsed(TimeSpan t) =>
        t.TotalMilliseconds >= 1
            ? t.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) + " ms"
            : t.TotalMicroseconds.ToString("0.#", CultureInfo.InvariantCulture) + " µs";

    private void EchoStack()
    {
        var items = Session.State.Stack.Items;
        var spans = new List<TranscriptSpan> { new("  ┊ ", SpanStyle.Dim) };
        if (items.Count == 0)
        {
            spans.Add(new TranscriptSpan("[]", SpanStyle.Dim));
        }
        else
        {
            spans.Add(new TranscriptSpan("[", SpanStyle.Dim));
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    spans.Add(new TranscriptSpan(", ", SpanStyle.Dim));
                }

                spans.Add(new TranscriptSpan(StackSimulator.Name(items[i]), i == items.Count - 1 ? SpanStyle.TopType : SpanStyle.Type));
            }

            spans.Add(new TranscriptSpan("]", SpanStyle.Dim));
            if (items.Count > 1)
            {
                spans.Add(new TranscriptSpan(" ◂ top", SpanStyle.Dim));
            }
        }

        Transcript.Add(new TranscriptLine(LineKind.Stack, spans));
    }

    private void Note(string text) => Transcript.Add(LineKind.Info, "  " + text, SpanStyle.Dim);

    /// <summary>
    /// Adds a listing line coloured by the tokenizer, so it reads as it would at the prompt.
    /// </summary>
    private void Listing(string text) => Transcript.Add(new TranscriptLine(LineKind.Listing, Tokenizer.Spans(text)));

    /// <summary>
    /// Adds a block row of a listing: the offset column blank, then the row indented inside its region.
    /// </summary>
    private void BlockRow(int indent, string text) =>
        Transcript.Add(new TranscriptLine(LineKind.Listing, [new TranscriptSpan("       ", SpanStyle.Dim), .. Tokenizer.Spans(new string(' ', indent * 2) + text)]));

    /// <summary>
    /// Adds an instruction row of a listing: the offset column, the instruction coloured by the
    /// tokenizer and padded to a fixed width, then the stack column.
    /// </summary>
    private void InstructionRow(string offset, int indent, string text, string stack)
    {
        var padding = Math.Max(0, 40 - (indent * 2) - text.Length);
        var spans = new List<TranscriptSpan> { new(offset, SpanStyle.Dim) };
        spans.AddRange(Tokenizer.Spans(new string(' ', indent * 2) + text));
        if (padding > 0)
        {
            spans.Add(new TranscriptSpan(new string(' ', padding)));
        }

        spans.Add(new TranscriptSpan(" " + stack, SpanStyle.Dim));
        Transcript.Add(new TranscriptLine(LineKind.Listing, spans));
    }

    private void Error(string message)
    {
        var lines = message.Split('\n');
        Transcript.Add(new TranscriptLine(LineKind.Error, [new TranscriptSpan("  error: ", SpanStyle.Error), new TranscriptSpan(lines[0])]));
        foreach (var extra in lines.Skip(1))
        {
            Transcript.Add(LineKind.Error, "  " + extra, SpanStyle.Dim);
        }
    }

    private HandleResult Command(string command, string argument)
    {
        if (SessionTransitionRules.Of(command) == SessionTransition.Unknown)
        {
            throw new ReplException($"unknown command '{command}' (.help lists them)");
        }

        switch (command)
        {
            case ".help":
            case ".h":
            case ".?":
                foreach (var l in HelpText.Lines())
                {
                    Transcript.Add(l);
                }

                return new HandleResult(true, false);

            case ".quit":
            case ".exit":
            case ".q":
                return new HandleResult(true, true);

            case ".run":
                Run();
                return new HandleResult(true, false);

            case ".ops":
                ListOpcodes(argument);
                return new HandleResult(true, false);

            case ".show":
            case ".list":
            case ".ls":
                if (Session.OpenType is not null)
                {
                    ShowType();
                }
                else if (Session.OpenMethod is { } shown)
                {
                    ShowMethod(shown);
                }
                else
                {
                    Show();
                }

                return new HandleResult(true, false);

            case ".dis":
            case ".disassemble":
                if (argument.Length == 0)
                {
                    throw new ReplException("usage: .dis <method reference>  e.g. .dis instance string [System.Runtime]System.String::Trim()  or  .dis Fib");
                }

                Disassemble(argument);
                return new HandleResult(true, false);

            case ".undo":
            case ".u":
            {
                var wasOpen = Session.OpenMethod;
                var wasType = Session.OpenType;
                if (!Session.Undo())
                {
                    Note("nothing to undo");
                    return new HandleResult(true, false);
                }

                if (wasType is not null && Session.OpenType is null)
                {
                    Note($"class {wasType} abandoned");
                }
                else if (wasOpen is not null && Session.OpenMethod is null)
                {
                    Note($"method {wasOpen.Name} abandoned");
                }

                EchoStack();
                return new HandleResult(true, false);
            }

            case ".clear":
                if (Session.OpenMethod is { } abandoned)
                {
                    var owner = Session.OpenType;
                    Session.AbandonMethod();
                    Note(owner is null ? $"method {abandoned.Name} abandoned" : $"method {abandoned.Name} abandoned; class {owner} is still open");
                    return new HandleResult(true, false);
                }

                if (Session.OpenType is { } abandonedType)
                {
                    Session.AbandonType();
                    Note($"class {abandonedType} abandoned");
                    return new HandleResult(true, false);
                }

                Session.ClearCell();
                Note("cell cleared (declarations kept)");
                return new HandleResult(true, false);

            case ".reset":
                Session.Reset();
                Note("cell, declarations, methods, and types cleared");
                return new HandleResult(true, false);

            case ".types":
                foreach (var type in Session.Types)
                {
                    ListType(type.Declaration, 1);
                }

                if (Session.Types.Count == 0)
                {
                    Note("no types");
                }

                return new HandleResult(true, false);

            case ".methods":
                foreach (var method in Session.Methods)
                {
                    Transcript.Add(LineKind.Listing, "  " + method.Signature.DescribeWithNames(), SpanStyle.Default);
                }

                if (Session.Methods.Count == 0)
                {
                    Note("no methods");
                }

                return new HandleResult(true, false);

            case ".stack":
                EchoStack();
                return new HandleResult(true, false);

            case ".time":
                Options.ShowTiming = argument switch { "on" => true, "off" => false, _ => !Options.ShowTiming };
                Note("timing " + (Options.ShowTiming ? "on" : "off"));
                return new HandleResult(true, false);

            case ".quiet":
                Options.EchoStack = argument switch { "on" => false, "off" => true, _ => !Options.EchoStack };
                Note("stack echo " + (Options.EchoStack ? "on" : "off"));
                return new HandleResult(true, false);

            case ".load":
                if (argument.Length == 0)
                {
                    throw new ReplException("usage: .load <assembly name | path.dll>");
                }

                {
                    var assembly = Session.Resolver.Load(argument);
                    Session.AdvanceGeneration();
                    int count;
                    try
                    {
                        count = assembly.GetExportedTypes().Length;
                    }
                    catch (Exception ex) when (ex is System.Reflection.ReflectionTypeLoadException or FileNotFoundException or NotSupportedException)
                    {
                        count = -1;
                    }

                    Note($"loaded {assembly.GetName().Name} {assembly.GetName().Version}" + (count >= 0 ? $" ({count} public types)" : ""));
                }

                return new HandleResult(true, false);

            case ".assemblies":
                foreach (var assembly in Session.Resolver.LoadedAssemblies)
                {
                    Transcript.Add(LineKind.Listing, "  " + assembly.GetName().Name + "  " + (assembly.IsDynamic ? "(dynamic)" : assembly.Location), SpanStyle.Default);
                }

                if (Session.Resolver.LoadedAssemblies.Count == 0)
                {
                    Note("no assemblies loaded beyond the framework");
                }

                return new HandleResult(true, false);

            case ".save":
                if (argument.Length == 0)
                {
                    throw new ReplException("usage: .save <path.dll>");
                }

                RequireNoOpenBlock();
                Session.Save(argument);
                {
                    var parts = new List<string> { "IlRepl.Cell.Run" };
                    if (Session.Methods.Count > 0)
                    {
                        parts.Add($"{Session.Methods.Count} method{(Session.Methods.Count == 1 ? "" : "s")}");
                    }

                    if (Session.TypeCount > 0)
                    {
                        parts.Add($"{Session.TypeCount} type{(Session.TypeCount == 1 ? "" : "s")}");
                    }

                    var with = parts.Count switch
                    {
                        1 => parts[0],
                        2 => parts[0] + " and " + parts[1],
                        _ => string.Join(", ", parts.Take(parts.Count - 1)) + ", and " + parts[^1],
                    };
                    Note($"wrote {Path.GetFullPath(argument)} with {with}");
                }

                return new HandleResult(true, false);

            case ".il":
            {
                var commentOpen = false;
                foreach (var l in Session.ToIlAsm().TrimEnd().Split('\n'))
                {
                    Transcript.Add(new TranscriptLine(LineKind.Listing, Tokenizer.Spans(l.TrimEnd('\r'), ref commentOpen)));
                }

                return new HandleResult(true, false);
            }

            default:
                throw new ReplException($"unknown command '{command}' (.help lists them)");
        }
    }

    private void ListOpcodes(string filter)
    {
        var names = OpcodeTable.Names.Where(n => !OpcodeTable.IsReserved(n));
        if (filter.Length > 0)
        {
            names = names.Where(n =>
                n.Contains(filter, StringComparison.Ordinal)
                || OpcodeTable.Describe(OpcodeTable.ByName[n]).Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        var list = names.ToList();
        if (list.Count == 0)
        {
            Note("no opcodes match");
            return;
        }

        foreach (var name in list)
        {
            var op = OpcodeTable.ByName[name];
            Transcript.Add(new TranscriptLine(LineKind.Listing,
            [
                new TranscriptSpan("  " + name.PadRight(16), SpanStyle.Opcode),
                new TranscriptSpan(OpcodeTable.StackTransition(op).PadRight(24), SpanStyle.Dim),
                new TranscriptSpan(OpcodeTable.Describe(op)),
            ]));
        }

        Note($"{list.Count} opcode{(list.Count == 1 ? "" : "s")}");
    }

    private void Show() => ListBody(Session.State, showArguments: true, "(empty cell)");

    private void Disassemble(string spec)
    {
        var resolved = MemberResolver.ResolveMethod(spec, Session.InspectionContext, wantConstructor: false);
        MethodBase method;
        if (resolved.Definition is { } definition)
        {
            var record = Session.Methods.FirstOrDefault(m => m.Signature.Name == definition.Name && SignatureIdentity.Same(m.Signature, definition))
                ?? throw new ReplException($"no method '{definition.Name}' in the session");
            method = record.Version.Body;
        }
        else if (resolved.Method is { } loaded && loaded.DeclaringType is not System.Reflection.Emit.TypeBuilder && resolved.Declared is null)
        {
            method = loaded;
        }
        else
        {
            throw new ReplException($"{TypeNameFormatter.Pretty(resolved.DeclaringType)}::{resolved.Declared?.Name ?? resolved.Method?.Name} belongs to the class being written and has no compiled body; close it with }} first");
        }

        var listing = MethodDisassembler.Disassemble(method, Session);
        var column = StackAnalysis.Run(listing);
        Listing("  " + listing.Header + " {");
        Listing("  .maxstack " + listing.MaxStack.ToString(CultureInfo.InvariantCulture));
        if (listing.Locals.Count > 0)
        {
            Listing((listing.InitLocals ? "  .locals init (" : "  .locals (") + string.Join(", ", listing.Locals.Select((l, i) => $"{IlSignatureRenderer.IlAsmNamed(l)} V_{i.ToString(CultureInfo.InvariantCulture)}")) + ")");
        }

        var indent = 0;
        for (var i = 0; i < listing.Entries.Count; i++)
        {
            var entry = listing.Entries[i];
            switch (entry.Kind)
            {
                case DisassembledEntryKind.Label:
                    Listing(entry.Label + ":");
                    break;
                case DisassembledEntryKind.Block:
                {
                    var text = entry.Block switch
                    {
                        BlockKind.Try => ".try {",
                        BlockKind.Catch => "} catch " + entry.CatchText + " {",
                        BlockKind.Filter => "} filter {",
                        BlockKind.FilterHandler => "} handler {",
                        BlockKind.Finally => "} finally {",
                        BlockKind.Fault => "} fault {",
                        _ => "}",
                    };
                    if (entry.Block != BlockKind.Try)
                    {
                        indent = Math.Max(0, indent - 1);
                    }

                    BlockRow(indent, text);
                    if (entry.Block != BlockKind.End)
                    {
                        indent++;
                    }

                    break;
                }

                default:
                    InstructionRow("  " + entry.Offset.ToString("x4", CultureInfo.InvariantCulture) + " ", indent, entry.DisplayText, column[i] ?? "");
                    break;
            }
        }

        Listing("  }");
        Note($"code size {listing.CodeSize} (0x{listing.CodeSize:x})");
        foreach (var note in listing.Notes)
        {
            Note(note);
        }

        foreach (var problem in listing.Problems)
        {
            Note("problem: " + problem);
        }
    }

    private void ShowType()
    {
        var lines = Session.DescribeOpenType();
        foreach (var l in lines)
        {
            Listing("  " + l);
        }

        if (Session.OpenMethod is { } open)
        {
            Listing("    .method " + open.DescribeMember() + " {");
            ListBody(Session.State, showArguments: false, "(empty method)");
        }
        else if (lines.Count == 1)
        {
            Note("(empty class)");
        }
    }

    private void ListType(TypeDeclaration type, int level)
    {
        var indent = new string(' ', level * 2);
        var header = type.KindWord + " " + type.DisplayName;
        if (type.BaseType is not null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType) && type.BaseType != typeof(Enum))
        {
            header += " extends " + TypeNameFormatter.Pretty(type.BaseType);
        }

        if (type.Interfaces.Count > 0)
        {
            header += " implements " + string.Join(", ", type.Interfaces.Select(TypeNameFormatter.Pretty));
        }

        Transcript.Add(LineKind.Listing, indent + header, SpanStyle.Label);
        foreach (var field in type.Fields)
        {
            Transcript.Add(LineKind.Listing, indent + "    " + field.Describe(), SpanStyle.Default);
        }

        foreach (var method in type.Methods)
        {
            Transcript.Add(LineKind.Listing, indent + "    " + method.Describe(), SpanStyle.Default);
        }

        foreach (var property in type.Properties)
        {
            Transcript.Add(LineKind.Listing, indent + "    " + property.Describe(), SpanStyle.Default);
        }

        foreach (var evt in type.Events)
        {
            Transcript.Add(LineKind.Listing, indent + "    " + evt.Describe(), SpanStyle.Default);
        }

        foreach (var nested in type.NestedTypes)
        {
            ListType(nested, level + 2);
        }
    }

    private void ShowMethod(MethodSignature open)
    {
        Listing("  .method " + open.DescribeWithNames() + " {");
        ListBody(Session.State, showArguments: false, "(empty method)");
    }

    private void ListBody(CellState state, bool showArguments, string emptyNote)
    {
        if (state.Locals.Count > 0)
        {
            Listing("  .locals init (" + string.Join(", ", state.Locals.Select((l, i) => $"{TypeNameFormatter.Pretty(l.Type)} {l.Name ?? "V_" + i.ToString(CultureInfo.InvariantCulture)}")) + ")");
        }

        if (showArguments && state.Arguments.Count > 0)
        {
            Listing("  .args (" + string.Join(", ", state.Arguments.Select(a => $"{TypeNameFormatter.Pretty(a.Type)} {a.Name} = {a.ValueText}")) + ")");
        }

        if (state.IsEmpty)
        {
            Note(emptyNote);
            return;
        }

        var simulator = new StackSimulator();
        var context = state.Context;
        var index = 0;
        var indent = 0;
        foreach (var entry in state.Entries)
        {
            foreach (var l in entry.Labels)
            {
                Listing(l + ":");
            }

            switch (entry.Kind)
            {
                case EntryKind.Instruction:
                    simulator.Apply(entry.Instruction!, context);
                    InstructionRow("  " + index.ToString("D3", CultureInfo.InvariantCulture) + "  ", indent, entry.Instruction!.Text, simulator.Render());
                    index++;
                    break;
                case EntryKind.Block:
                    simulator.ApplyBlock(entry.Block!.Value, entry.CatchType);
                    var text = entry.Block switch
                    {
                        BlockKind.Try => ".try {",
                        BlockKind.Catch => "} catch " + TypeNameFormatter.Pretty(entry.CatchType) + " {",
                        BlockKind.Filter => "} filter {",
                        BlockKind.FilterHandler => "} handler {",
                        BlockKind.Finally => "} finally {",
                        BlockKind.Fault => "} fault {",
                        _ => "}",
                    };
                    if (entry.Block != BlockKind.Try)
                    {
                        indent = Math.Max(0, indent - 1);
                    }

                    BlockRow(indent, text);
                    if (entry.Block != BlockKind.End)
                    {
                        indent++;
                    }

                    break;
                default:
                    break;
            }
        }
    }
}
