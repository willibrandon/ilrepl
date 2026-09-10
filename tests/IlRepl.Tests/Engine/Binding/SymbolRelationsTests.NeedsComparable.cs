namespace IlRepl.Tests.Engine.Binding;

public sealed partial class SymbolRelationsTests
{
    private sealed class NeedsComparable<T> where T : IComparable<T>
    {
    }
}
