using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Host;

/// <summary>
/// Supplies desktop storage and dependency tooling to the shared engine's typed workspace operations.
/// </summary>
internal sealed class HostSessionService
{
    private readonly InProcessEngine _engine;
    private readonly SessionFileStore _files = new();

    /// <summary>
    /// Attaches desktop workspace operations to an engine owned by this host.
    /// </summary>
    /// <param name="engine">The host's engine.</param>
    internal HostSessionService(InProcessEngine engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// Performs filesystem work outside the engine gate and acknowledges the exact resulting revision.
    /// </summary>
    /// <param name="request">The typed operation.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The resulting source workspace.</returns>
    internal async Task<SessionReply> ExecuteAsync(SessionRequest request, CancellationToken cancellationToken)
    {
        if (request.Action.Operation == SessionOperation.Open)
        {
            var path = Path.GetFullPath(request.Action.Path ?? throw new InvalidDataException("a session path is required"));
            var document = await _files.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            document = await MaterializeNativeAsync(document, cancellationToken).ConfigureAwait(false);
            return await _engine.SessionAsync(request with
            {
                Action = new SessionAction
                {
                    Operation = request.Action.Execute ? SessionOperation.Run : SessionOperation.Hydrate,
                    Path = path,
                },
                Document = document, Editor = document.Editor,
            }, cancellationToken).ConfigureAwait(false);
        }

        var capture = await _engine.SessionAsync(request with
        {
            Action = new SessionAction { Operation = SessionOperation.Capture },
        }, cancellationToken).ConfigureAwait(false);

        if (request.Action.Operation == SessionOperation.Save)
        {
            var path = request.Action.Path ?? capture.Path ?? throw new InvalidDataException("a path is required for the first save");
            path = await _files.WriteAsync(path, capture.Document, request.Action.Embed, cancellationToken).ConfigureAwait(false);
            var saved = await _engine.SessionAsync(request with
            {
                Action = new SessionAction { Operation = SessionOperation.AcknowledgeSave, Path = path },
                Document = capture.Document,
            }, cancellationToken).ConfigureAwait(false);

            return saved with
            {
                Reply = saved.Reply with
                {
                    Lines = [TranscriptLine.Of(LineKind.Info, "  saved session " + path, SpanStyle.Dim)],
                },
            };
        }

        var source = request.Document ?? capture.Document;
        var directory = Path.GetDirectoryName(request.AssociatedPath ?? capture.Path) ?? Directory.GetCurrentDirectory();
        SessionDocument resolved;
        var warnings = new List<string>();
        if (request.Action.Operation == SessionOperation.Restore)
        {
            resolved = await PackageResolver.ResolveAsync(source, null, directory, warnings, cancellationToken).ConfigureAwait(false);
            if (request.Action.Build)
            {
                foreach (var project in source.References.Where(reference => reference.Origin == "project"))
                {
                    var built = await ProjectResolver.ResolveAsync(resolved, new SessionAction
                    {
                        Operation = SessionOperation.Load, Path = project.Request, Framework = project.Framework,
                        Configuration = project.Configuration,
                    }, cancellationToken).ConfigureAwait(false);

                    var outputs = built.References.Single(reference => reference.Identity == project.Identity);
                    if (!project.Assets.Select(asset => asset.Hash).Order()
                        .SequenceEqual(outputs.Assets.Select(asset => asset.Hash).Order()))
                    {
                        throw new InvalidDataException($"project '{project.Request}' produced changed outputs; "
                            + "use .load to adopt the new build, or .load with --reload when existing definitions use it");
                    }

                    resolved = built;
                }
            }
        }
        else if (request.Action.Path?.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase) == true)
        {
            resolved = await PackageResolver.ResolveAsync(source, request.Action.Path, directory, warnings, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (request.Action.Path is { } path && (Directory.Exists(path)
            || Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj"))
        {
            resolved = await ProjectResolver.ResolveAsync(source, request.Action, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            resolved = await AssemblyResolver.ResolveAsync(source,
                request.Action.Path ?? throw new InvalidDataException("a dependency path is required"), cancellationToken)
                .ConfigureAwait(false);
        }

        resolved = await MaterializeNativeAsync(resolved, cancellationToken).ConfigureAwait(false);
        var reply = await _engine.SessionAsync(request with
        {
            Action = new SessionAction
            {
                Operation = request.Document is null ? SessionOperation.AdoptReferences : SessionOperation.Hydrate,
                Path = request.AssociatedPath,
            },
            Document = resolved,
        }, cancellationToken).ConfigureAwait(false);

        if (reply.Diagnostics.Length != 0 && request.Action.Operation == SessionOperation.Load)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, reply.Diagnostics));
        }

        return reply with
        {
            Reply = reply.Reply with
            {
                Lines = [.. reply.Reply.Lines,
                    .. warnings.Select(message => TranscriptLine.Of(LineKind.Info, "  " + message, SpanStyle.Dim)),
                    .. resolved.References.Where(reference => reference.Origin is "package" or "project")
                    .Select(reference => TranscriptLine.Of(LineKind.Info, reference.Origin == "project"
                        ? "  loaded project " + reference.Request + " (" + reference.Framework + ", " + reference.Configuration
                            + ", SDK " + reference.SdkVersion + ") -> " + reference.Assets.FirstOrDefault()?.Path
                        : "  " + reference.Request + (reference.Version is { } version ? " " + version : "")
                            + " (" + reference.Framework + ")", SpanStyle.Dim))],
            },
        };
    }

    private static async Task<SessionDocument> MaterializeNativeAsync(SessionDocument document, CancellationToken cancellationToken)
    {
        var images = document.Assets.ToDictionary(asset => asset.Hash, asset => asset.Image, StringComparer.Ordinal);
        var references = new List<SessionReference>();
        foreach (var reference in document.References)
        {
            var assets = new List<SessionReferenceAsset>();
            foreach (var asset in reference.Assets)
            {
                if (asset.Kind != "native" || !images.TryGetValue(asset.Hash, out var bytes))
                {
                    assets.Add(asset);
                    continue;
                }

                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ilrepl", "native", asset.Hash);
                Directory.CreateDirectory(directory);
                var destination = Path.Combine(directory, Path.GetFileName(asset.Name));
                if (File.Exists(destination) && new FileInfo(destination).Length == bytes.Length
                    && SessionCodec.Hash(await File.ReadAllBytesAsync(destination, cancellationToken).ConfigureAwait(false)) == asset.Hash)
                {
                    assets.Add(asset with { Path = destination });
                    continue;
                }

                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                    File.Move(temporary, destination, overwrite: true);
                }
                finally
                {
                    File.Delete(temporary);
                }

                assets.Add(asset with { Path = destination });
            }

            references.Add(reference with { Assets = [.. assets] });
        }

        return document with { References = [.. references] };
    }
}
