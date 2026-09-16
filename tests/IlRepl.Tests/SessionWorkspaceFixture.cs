using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests;

/// <summary>
/// Owns isolated session files and observable execution markers for frontend integration tests.
/// </summary>
internal sealed class SessionWorkspaceFixture : IDisposable
{
    /// <summary>
    /// The unique directory containing every file created by this fixture.
    /// </summary>
    public string DirectoryPath { get; } = Directory.CreateTempSubdirectory("ilrepl-session-ui-").FullName;

    /// <summary>
    /// The session filename, including spaces to exercise command and dialog path handling.
    /// </summary>
    public string SessionPath => Path.Combine(DirectoryPath, "saved experiment.ilrepl.json");

    /// <summary>
    /// The marker created only when the saved instructions execute.
    /// </summary>
    public string MarkerPath => Path.Combine(DirectoryPath, "executed.txt");

    /// <summary>
    /// Creates a document with accepted executable source but no execution attempt.
    /// </summary>
    /// <param name="editor">The optional unsent draft.</param>
    /// <returns>The complete portable document.</returns>
    public SessionDocument PendingDocument(SessionEditor? editor = null)
    {
        string[] lines =
        [
            "ldstr \"" + MarkerPath.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"",
            "ldstr \"executed\"",
            "call void [System.IO.FileSystem]System.IO.File::WriteAllText(string, string)",
            "ldc.i4.s 42",
        ];
        return new SessionDocument
        {
            Entries = lines.Select(line => new SessionEntry { Source = [line] }).ToArray(),
            Editor = editor ?? new SessionEditor(),
        };
    }

    /// <summary>
    /// Executes one real cell with observable output and a marker before capturing its saved history.
    /// </summary>
    /// <param name="editor">The unsent editor draft retained alongside the completed cell.</param>
    /// <returns>The saved source, formatted output, and result of the completed execution.</returns>
    public SessionDocument CompletedDocument(SessionEditor? editor = null)
    {
        using var core = new ReplCore();
        string[] lines = ["// saved café λ", .. PendingDocument().Entries.SelectMany(entry => entry.Source),
            "ldstr \"saved stdout\"", "call Console::WriteLine(string)", "ret"];
        foreach (var line in lines)
        {
            Assert.IsTrue(core.Handle(line).Succeeded, line);
        }

        Assert.AreEqual("executed", File.ReadAllText(MarkerPath));
        return core.CaptureSession(editor ?? new SessionEditor());
    }

    /// <summary>
    /// Writes the fixture document through the public codec.
    /// </summary>
    /// <param name="document">The document, or the default pending experiment.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The completed file write.</returns>
    public Task WriteAsync(SessionDocument? document, CancellationToken cancellationToken) =>
        File.WriteAllBytesAsync(SessionPath, SessionCodec.Write(document ?? PendingDocument()), cancellationToken);

    /// <summary>
    /// Starts a workspace using real disposable host processes.
    /// </summary>
    /// <param name="cancellationToken">Cancels host startup.</param>
    /// <returns>The replaceable host coordinator.</returns>
    public static async Task<SessionController> StartAsync(CancellationToken cancellationToken) =>
        new(await HostPaths.StartEngineAsync(cancellationToken), async token => await HostPaths.StartEngineAsync(token));

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
