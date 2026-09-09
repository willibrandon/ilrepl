namespace IlRepl.Tests.Engine.Binding;

public sealed partial class SymbolRelationsTests
{
    private sealed class NeedsClass<T> where T : class
    {
    }
}
