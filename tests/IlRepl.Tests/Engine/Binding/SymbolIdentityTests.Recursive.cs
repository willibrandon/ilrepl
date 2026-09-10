namespace IlRepl.Tests.Engine.Binding;

public sealed partial class SymbolIdentityTests
{
    private sealed class Recursive<T> where T : IComparable<T>
    {
    }
}
