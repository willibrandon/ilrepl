namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Supplies the runtime oracle for a generic parameter requiring a reference type.
/// </summary>
/// <typeparam name="T">The required reference type.</typeparam>
internal sealed class ReferenceConstraint<T> where T : class
{
}
