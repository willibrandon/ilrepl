using Hex1b;
using Hex1b.Input;
using Hex1b.Widgets;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// The terminal UI: a following transcript, the prompt, and a status bar. The layout is
/// attached to a terminal builder so the same tree runs on a real console or a headless
/// emulator in tests.
/// </summary>
public static class IlReplApp
{
    /// <summary>
    /// The banner shown at the top of an empty transcript.
    /// </summary>
    public const string Banner = "ilrepl  type IL, watch the stack, ret runs the cell.  .help for more";

    /// <summary>
    /// Attaches the REPL application to a terminal builder.
    /// </summary>
    /// <param name="builder">The terminal builder.</param>
    /// <param name="engine">The engine that handles lines.</param>
    /// <param name="transcript">The transcript to render and append to.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static Hex1bTerminalBuilder Configure(Hex1bTerminalBuilder builder, IReplEngine engine, Transcript transcript)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(transcript);
        if (transcript.Lines.Count == 0)
        {
            transcript.Add(LineKind.Info, Banner, SpanStyle.Dim);
        }

        return builder.WithHex1bApp(
            options => { },
            app =>
            {
                app.RequestFocus(node => node is TextBoxNode);
                return ctx => BuildRoot(ctx, app, engine, transcript);
            });
    }

    /// <summary>
    /// Runs the REPL on the current console until the user leaves.
    /// </summary>
    /// <param name="engine">The engine that handles lines.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(IReplEngine engine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var transcript = new Transcript { MaxLines = 1000 };
        await using var terminal = Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithMouse()
            .Build();
        return await terminal.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static VStackWidget BuildRoot(RootContext ctx, Hex1bApp app, IReplEngine engine, Transcript transcript)
    {
        var status = engine.Status;
        return ctx.VStack(v =>
        [
            v.VScrollPanel(sv => transcript.Lines.Select(line => (Hex1bWidget)new TranscriptLineWidget(line)).ToArray(), showScrollbar: true)
                .Follow()
                .Fill(),
            v.Separator(),
            v.IlPrompt(status.Prompt, engine.Catalog)
                .OnSubmit(async line =>
                {
                    try
                    {
                        var reply = await engine.HandleAsync(line, CancellationToken.None).ConfigureAwait(true);
                        foreach (var l in reply.Lines)
                        {
                            transcript.Add(l);
                        }

                        if (reply.Quit)
                        {
                            app.RequestStop();
                        }
                    }
                    catch (ReplEngineException ex)
                    {
                        transcript.Add(new TranscriptLine(LineKind.Error, [new TranscriptSpan("  engine error: ", SpanStyle.Error), new TranscriptSpan(ex.Message)]));
                    }

                    app.RequestFocus(node => node is TextBoxNode);
                    app.Invalidate();
                }),
            v.InfoBar(s =>
            [
                s.Section("ilrepl"),
                s.Section("stack " + status.Stack),
                s.Section(status.Locals == 0 ? "no locals" : $"{status.Locals} local{(status.Locals == 1 ? "" : "s")}"),
                s.Section(status.OpenBlocks > 0 ? $"{status.OpenBlocks} open block{(status.OpenBlocks == 1 ? "" : "s")}" : $"{status.Instructions} instruction{(status.Instructions == 1 ? "" : "s")}"),
                s.Spacer(),
                s.Section("Tab complete"),
                s.Section("↑↓ history"),
                s.Section("Ctrl+L clear"),
                s.Section("Ctrl+Q quit"),
            ]).Divider(" │ "),
        ])
        .InputBindings(b =>
        {
            b.Ctrl().Key(Hex1bKey.Q).Action(c =>
            {
                c.RequestStop();
                return Task.CompletedTask;
            }, "Quit");
            b.Ctrl().Key(Hex1bKey.L).Action(_ =>
            {
                transcript.Clear();
                transcript.Add(LineKind.Info, Banner, SpanStyle.Dim);
                app.Invalidate();
            }, "Clear transcript");
        });
    }
}
