namespace IlRepl.Engine.Binding;

/// <summary>
/// What an <c>ldtoken</c> operand names, which decides the handle it pushes.
/// </summary>
public enum StackTokenKind
{
    /// <summary>
    /// A type: <c>RuntimeTypeHandle</c>.
    /// </summary>
    Type,

    /// <summary>
    /// A field: <c>RuntimeFieldHandle</c>.
    /// </summary>
    Field,

    /// <summary>
    /// A method: <c>RuntimeMethodHandle</c>.
    /// </summary>
    Method,
}
