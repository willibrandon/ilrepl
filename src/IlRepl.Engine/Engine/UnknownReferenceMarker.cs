namespace IlRepl.Engine;

/// <summary>
/// Marks a stack entry that is an object reference of a type the model does not know.
/// </summary>
/// <remarks>
/// Such an entry is the result of <c>box</c>, or of a load the model cannot type. It renders as <c>object</c>, and unlike a value that is
/// exactly <c>object</c> it may be returned as any reference type. The JIT settles it.
/// </remarks>
public sealed class UnknownReferenceMarker
{
    private UnknownReferenceMarker()
    {
    }
}
