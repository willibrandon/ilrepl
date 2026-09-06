using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using Hex1b;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;
using IlRepl.Wasm;

// Bind the [JSImport] functions to the interop module that the worker loads.
await JSHost.ImportAsync("main.js", "../interop.js");

var columns = 100;
var rows = 30;
var initialSize = WasmPresentationAdapter.PollResize();
if (!string.IsNullOrEmpty(initialSize))
{
    var parts = initialSize.Split(',');
    if (parts.Length == 2
        && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)
        && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
    {
        columns = c;
        rows = r;
    }
}

var adapter = new WasmPresentationAdapter(columns, rows);
WasmPresentationAdapter.Instance = adapter;

await using var engine = new InProcessEngine();
var transcript = new Transcript { MaxLines = 500 };
await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder().WithPresentation(adapter), engine, transcript)
    .WithMouse()
    .Build();

WasmPresentationAdapter.NotifyReady(adapter.Width, adapter.Height);
try
{
    await terminal.RunAsync();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    // The browser console is the only place a failure can be read from.
    Console.Error.WriteLine(ex.ToString());
    throw;
}
