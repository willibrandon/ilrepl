using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Engine;

/// <summary>
/// Finds the bytes of a method body. The PE image comes first, because reflection refuses to
/// describe a body whose locals or clauses name something it cannot load while the bytes sit
/// there unchanged: the image a session definition was loaded from, the bytes read when an
/// assembly was <c>.load</c>ed, or the file a framework assembly lives in. Every image is checked
/// against the loaded module's version id before it is trusted. Reflection is the fallback.
/// </summary>
public static class MethodBodySource
{
    /// <summary>
    /// Opens the body of a method definition.
    /// </summary>
    /// <param name="method">The method, already reduced to its definition.</param>
    /// <param name="resolver">The session's resolver, which knows the images of <c>.load</c>ed assemblies.</param>
    /// <param name="notes">Receives a note when an image was rejected or reflection had to serve.</param>
    /// <returns>The body.</returns>
    /// <exception cref="ReplException">The method has no IL, or nothing could read it.</exception>
    public static MethodBodyImage Open(MethodBase method, TypeResolver resolver, IList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(notes);
        var describe = MemberResolver.Describe(method);
        if (method is DynamicMethod || method.GetType().Name.Contains("RTDynamicMethod", StringComparison.Ordinal))
        {
            throw new ReplException($"{describe} is a dynamic method; its IL cannot be read");
        }

        if (method.IsAbstract)
        {
            throw new ReplException($"{describe} is abstract; there is no body");
        }

        var impl = method.GetMethodImplementationFlags();
        if (method.Attributes.HasFlag(MethodAttributes.PinvokeImpl) || (impl & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL || impl.HasFlag(MethodImplAttributes.InternalCall))
        {
            throw new ReplException($"{describe} is implemented by the runtime; there is no IL");
        }

        var image = FindImage(method, resolver, notes);
        if (image is not null)
        {
            var fromImage = ReadFromImage(method, image.Value.Bytes, image.Value.Stream, describe, notes);
            if (fromImage is not null)
            {
                return fromImage;
            }
        }

        return ReadThroughReflection(method, describe, notes);
    }

    private static (byte[]? Bytes, Stream? Stream)? FindImage(MethodBase method, TypeResolver resolver, IList<string> notes)
    {
        var assembly = method.Module.Assembly;
        if (assembly.IsDynamic)
        {
            return null;
        }

        if (SessionAssemblies.TryGetDefinition(assembly, out var definition) && definition.Image is { } sessionImage)
        {
            return (sessionImage, null);
        }

        if (resolver.TryGetImage(assembly, out var loaded))
        {
            return (loaded, null);
        }

        string? location;
        try
        {
            location = assembly.Location;
        }
        catch (NotSupportedException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(location) || !File.Exists(location))
        {
            return null;
        }

        try
        {
            return (null, File.OpenRead(location));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"the file {location} could not be opened ({ex.Message}); the body was read through reflection");
            return null;
        }
    }

    private static MethodBodyImage? ReadFromImage(MethodBase method, byte[]? bytes, Stream? stream, string describe, IList<string> notes)
    {
        PEReader? pe = null;
        try
        {
            pe = bytes is not null ? new PEReader(ImmutableArray.Create(bytes)) : new PEReader(stream!);
            if (!pe.HasMetadata)
            {
                notes.Add("the image has no metadata; the body was read through reflection");
                pe.Dispose();
                return null;
            }

            var metadata = pe.GetMetadataReader();
            var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            if (mvid != method.Module.ModuleVersionId)
            {
                notes.Add("the image on disk no longer matches the loaded assembly (different module version id); the body was read through reflection");
                pe.Dispose();
                return null;
            }

            var handle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(method.MetadataToken);
            var definition = metadata.GetMethodDefinition(handle);
            if (definition.RelativeVirtualAddress == 0)
            {
                pe.Dispose();
                throw new ReplException($"{describe} has no IL body");
            }

            var body = pe.GetMethodBody(definition.RelativeVirtualAddress);
            var regions = body.ExceptionRegions.Select(r => new RawExceptionRegion(
                r.Kind switch
                {
                    ExceptionRegionKind.Filter => IlClauseKind.Filter,
                    ExceptionRegionKind.Finally => IlClauseKind.Finally,
                    ExceptionRegionKind.Fault => IlClauseKind.Fault,
                    _ => IlClauseKind.Catch,
                },
                r.TryOffset,
                r.TryLength,
                r.HandlerOffset,
                r.HandlerLength,
                r.FilterOffset,
                r.CatchType.IsNil ? 0 : MetadataTokens.GetToken(r.CatchType),
                null)).ToList();
            var localToken = body.LocalSignature.IsNil ? 0 : MetadataTokens.GetToken(body.LocalSignature);
            return new MethodBodyImage(body.GetILBytes() ?? [], body.MaxStack, body.LocalVariablesInitialized, localToken, regions, metadata, pe, "image");
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException or IOException)
        {
            pe?.Dispose();
            notes.Add($"the image could not be read ({ex.Message}); the body was read through reflection");
            return null;
        }
    }

    private static MethodBodyImage ReadThroughReflection(MethodBase method, string describe, IList<string> notes)
    {
        MethodBody? body;
        try
        {
            body = method.GetMethodBody();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException)
        {
            throw new ReplException($"{describe}: reflection could not read the body ({ex.Message})", ex);
        }

        if (body is null)
        {
            throw new ReplException($"{describe} has no IL body");
        }

        var il = body.GetILAsByteArray() ?? [];
        var regions = new List<RawExceptionRegion>();
        foreach (var clause in body.ExceptionHandlingClauses)
        {
            var kind = clause.Flags switch
            {
                ExceptionHandlingClauseOptions.Filter => IlClauseKind.Filter,
                ExceptionHandlingClauseOptions.Finally => IlClauseKind.Finally,
                ExceptionHandlingClauseOptions.Fault => IlClauseKind.Fault,
                _ => IlClauseKind.Catch,
            };
            Type? catchType = null;
            if (kind == IlClauseKind.Catch)
            {
                try
                {
                    catchType = clause.CatchType;
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException or ArgumentException)
                {
                    notes.Add($"the catch type of a clause at {IlReader.LabelFor(clause.HandlerOffset)} could not be resolved ({ex.Message})");
                }
            }

            regions.Add(new RawExceptionRegion(kind, clause.TryOffset, clause.TryLength, clause.HandlerOffset, clause.HandlerLength, kind == IlClauseKind.Filter ? clause.FilterOffset : 0, 0, catchType));
        }

        notes.Add("the body was read through reflection");
        return new MethodBodyImage(il, body.MaxStackSize, body.InitLocals, body.LocalSignatureMetadataToken, regions, ModuleMetadata.TryOpen(method.Module), null, "reflection");
    }
}
