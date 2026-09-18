namespace IlRepl.Engine;

/// <summary>
/// Publishes a completed assembly by replacing its destination with a fully written sibling file.
/// </summary>
internal static class AtomicAssemblyFile
{
    /// <summary>
    /// Writes a cancellable image without modifying the destination until the complete file is ready.
    /// </summary>
    /// <param name="path">The absolute destination path.</param>
    /// <param name="image">The complete assembly image.</param>
    /// <param name="cancellationToken">Cancels writing before the final atomic replacement.</param>
    internal static void Write(string path, ReadOnlySpan<byte> image, CancellationToken cancellationToken)
        => Write(path, image, null, cancellationToken);

    /// <summary>
    /// Reports completed physical write batches before cancellation and atomic publication are checked.
    /// </summary>
    /// <param name="path">The absolute destination path.</param>
    /// <param name="image">The complete assembly image.</param>
    /// <param name="progress">An optional per-operation observer of bytes written to the temporary file.</param>
    /// <param name="cancellationToken">Cancels writing before the final atomic replacement.</param>
    internal static void Write(string path, ReadOnlySpan<byte> image, Action<long>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".ilrepl-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var written = 0L;
                while (!image.IsEmpty)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(image.Length, 64 * 1024);
                    file.Write(image[..count]);
                    image = image[count..];
                    written += count;
                    progress?.Invoke(written);
                }

                file.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
