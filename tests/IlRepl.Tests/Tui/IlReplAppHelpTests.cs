using System.Text;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using Hex1b.Nodes;
using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Drives read-only contextual help, keyboard actions, and resizing through the real terminal and host engine.
/// </summary>
[TestClass]
public sealed class IlReplAppHelpTests
{
    /// <summary>
    /// Supplies cancellation for host startup, terminal input, and engine settlement.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Keys queued behind F1 use read-only help immediately and acknowledge navigation only after its action finishes.
    /// </summary>
    /// <param name="loadAssembly">Whether a real host assembly load occurs between queued Tab and Enter.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task QueuedOpen_ProtectsTextUndoAndAcknowledgesCompletedActions(bool loadAssembly)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        PromptState prompt = null!;
        var opened = new TaskCompletionSource<(string Url, int Sequence)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishedSequence = 0;
        var assemblyLoaded = false;
        var adapter = new ScriptedPresentationAdapter(90, 24);
        await using var terminal = IlReplApp.Configure(Hex1b.Hex1bTerminal.CreateBuilder(), engine, transcript,
            onPrompt: state =>
            {
                prompt = state;
                state.OpenDocumentation = url => opened.TrySetResult((url, Volatile.Read(ref publishedSequence)));
                state.DocumentationTargetChanged = (_, sequence, active) =>
                {
                    if (loadAssembly && active && sequence == 1 && state.Help is { Scroll: > 0 } && !assemblyLoaded)
                    {
                        // Tab has scrolled to the link but has not acknowledged its input yet.
                        assemblyLoaded = true;
                        var previous = engine.AssemblyVersion;
                        var loaded = engine.HandleAsync(".load " + SampleHost.Samples.GreeterDll, ct)
                            .WaitAsync(AppTest.Timeout, ct).GetAwaiter().GetResult();
                        Assert.IsTrue(loaded.Succeeded);
                        // The load reply and the assembly notification travel independently over RPC.
                        engine.WaitForAssembliesAsync(previous, ct)
                            .WaitAsync(AppTest.Timeout, ct).GetAwaiter().GetResult();
                        Assert.IsGreaterThan(previous, engine.AssemblyVersion);
                    }

                    Volatile.Write(ref publishedSequence, sequence);
                    if (active && sequence >= 3)
                    {
                        acknowledged.TrySetResult(opened.Task.IsCompletedSuccessfully);
                    }
                };
            }).WithPresentation(adapter).Build();

        using var terminalCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = terminal.RunAsync(terminalCancellation.Token);
        try
        {
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
            await auto.WaitUntilTextAsync("il[1]>");
            await auto.TypeAsync("constr", ct: ct);
            await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("opcodes") && snapshot.ContainsText("il[1]> constr")
                && prompt.Text == "constr" && prompt.Analysis is not null && prompt.Analyzer?.IsPending == false,
                description: "the complete prompt, opcode palette, and analysis are ready");
            if (loadAssembly)
            {
                adapter.Resize(90, 8);
                await auto.WaitUntilAsync(snapshot => snapshot.Height == 8 && snapshot.ContainsText("il[1]> constr"),
                    description: "the compact viewport will require Tab to scroll to the documentation link");
            }

            var text = prompt.Text;
            var caret = prompt.Editor.Cursor.Position;
            var version = prompt.Editor.Document.Version;
            var undo = prompt.Editor.History.UndoCount;
            var redo = prompt.Editor.History.RedoCount;
            var selection = prompt.SelectedIndex;

            // Keep one input batch to exercise keys already queued when F1 changes the active widget.
            await adapter.SendAsync(Encoding.UTF8.GetBytes("\u001bOPignored\u007f\u001b[3~\u001a\t\r"));
            await auto.WaitUntilAsync(snapshot => snapshot.GetLine(0).StartsWith("help ·", StringComparison.Ordinal)
                && opened.Task.IsCompleted && acknowledged.Task.IsCompleted,
                description: "queued Enter opens documentation before its acknowledgment");
            var activation = await opened.Task;
            Assert.IsTrue(await acknowledged.Task);

