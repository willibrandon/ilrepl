using System.Text.Json;
using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// Publishes atomic worker snapshots while allowing readers to retain the previous snapshot on Windows.
/// </summary>
public static class NativeStateFile
{
    /// <summary>
    /// Opens a snapshot without preventing its atomic replacement by the worker.
    /// </summary>
    /// <param name="path">The worker's state file.</param>
    /// <returns>An asynchronously readable stream that shares deletion and replacement.</returns>
    public static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>
    /// Reads one complete published snapshot, retaining the caller's previous state when publication is in progress.
    /// </summary>
    /// <param name="path">The worker's state file.</param>
    /// <param name="cancellationToken">Cancels reading the snapshot.</param>
    /// <returns>The current snapshot, or null when it is not yet readable.</returns>
    public static async Task<NativeWorkerState?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = OpenRead(path);
                return await JsonSerializer.DeserializeAsync(stream, ProtocolJsonContext.Default.NativeWorkerState,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
            {
                if (attempt == 2)
                {
                    return null;
                }

                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// Publishes a complete snapshot without exposing partially written JSON to the supervisor.
    /// </summary>
    /// <param name="root">The worker's private control directory.</param>
    /// <param name="state">The complete new snapshot.</param>
    /// <returns>A task completing after atomic publication.</returns>
    public static async Task WriteAsync(string root, NativeWorkerState state)
    {
        var path = Path.Join(root, "state.json");
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(state,
            ProtocolJsonContext.Default.NativeWorkerState)).ConfigureAwait(false);
        // MoveFileEx cannot overwrite an open destination on Windows, even when the reader shares deletion.
        if (OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.Replace(path + ".tmp", path, null);
        }
        else
        {
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }
}
