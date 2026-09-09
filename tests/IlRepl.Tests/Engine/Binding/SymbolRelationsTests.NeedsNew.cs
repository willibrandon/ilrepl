namespace IlRepl.Tests.Engine.Binding;

public sealed partial class SymbolRelationsTests
{
    private sealed class NeedsNew<T> where T : new()
    {
    }
}