            Assert.AreEqual(InstructionReference.For("constrained.").DocumentationUrl, activation.Url);
            Assert.IsLessThan(3, activation.Sequence, "Enter must finish its action before publishing its acknowledgment.");
            Assert.AreEqual(3, Volatile.Read(ref publishedSequence), "Only F1, Tab, and Enter are acknowledged help actions.");
            Assert.AreEqual(loadAssembly, assemblyLoaded);
            Assert.AreEqual(text, prompt.Text);
            Assert.AreEqual(caret, prompt.Editor.Cursor.Position);
            Assert.AreEqual(version, prompt.Editor.Document.Version);
            Assert.AreEqual(undo, prompt.Editor.History.UndoCount);
            Assert.AreEqual(redo, prompt.Editor.History.RedoCount);
            Assert.AreEqual(selection, prompt.SelectedIndex);
            await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
            await run;
        }
        finally
        {
            await terminalCancellation.CancelAsync();
            try
            {
                await run;
            }
            catch (OperationCanceledException) when (terminalCancellation.IsCancellationRequested)
            {
            }

            await IlReplApp.SettleAsync(prompt);
        }

        Assert.IsEmpty(AppTest.Echoes(transcript));
    }

    /// <summary>
    /// Typing queued immediately behind the help-closing key reaches the editor with its original undo history intact.
    /// </summary>
    /// <param name="toggle">Whether F1 closes help instead of Escape.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task QueuedClose_ReturnsFollowingTextToEditor(bool toggle)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        PromptState prompt = null!;
        Hex1bApp app = null!;
        var publishedSequence = 0;
        var adapter = new ScriptedPresentationAdapter(80, 24);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript, onApp: value => app = value,
            onPrompt: state =>
            {
                prompt = state;
                state.DocumentationTargetChanged = (_, sequence, _) => Volatile.Write(ref publishedSequence, sequence);
            }).WithPresentation(adapter).Build();

        using var terminalCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = terminal.RunAsync(terminalCancellation.Token);
        try
        {
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
            await auto.WaitUntilTextAsync("il[1]>");
            await auto.TypeAsync("constr", ct: ct);
            await auto.WaitUntilTextAsync("opcodes");
            await auto.KeyAsync(Hex1bKey.F1, ct: ct);
            await auto.WaitUntilAsync(snapshot => snapshot.GetLine(0).StartsWith("help ·", StringComparison.Ordinal)
                || snapshot.ContainsText("APPLICATION ERROR"));
            if (IlReplApp.FindNode<RescueNode>(app) is { HasError: true } rescue)
            {
                Assert.Fail(rescue.ErrorPhase + ": " + rescue.Exception);
            }

            var undo = prompt.Editor.History.UndoCount;
            Assert.IsGreaterThan(0, undo);

            using var queued = await new Hex1bTerminalInputSequenceBuilder().Key(toggle ? Hex1bKey.F1 : Hex1bKey.Escape)
                .Type("ained. ").Build().ApplyAsync(terminal, ct);
            await auto.WaitUntilAsync(snapshot => !snapshot.GetLine(0).StartsWith("help ·", StringComparison.Ordinal)
                && prompt.Text == "constrained. ",
                description: "all text queued behind the closing key reaches the editor");

            Assert.AreEqual(2, Volatile.Read(ref publishedSequence));
            Assert.AreEqual("constrained. ".Length, prompt.Editor.Cursor.Position.Value);
            Assert.IsGreaterThanOrEqualTo(undo, prompt.Editor.History.UndoCount);
            await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
            await run;
        }
        finally
        {
            await terminalCancellation.CancelAsync();
            try
            {
                await run;
            }
            catch (OperationCanceledException) when (terminalCancellation.IsCancellationRequested)
            {
            }

            await IlReplApp.SettleAsync(prompt);
        }

        Assert.IsEmpty(AppTest.Echoes(transcript));
    }

    /// <summary>
    /// Bracketed paste in help leaves the document untouched and releases the next queued help key.
    /// </summary>
    [TestMethod]
    public async Task HelpPaste_PreservesEditorAndReleasesQueuedKeys()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        PromptState prompt = null!;
        var publishedSequence = 0;
        var adapter = new ScriptedPresentationAdapter(80, 24);
        await using var terminal = IlReplApp.Configure(Hex1b.Hex1bTerminal.CreateBuilder(), engine, transcript,
            onPrompt: state =>
            {
                prompt = state;
                state.DocumentationTargetChanged = (_, sequence, _) => Volatile.Write(ref publishedSequence, sequence);
            }).WithPresentation(adapter).Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("constr", ct: ct);
        await auto.WaitUntilTextAsync("opcodes");
        await auto.KeyAsync(Hex1bKey.F1, ct: ct);
        await auto.WaitUntilAsync(snapshot => snapshot.GetLine(0).StartsWith("help ·", StringComparison.Ordinal));
        var caret = prompt.Editor.Cursor.Position;
        var version = prompt.Editor.Document.Version;
        var undo = prompt.Editor.History.UndoCount;

        await adapter.SendAsync(Encoding.UTF8.GetBytes("\u001b[200~ldc.i4.1\nret\u001b[201~\t"));
        await auto.WaitUntilAsync(snapshot => snapshot.GetLine(0).StartsWith("help ·", StringComparison.Ordinal)
            && Volatile.Read(ref publishedSequence) == 2,
            description: "Tab queued behind the ignored paste reaches help");

        Assert.AreEqual("constr", prompt.Text);
        Assert.AreEqual(caret, prompt.Editor.Cursor.Position);
        Assert.AreEqual(version, prompt.Editor.Document.Version);
        Assert.AreEqual(undo, prompt.Editor.History.UndoCount);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
        await IlReplApp.SettleAsync(prompt);
        Assert.IsEmpty(AppTest.Echoes(transcript));
    }

    /// <summary>
    /// F1 opens completion help in a full viewport, pages through it, and returns focus without editing or accepting a candidate.
    /// </summary>
    /// <param name="width">The resized terminal width.</param>
    /// <param name="height">The resized terminal height.</param>
    [TestMethod]
    [DataRow(24, 6)]
    [DataRow(80, 24)]
    public async Task CompletionHelp_PagesResizesAndPreservesEditor(int width, int height)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        PromptState prompt = null!;
        var adapter = new ScriptedPresentationAdapter(80, 24);
        await using var terminal = IlReplApp.Configure(Hex1b.Hex1bTerminal.CreateBuilder(), engine, transcript,
            onPrompt: state => prompt = state).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("constr", ct: ct);
        await auto.WaitUntilTextAsync("opcodes");
        await auto.WaitUntilAsync(_ => prompt.Analysis is not null, description: "instruction analysis is ready");
        var original = prompt.Text;
        var caret = prompt.Editor.Cursor.Position;
        var version = prompt.Editor.Document.Version;
        var selection = prompt.SelectedIndex;
        await auto.KeyAsync(Hex1bKey.F1, ct: ct);
        await auto.WaitUntilAsync(snapshot => snapshot.GetLine(0).StartsWith("help ·", StringComparison.Ordinal));
        adapter.Resize(width, height);
        await auto.WaitUntilAsync(snapshot => snapshot.Width == width && snapshot.Height == height,
            description: "help uses the resized terminal");
        await auto.TypeAsync("ignored", ct: ct);
        await auto.KeyAsync(Hex1bKey.Backspace, ct: ct);
        await auto.KeyAsync(Hex1bKey.Delete, ct: ct);
        await auto.KeyAsync(Hex1bKey.PageDown, ct: ct);
        if (height == 6)
        {
            await auto.WaitUntilAsync(_ => prompt.Help is { Scroll: > 0 }, description: "the help page scrolls");
        }

        await auto.TabAsync(ct: ct);
        await auto.WaitUntilAsync(snapshot => string.Concat(Enumerable.Range(0, snapshot.Height)
                .Select(row => snapshot.GetLine(row).Trim())).Contains(
                    InstructionReference.For("constrained.").DocumentationUrl, StringComparison.Ordinal),
            description: "the complete wrapped documentation target is visible");
        await auto.KeyAsync(Hex1bKey.PageUp, ct: ct);
        await auto.KeyAsync(Hex1bKey.F1, ct: ct);
        await auto.WaitUntilAsync(snapshot => !snapshot.GetLine(0).StartsWith("help ·", StringComparison.Ordinal) && prompt.Help is null,
            description: "F1 returns to the editor");
        Assert.AreEqual(original, prompt.Text);
        Assert.AreEqual(caret, prompt.Editor.Cursor.Position);
        Assert.AreEqual(version, prompt.Editor.Document.Version);
        Assert.AreEqual(selection, prompt.SelectedIndex);
        await auto.TypeAsync("ained. ", ct: ct);
        await auto.WaitUntilAsync(_ => prompt.Text == "constrained. ", description: "typing resumes in the editor");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
        await IlReplApp.SettleAsync(prompt);
        Assert.IsEmpty(AppTest.Echoes(transcript));
    }

    /// <summary>
    /// Tab and Shift+Tab choose documentation or producer actions and Enter activates only the selected fresh target.
    /// </summary>
    [TestMethod]
    public async Task DiagnosticHelp_ActivatesDocumentationThenNavigatesToProducer()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        PromptState prompt = null!;
        string? opened = null;
        var adapter = new ScriptedPresentationAdapter(90, 24);
        await using var terminal = IlReplApp.Configure(Hex1b.Hex1bTerminal.CreateBuilder(), engine, transcript,
            onPrompt: state =>
            {
                prompt = state;
                state.OpenDocumentation = url => Volatile.Write(ref opened, url);
            }).WithPresentation(adapter).Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        const string source = ".method int32 F() {\n  ldstr \"界👩‍💻\"\n  call int32 Math::Abs(int32)\n  ret\n}";
        await adapter.PasteAsync(source);
        await auto.WaitUntilAsync(_ => prompt.Analysis?.Diagnostics.Any(item => item.Code == "FLOW005") == true,
            description: "the real engine finds the argument mismatch");
        await auto.KeyAsync(Hex1bKey.F8, ct: ct);
        await auto.WaitUntilAsync(_ => prompt.CaretLine == 3 && prompt.Analysis is not null,
            description: "F8 moves to the rejected call");
        await auto.KeyAsync(Hex1bKey.F1, ct: ct);
        await auto.WaitUntilTextAsync("argument 1: expected int32; actual string");
        await auto.WaitUntilAsync(_ => prompt.Help?.IsCurrent(prompt) == true && prompt.Analyzer?.IsPending == false,
            description: "the displayed help actions belong to the current analysis");
        await auto.TabAsync(ct: ct);
        await auto.WaitUntilAsync(_ => prompt.Help is { SelectedAction: 1 }, description: "Tab selects the documentation action");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(_ => Volatile.Read(ref opened) is not null, description: "Enter opens the selected documentation");
        Assert.AreEqual(InstructionReference.For("call").DocumentationUrl, Volatile.Read(ref opened));
        Assert.AreEqual(3, prompt.CaretLine);
        Assert.AreEqual(source, prompt.Text);
        await auto.Shift().KeyAsync(Hex1bKey.Tab, ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(snapshot => !snapshot.GetLine(0).StartsWith("help ·", StringComparison.Ordinal) && prompt.CaretLine == 2,
            description: "Enter returns to the producer in the editor");
        Assert.AreEqual(2, prompt.CaretColumn);
        Assert.AreEqual(source, prompt.Text);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
        await IlReplApp.SettleAsync(prompt);
        Assert.IsEmpty(AppTest.Echoes(transcript));
    }

    /// <summary>
    /// Diagnostic navigation remains available inside help and Escape restores the editor at the selected diagnostic.
    /// </summary>
    [TestMethod]
    public async Task Help_F8AndShiftF8NavigateDiagnosticsWithoutChangingSource()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        PromptState prompt = null!;
        var adapter = new ScriptedPresentationAdapter(80, 20);
        await using var terminal = IlReplApp.Configure(Hex1b.Hex1bTerminal.CreateBuilder(), engine, transcript,
            onPrompt: state => prompt = state).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        const string source = ".method void F() {\n  pop\n}\n.method void G() {\n  pop\n}";
        await adapter.PasteAsync(source);
        await auto.WaitUntilAsync(_ => prompt.Analysis?.Diagnostics.Count(item => item.Code == "FLOW006") == 2,
            description: "the real engine reports both method underflows");
        await auto.KeyAsync(Hex1bKey.F8, ct: ct);
        await auto.WaitUntilAsync(_ => prompt.CaretLine == 2 && prompt.Analysis is not null);
        await auto.KeyAsync(Hex1bKey.F1, ct: ct);
        await auto.WaitUntilAsync(snapshot => snapshot.GetLine(0).StartsWith("help ·", StringComparison.Ordinal));
        await auto.KeyAsync(Hex1bKey.F8, ct: ct);
        await auto.WaitUntilAsync(_ => prompt.CaretLine == 5 && prompt.Analysis is not null);
        Assert.IsNotNull(prompt.Help);
        await auto.Shift().KeyAsync(Hex1bKey.F8, ct: ct);
        await auto.WaitUntilAsync(_ => prompt.CaretLine == 2 && prompt.Analysis is not null);
        await auto.KeyAsync(Hex1bKey.Escape, ct: ct);
        await auto.WaitUntilAsync(snapshot => !snapshot.GetLine(0).StartsWith("help ·", StringComparison.Ordinal) && prompt.Help is null);
        Assert.AreEqual(source, prompt.Text);
        Assert.AreEqual(2, prompt.CaretLine);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
        await IlReplApp.SettleAsync(prompt);
        Assert.IsEmpty(AppTest.Echoes(transcript));
    }
}
