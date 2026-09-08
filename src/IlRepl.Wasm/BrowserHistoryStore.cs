using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Wasm;

/// <summary>
/// History in the browser's own database. IndexedDB is reachable from the worker the runtime
/// runs in, and every entry is one record, so two tabs never write over each other's entries.
/// </summary>
public sealed partial class BrowserHistoryStore : IHistoryStore
{
    private Task _pending = Task.CompletedTask;

    /// <inheritdoc />
    public string? Problem { get; private set; }

    /// <inheritdoc />
    public int Written { get; private set; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The entries cross the interop boundary as one JSON array, so any character an
            // entry holds comes through as itself.
            var json = await LoadHistory().ConfigureAwait(false);
            return JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.StringArray) ?? [];
        }
        catch (JSException ex)
        {
            Problem = Reason(ex);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task AppendAsync(string entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var append = AppendHistory(entry);
        _pending = append;
        try
        {
            await append.ConfigureAwait(false);
            Written++;
        }
        catch (JSException ex)
        {
            Problem = Reason(ex);
        }
    }

    /// <summary>
    /// Waits for the append in flight, if any, so a session that quits leaves nothing half written.
    /// </summary>
    /// <returns>A task that completes once the store is idle.</returns>
    public async Task SettleAsync()
    {
        try
        {
            await _pending.ConfigureAwait(false);
        }
        catch (JSException)
        {
            // Already reported through Problem.
        }
    }

    // A rejection reaches .NET as the error's string form, "Error: reason"; the reason is enough.
    private static string Reason(JSException ex) => ex.Message.StartsWith("Error: ", StringComparison.Ordinal) ? ex.Message["Error: ".Length..] : ex.Message;

    [JSImport("loadHistory", "main.js")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string> LoadHistory();

    [JSImport("appendHistory", "main.js")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    private static partial Task AppendHistory(string entry);
}
