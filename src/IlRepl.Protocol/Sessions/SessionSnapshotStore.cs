namespace IlRepl.Protocol;

/// <summary>
/// Saves source snapshots with identical portable paths, dependency caches, and atomic replacement in every terminal runtime state.
/// </summary>
public static partial class SessionSnapshotStore
{
    /// <summary>
    /// Saves an acknowledged workspace without requiring a running execution host.
    /// </summary>
    /// <param name="path">The destination path.</param>
    /// <param name="document">The captured source and editor state.</param>
    /// <param name="embed">Whether to embed all available dependency images.</param>
    /// <param name="cancellationToken">Cancels preparation and writing before atomic replacement.</param>
    /// <returns>The absolute associated session path.</returns>
    public static Task<string> WriteAsync(string path, SessionDocument document, bool embed, CancellationToken cancellationToken)
        => WriteAsync(path, document, embed,
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ilrepl", "assets"),
            cancellationToken);

    /// <summary>
    /// Writes verified assets and private locators to an isolated cache before publishing the portable source document.
    /// </summary>
    /// <param name="path">The destination path.</param>
    /// <param name="document">The captured source and editor state.</param>
    /// <param name="embed">Whether to embed all available dependency images.</param>
    /// <param name="cacheDirectory">The local asset cache used for later recovery.</param>
    /// <param name="cancellationToken">Cancels preparation and writing before atomic replacement.</param>
    /// <returns>The absolute associated session path.</returns>
    public static async Task<string> WriteAsync(
        string path,
        SessionDocument document,
        bool embed,
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionCodec.Validate(document);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var baselines = document.References.Where(reference => reference.Origin == "baseline")
            .SelectMany(reference => reference.Assets).Select(asset => asset.Hash).ToHashSet(StringComparer.Ordinal);
        var portable = document with
        {
            References = [.. document.References.Select(reference => PortableLocators(reference, directory))],
            Assets = embed ? document.Assets : [.. document.Assets.Where(asset => baselines.Contains(asset.Hash))],
        };

        var bytes = SessionCodec.Write(portable);
        Directory.CreateDirectory(cacheDirectory);
        foreach (var reference in document.References.Where(reference => reference.Origin is "project" or "assembly"))
        {
            await RememberLocatorsAsync(cacheDirectory, reference, cancellationToken).ConfigureAwait(false);
        }

        foreach (var asset in document.Assets)
        {
            await AtomicWriteAsync(Path.Join(cacheDirectory, asset.Hash), asset.Image, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            Directory.CreateDirectory(directory);
            await AtomicWriteAsync(fullPath, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"could not save session '{fullPath}': {exception.Message}", exception);
        }

        return fullPath;
    }

    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
            {
                ReplaceWindowsFile(temporary, path);
            }
            else
            {
                File.Move(temporary, path, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
