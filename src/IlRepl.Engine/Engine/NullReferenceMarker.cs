namespace IlRepl.Engine;

/// <summary>
/// Marks a stack entry pushed by <c>ldnull</c>. It is a reference of no particular type, and
/// it does not need boxing when returned.
/// </summary>
public sealed class NullReferenceMarker
{
    private NullReferenceMarker()
    {
    }
}
