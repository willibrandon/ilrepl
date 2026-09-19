namespace IlRepl.Tests.Engine.Binding;

public sealed partial class SymbolRelationsTests
{
    private sealed class Chain<T, S> where T : Stream where S : T
    {
    }
}
