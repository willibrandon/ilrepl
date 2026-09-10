namespace IlRepl.Tests.Engine.Binding;

public sealed partial class SymbolRelationsTests
{
    private sealed class NeedsStream<T> where T : System.IO.Stream
    {
    }
}
