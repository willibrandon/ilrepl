namespace IlRepl.Tests.Engine.Binding;

public sealed partial class SymbolRelationsTests
{
    private sealed class NeedsStruct<T> where T : struct
    {
    }
}
