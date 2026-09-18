using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace IlRepl.Protocol;

/// <summary>
/// Reads and writes bounded, portable session documents and versioned share fragments.
/// </summary>
public static class SessionCodec
{
    /// <summary>
    /// The largest UTF-8 session file accepted before parsing.
    /// </summary>
    public const int FileLimit = 64 * 1024 * 1024;

    /// <summary>
    /// The largest expanded document accepted from a share fragment.
    /// </summary>
    public const int FragmentDocumentLimit = 1024 * 1024;

    /// <summary>
    /// The largest complete URL offered by the Share action.
    /// </summary>
    public const int LinkLimit = 16 * 1024;

    /// <summary>
    /// Identifies recommended session filenames without inspecting or executing their contents.
    /// </summary>
    /// <param name="path">The candidate path.</param>
    /// <returns>Whether its extension identifies a session document.</returns>
    public static bool IsSessionPath(string path) => path.EndsWith(".ilrepl.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Computes the canonical content identity of a dependency image.
    /// </summary>
    /// <param name="image">The exact asset bytes.</param>
    /// <returns>The lowercase SHA-256 hash.</returns>
    public static string Hash(ReadOnlySpan<byte> image) => Convert.ToHexStringLower(SHA256.HashData(image));

    /// <summary>
    /// Validates and serializes a document with stable asset ordering and readable physical source lines.
    /// </summary>
    /// <param name="document">The document to serialize.</param>
    /// <returns>The UTF-8 representation.</returns>
    public static byte[] Write(SessionDocument document)
    {
        Validate(document);
        var ordered = document with { Assets = [.. document.Assets.OrderBy(asset => asset.Hash, StringComparer.Ordinal)] };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ordered, SessionJsonContext.Default.SessionDocument);
        if (bytes.Length > FileLimit)
        {
            throw new InvalidDataException("the session document exceeds the 64 MiB file limit");
        }

        return bytes;
    }

