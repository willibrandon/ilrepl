using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using Hex1b;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;
using IlRepl.Wasm;

// Bind the [JSImport] functions to the interop module that the worker loads.
await JSHost.ImportAsync("main.js", "../interop.js");

// The page posts the terminal size as soon as the worker starts, but the message is delivered
// asynchronously. Wait for it briefly so the first layout matches the terminal rather than a default.
var columns = 100;
var rows = 28;
var initialSize = WasmPresentationAdapter.PollResize();
for (var attempt = 0; attempt < 300 && string.IsNullOrEmpty(initialSize); attempt++)
{
    await Task.Delay(10);
    initialSize = WasmPresentationAdapter.PollResize();
}

if (TryParseSize(initialSize, out var initialColumns, out var initialRows))
{
    columns = initialColumns;
    rows = initialRows;
}

// Each worker owns one session. After quitting, persist history and let the page replace this
// worker with a fresh runtime. Starting another loop here would race that replacement.
var history = new BrowserHistoryStore();
await RunSessionAsync(columns, rows, history);
await history.SettleAsync();
WasmPresentationAdapter.NotifyExited();

static async Task RunSessionAsync(int columns, int rows, BrowserHistoryStore history)
{
    var adapter = new WasmPresentationAdapter(columns, rows);
    WasmPresentationAdapter.Instance = adapter;

    await using var engine = new SessionController(new InProcessEngine(new ReplCore()),
        _ => Task.FromResult<IReplEngine>(new InProcessEngine(new ReplCore())));
    await BrowserWorkspace.InitializeAsync(engine);
    var transcript = new Transcript { MaxLines = 500 };
    transcript.Add(LineKind.Info, IlReplApp.Banner, SpanStyle.Dim);
    foreach (var line in BrowserWorkspace.StartupMessages)
    {
        transcript.Add(line);
    }
    PromptState? prompt = null;
    // Selection and copy are the terminal's own in the browser, so the mouse stays with it. Wheel
    // notches still reach the app, as the reports a terminal sends; the page makes them.
    await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder().WithPresentation(adapter), engine, transcript,
        history: history, onPrompt: p =>
        {
            prompt = p;
            BrowserWorkspace.Attach(p);
            p.OpenDocumentation = null;
            p.DocumentationTargetChanged = WasmPresentationAdapter.DocumentationTarget;
        }, ownSelection: false)
        .Build();

    WasmPresentationAdapter.NotifyReady(adapter.Width, adapter.Height);
    try
    {
        // Settle pending submissions before handing the completed session back to the page.
        await IlReplApp.RunAsync(terminal, prompt, CancellationToken.None);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // The browser console is the only place a failure can be read from.
        Console.Error.WriteLine(ex.ToString());
        throw;
    }
}

static bool TryParseSize(string? size, out int columns, out int rows)
{
    columns = 0;
    rows = 0;
    if (string.IsNullOrEmpty(size))
    {
        return false;
    }

    var parts = size.Split(',');
    return parts.Length == 2
        && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out columns)
        && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out rows);
}
