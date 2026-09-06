namespace IlRepl.Engine;

/// <summary>
/// Marks a stack entry that is an object reference of a type the model does not know: the
/// result of <c>box</c>, or of a load it cannot type. It renders as <c>object</c>, and unlike a
/// value that is exactly <c>object</c> it may be returned as any reference type; the JIT settles it.
/// </summary>
public sealed class UnknownReferenceMarker
{
    private UnknownReferenceMarker()
    {
    }
}