    /// <summary>
    /// Parses a session file and validates every referenced source identity and embedded asset.
    /// </summary>
    /// <param name="bytes">The UTF-8 document.</param>
    /// <returns>The validated document.</returns>
    public static SessionDocument Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > FileLimit)
        {
            throw new InvalidDataException("the session document exceeds the 64 MiB file limit");
        }

        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new InvalidDataException("the file is not an ilrepl session document");
            }

            var hasFormat = false;
            var hasVersion = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                hasFormat |= reader.ValueTextEquals("format"u8);
                hasVersion |= reader.ValueTextEquals("version"u8);
                reader.Read();
                reader.Skip();
                if (hasFormat && hasVersion) break;
            }

            if (!hasFormat || !hasVersion)
            {
                throw new InvalidDataException("the file is not an ilrepl session document");
            }

            var document = JsonSerializer.Deserialize(bytes, SessionJsonContext.Default.SessionDocument)
                ?? throw new InvalidDataException("the session document is empty");
            Validate(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("invalid session JSON: " + exception.Message, exception);
        }
    }

    /// <summary>
    /// Produces a versioned fragment after bounding both the expanded document and complete URL.
    /// </summary>
    /// <param name="document">The portable example.</param>
    /// <param name="baseUrl">The page URL without a fragment.</param>
    /// <returns>The complete share URL.</returns>
    public static string Share(SessionDocument document, string baseUrl)
    {
        var bytes = Write(document);
        if (bytes.Length > FragmentDocumentLimit)
        {
            throw new InvalidDataException("the example is too large for a share link; download the session file");
        }

        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(bytes);
        }

        var encoded = Convert.ToBase64String(buffer.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var url = baseUrl.Split('#')[0] + "#session=v1." + encoded;
        if (url.Length > LinkLimit)
        {
            throw new InvalidDataException("the example exceeds the 16 KiB link limit; download the session file");
        }

        return url;
    }

    /// <summary>
    /// Expands a share fragment with a hard limit before deserializing its document.
    /// </summary>
    /// <param name="fragment">The fragment, with or without its leading hash.</param>
    /// <returns>The validated session document.</returns>
    public static SessionDocument ReadFragment(string fragment)
    {
        const string prefix = "session=v1.";
        var value = fragment.TrimStart('#');
        if (value.Length > LinkLimit || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("invalid or unsupported session share link");
        }

        try
        {
            var encoded = value[prefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            var compressed = Convert.FromBase64String(encoded);
            if (compressed.Length < 18)
            {
                throw new InvalidDataException("the session share payload is not a complete gzip stream");
            }

            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = gzip.Read(chunk)) != 0)
            {
                if (output.Length + read > FragmentDocumentLimit)
                {
                    throw new InvalidDataException("the share link exceeds the 1 MiB expanded-document limit");
                }

                output.Write(chunk, 0, read);
            }

            var bytes = output.GetBuffer().AsSpan(0, checked((int)output.Length));
            var trailer = compressed.AsSpan(compressed.Length - 8);
            if (BinaryPrimitives.ReadUInt32LittleEndian(trailer) != Crc32(bytes)
                || BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) != (uint)bytes.Length)
            {
                throw new InvalidDataException("the session share payload has a missing or corrupt gzip trailer");
            }

            return Read(bytes);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("invalid session share encoding", exception);
        }
    }

    /// <summary>
    /// Validates document invariants without loading assemblies or interpreting source.
    /// </summary>
    /// <param name="document">The document to validate.</param>
    public static void Validate(SessionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Format != "ilrepl-session" || document.Version != 1)
        {
            throw new InvalidDataException($"unsupported session format '{document.Format}' version {document.Version}");
        }

        if (document.Entries is null || document.Cells is null || document.Interruptions is null
            || document.References is null || document.Assets is null
            || document.Editor is null || document.Runtime is null)
        {
            throw new InvalidDataException("the session contains a null document collection");
        }

        Lines(document.Editor.Lines);
        var length = string.Join('\n', document.Editor.Lines).Length;
        if (document.Editor.Caret < 0 || document.Editor.Caret > length
            || document.Editor.Anchor < 0 || document.Editor.Anchor > length)
        {
            throw new InvalidDataException("the editor selection lies outside its source");
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Identity) || !identities.Add(entry.Identity)
                || entry.Number < 1 || !Enum.IsDefined(entry.Kind))
            {
                throw new InvalidDataException("invalid or duplicate source transition");
            }

            Lines(entry.Source);
            if (entry.Kind == SessionEntryKind.Edit && entry.Edit is null)
            {
                throw new InvalidDataException("an edit source transition is missing its immutable snapshot");
            }

            if (entry.Edit is { } edit)
            {
                if (string.IsNullOrWhiteSpace(edit.Name) || string.IsNullOrWhiteSpace(edit.Reference)
                    || string.IsNullOrWhiteSpace(edit.Fingerprint))
                {
                    throw new InvalidDataException("an edit snapshot has an invalid name, method reference, or fingerprint");
                }

                Lines(edit.Source);
                if (edit.PinnedMethods is null || edit.MethodAliases is null || edit.SignatureHeaders is null || edit.TypeAliases is null
                    || edit.PinnedMethods.Keys.Any(string.IsNullOrWhiteSpace) || edit.MethodAliases.Keys.Any(string.IsNullOrWhiteSpace)
                    || edit.SignatureHeaders.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                    || edit.TypeAliases.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
                {
                    throw new InvalidDataException("an edit snapshot has invalid frozen method or type aliases");
                }

                if (edit.Original is { } original)
                {
                    MethodIdentity(original);
                }

                foreach (var method in edit.PinnedMethods.Values.Concat(edit.MethodAliases.Values))
                {
                    MethodIdentity(method);
                }
            }
        }

        identities.Clear();
        var numbers = new HashSet<int>();
        foreach (var cell in document.Cells)
        {
            if (cell is null || string.IsNullOrWhiteSpace(cell.Identity) || !identities.Add(cell.Identity)
                || cell.Number < 1 || !numbers.Add(cell.Number) || cell.Output is null
                || cell.State is not ("unrun" or "succeeded" or "failed" or "interrupted") || cell.Kind is not ("cell" or "definition"))
            {
                throw new InvalidDataException("invalid or duplicate numbered cell");
            }

            Lines(cell.Source);
            Lines(cell.Inputs);
            if (cell.Output.Any(line => line is null || !Enum.IsDefined(line.Kind) || line.Spans is null
                || line.Spans.Any(span => span is null || span.Text is null || !Enum.IsDefined(span.Style))))
            {
                throw new InvalidDataException("invalid historical output");
            }
        }

        identities.Clear();
        foreach (var interruption in document.Interruptions)
        {
            if (interruption is null || string.IsNullOrWhiteSpace(interruption.Identity) || !identities.Add(interruption.Identity)
                || interruption.Number < 1)
            {
                throw new InvalidDataException("invalid or duplicate interrupted execution attempt");
            }

            Lines(interruption.Source);
        }

        identities.Clear();
        foreach (var reference in document.References)
        {
            if (reference is null || string.IsNullOrWhiteSpace(reference.Identity) || !identities.Add(reference.Identity)
                || string.IsNullOrWhiteSpace(reference.Request) || reference.Assets is null || reference.Dependencies is null
                || reference.Origin is not ("assembly" or "package" or "project" or "baseline")
                || reference.Frameworks is null || reference.Frameworks.Any(string.IsNullOrWhiteSpace)
                || reference.Dependencies.Any(string.IsNullOrWhiteSpace)
                || reference.Assets.Any(asset => asset is null || !IsHash(asset.Hash) || string.IsNullOrWhiteSpace(asset.Name)
                    || asset.Kind is not ("managed" or "reference" or "native" or "satellite")
                    || (asset.PackagePath is not null && (reference.Origin != "package" || !IsPackagePath(asset.PackagePath)))))
            {
                throw new InvalidDataException("invalid or duplicate dependency reference");
            }
        }

        if (document.References.Any(reference => reference.Dependencies.Any(dependency => !identities.Contains(dependency))))
        {
            throw new InvalidDataException("the dependency graph names a missing reference");
        }

        foreach (var entry in document.Entries.Where(entry => entry.Kind == SessionEntryKind.Reference))
        {
            if (entry.Reference is null || !identities.Contains(entry.Reference))
            {
                throw new InvalidDataException("a source transition names a missing dependency reference");
            }
        }

        var baselines = document.References.Where(reference => reference.Origin == "baseline")
            .Select(reference => reference.Identity).ToHashSet(StringComparer.Ordinal);
        if (document.Entries.Any(entry => entry.Edit?.BaselineReference is { } baseline && !baselines.Contains(baseline)))
        {
            throw new InvalidDataException("an edit snapshot names a missing baseline reference");
        }

        identities.Clear();
        foreach (var asset in document.Assets)
        {
            if (asset is null || asset.Image is null || !IsHash(asset.Hash) || !identities.Add(asset.Hash)
                || Hash(asset.Image) != asset.Hash)
            {
                throw new InvalidDataException("an embedded asset has an invalid, duplicate, or mismatched content hash");
            }
        }
    }

    private static bool IsPackagePath(string path) => path.Length != 0 && !path.Contains('\\') && !path.Contains(':')
        && !path.Contains('\0') && path.Split('/').All(segment => segment.Length != 0 && segment is not "." and not "..");

    private static bool IsHash(string hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void MethodIdentity(SessionMethodIdentity identity)
    {
        if (identity is null || string.IsNullOrWhiteSpace(identity.Assembly) || !Guid.TryParse(identity.Module, out _)
            || (identity.Token & unchecked((int)0xff000000)) != 0x06000000 || (identity.Token & 0x00ffffff) == 0
            || identity.TypeArguments is null || identity.MethodArguments is null
            || identity.TypeArguments.Concat(identity.MethodArguments)
                .Any(argument => argument is not null && string.IsNullOrWhiteSpace(argument)))
        {
            throw new InvalidDataException("an edit snapshot has an invalid frozen method identity");
        }
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var checksum = uint.MaxValue;
        foreach (var value in bytes)
        {
            checksum ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                checksum = (checksum >> 1) ^ ((checksum & 1) == 0 ? 0 : 0xedb88320u);
            }
        }

        return ~checksum;
    }

    private static void Lines(string[] lines)
    {
        if (lines is null || lines.Any(line => line is null || line.Contains('\n') || line.Contains('\r')))
        {
            throw new InvalidDataException("source must be an array of physical lines");
        }
    }
}
