namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Supplies the runtime oracle for a generic parameter requiring a public default constructor.
/// </summary>
/// <typeparam name="T">The type with a public default constructor.</typeparam>
internal sealed class ConstructorConstraint<T> where T : new()
{
}
