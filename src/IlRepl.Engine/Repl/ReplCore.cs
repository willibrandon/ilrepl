using System.Globalization;
using IlRepl.Engine;
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
    private static readonly string[] Directives = [".locals", ".args", ".typeparams", ".typeargs", ".vararg", ".method", ".try", ".maxstack"];

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
            return new SessionStatus(Prompt, CellNumber, state.Stack.Render(), state.Stack.Count, state.Locals.Count, state.InstructionCount, state.OpenBlockDepth, state.IsEmpty, Session.OpenMethod?.Name, Session.Methods.Count);
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
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>Whether it succeeded and whether the user asked to leave.</returns>
    public HandleResult Handle(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = line.Trim();
        Transcript.Add(new TranscriptLine(LineKind.Input, [new TranscriptSpan(Prompt, SpanStyle.Prompt), new TranscriptSpan(line, SpanStyle.Input)]));

        try
        {
            if (text.Length == 0)
            {
                RequireNoOpenMethod();
                if (!Session.State.IsEmpty)
                {
                    Run();
                }

                return new HandleResult(true, false);
            }

            if (text.StartsWith('.') && !IsDirective(text))
            {
                return Command(text);
            }

            if (text == "ret" || text.StartsWith("ret ", StringComparison.Ordinal) || text.StartsWith("ret//", StringComparison.Ordinal))
            {
                if (Session.OpenMethod is not null)
                {
                    // ret returns from the method; only the closing brace ends the block.
                    Session.AddLine(line);
                    if (Options.EchoStack)
                    {
                        EchoStack();
                    }

                    return new HandleResult(true, false);
                }

                if (Session.State.HasPendingLabels || Session.State.OpenBlockDepth > 0)
                {
                    var inline = Session.AddLine("ret");
                    Note("ret inside the cell (a forward label or a block is still open)");
                    _ = inline;
                    return new HandleResult(true, false);
                }

                Run();
                return new HandleResult(true, false);
            }

            var result = Session.AddLine(line);
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
                    Note(result.Message ?? "");
                    break;
                default:
                    break;
            }

            return new HandleResult(true, false);
        }
        catch (ReplException ex)
        {
            Error(ex.Message);
            return new HandleResult(false, false);
        }
        catch (Exception ex) when (ex is not (CellException or OperationCanceledException))
        {
            // A line must never take the session down with it; the host keeps serving.
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

    private static bool IsDirective(string text)
    {
        foreach (var d in Directives)
        {
            if (text.StartsWith(d, StringComparison.Ordinal) && (text.Length == d.Length || !char.IsLetter(text[d.Length])))
            {
                return true;
            }
        }

        return false;
    }

    private void RequireNoOpenMethod()
    {
        if (Session.OpenMethod is { } open)
        {
            throw new ReplException($"method {open.Name} is still open; close it with }}");
        }
    }

    private void Run()
    {
        RequireNoOpenMethod();
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

    private void Error(string message)
    {
        var lines = message.Split('\n');
        Transcript.Add(new TranscriptLine(LineKind.Error, [new TranscriptSpan("  error: ", SpanStyle.Error), new TranscriptSpan(lines[0])]));
        foreach (var extra in lines.Skip(1))
        {
            Transcript.Add(LineKind.Error, "  " + extra, SpanStyle.Dim);
        }
    }

    private HandleResult Command(string line)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        var command = space < 0 ? line : line[..space];
        var argument = space < 0 ? "" : line[(space + 1)..].Trim();

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
                if (Session.OpenMethod is { } shown)
                {
                    ShowMethod(shown);
                }
                else
                {
                    Show();
                }

                return new HandleResult(true, false);

            case ".undo":
            case ".u":
            {
                var wasOpen = Session.OpenMethod;
                if (!Session.Undo())
                {
                    Note("nothing to undo");
                    return new HandleResult(true, false);
                }

                if (wasOpen is not null && Session.OpenMethod is null)
                {
                    Note($"method {wasOpen.Name} abandoned");
                }

                EchoStack();
                return new HandleResult(true, false);
            }

            case ".clear":
                if (Session.OpenMethod is { } abandoned)
                {
                    Session.AbandonMethod();
                    Note($"method {abandoned.Name} abandoned");
                    return new HandleResult(true, false);
                }

                Session.ClearCell();
                Note("cell cleared (declarations kept)");
                return new HandleResult(true, false);

            case ".reset":
                Session.Reset();
                Note("cell, declarations, and methods cleared");
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

                RequireNoOpenMethod();
                Session.Save(argument);
                {
                    var count = Session.Methods.Count;
                    var methods = count == 0 ? "" : $" and {count} method{(count == 1 ? "" : "s")}";
                    Note($"wrote {Path.GetFullPath(argument)} with IlRepl.Cell.Run{methods}");
                }

                return new HandleResult(true, false);

            case ".il":
                foreach (var l in Session.ToIlAsm().TrimEnd().Split('\n'))
                {
                    Transcript.Add(LineKind.Listing, l.TrimEnd('\r'), SpanStyle.Default);
                }

                return new HandleResult(true, false);

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

    private void ShowMethod(MethodSignature open)
    {
        Transcript.Add(LineKind.Listing, "  .method " + open.DescribeWithNames() + " {", SpanStyle.Label);
        ListBody(Session.State, showArguments: false, "(empty method)");
    }

    private void ListBody(CellState state, bool showArguments, string emptyNote)
    {
        if (state.Locals.Count > 0)
        {
            Transcript.Add(new TranscriptLine(LineKind.Listing,
            [
                new TranscriptSpan("  .locals init (", SpanStyle.Dim),
                new TranscriptSpan(string.Join(", ", state.Locals.Select((l, i) => $"{TypeNameFormatter.Pretty(l.Type)} {l.Name ?? "V_" + i.ToString(CultureInfo.InvariantCulture)}")), SpanStyle.Type),
                new TranscriptSpan(")", SpanStyle.Dim),
            ]));
        }

        if (showArguments && state.Arguments.Count > 0)
        {
            Transcript.Add(new TranscriptLine(LineKind.Listing,
            [
                new TranscriptSpan("  .args (", SpanStyle.Dim),
                new TranscriptSpan(string.Join(", ", state.Arguments.Select(a => $"{TypeNameFormatter.Pretty(a.Type)} {a.Name} = {a.ValueText}")), SpanStyle.Type),
                new TranscriptSpan(")", SpanStyle.Dim),
            ]));
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
                Transcript.Add(LineKind.Listing, l + ":", SpanStyle.Label);
            }

            switch (entry.Kind)
            {
                case EntryKind.Instruction:
                    simulator.Apply(entry.Instruction!, context);
                    Transcript.Add(new TranscriptLine(LineKind.Listing,
                    [
                        new TranscriptSpan("  " + index.ToString("D3", CultureInfo.InvariantCulture) + "  ", SpanStyle.Dim),
                        new TranscriptSpan(new string(' ', indent * 2) + entry.Instruction!.Text.PadRight(40 - (indent * 2))),
                        new TranscriptSpan(" " + simulator.Render(), SpanStyle.Dim),
                    ]));
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

                    Transcript.Add(LineKind.Listing, "       " + new string(' ', indent * 2) + text, SpanStyle.Label);
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
