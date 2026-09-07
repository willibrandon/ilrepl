using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace IlRepl.Engine;

/// <summary>
/// A method body as read for a listing: the IL, the header facts, the exception clauses, and the
/// metadata reader for the module when the body came from an image. Disposed when the command is done.
/// </summary>
public sealed class MethodBodyImage : IDisposable
{
    private readonly PEReader? _pe;

    internal MethodBodyImage(byte[] il, int maxStack, bool initLocals, int localSignatureToken, IReadOnlyList<RawExceptionRegion> regions, MetadataReader? metadata, PEReader? pe, string source)
    {
        Il = il;
        MaxStack = maxStack;
        InitLocals = initLocals;
        LocalSignatureToken = localSignatureToken;
        Regions = regions;
        Metadata = metadata;
        _pe = pe;
        Source = source;
    }

    /// <summary>
    /// The IL bytes.
    /// </summary>
    public byte[] Il { get; }

    /// <summary>
    /// The declared maximum stack depth.
    /// </summary>
    public int MaxStack { get; }

    /// <summary>
    /// True when the header asks for locals to be zeroed.
    /// </summary>
    public bool InitLocals { get; }

    /// <summary>
    /// The local signature token, or 0 when the body declares no locals.
    /// </summary>
    public int LocalSignatureToken { get; }

    /// <summary>
    /// The exception clauses as encoded.
    /// </summary>
    public IReadOnlyList<RawExceptionRegion> Regions { get; }

    /// <summary>
    /// The module's metadata, or null when nothing but reflection is available.
    /// </summary>
    public MetadataReader? Metadata { get; }

    /// <summary>
    /// Where the body came from: <c>image</c> or <c>reflection</c>.
    /// </summary>
    public string Source { get; }

    /// <summary>
    /// True when the body was read from the PE image rather than through reflection.
    /// </summary>
    public bool FromImage => _pe is not null;

    /// <inheritdoc/>
    public void Dispose() => _pe?.Dispose();
}
