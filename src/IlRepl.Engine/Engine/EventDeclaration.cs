using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// An event declared with <c>.event</c>: its handler type and the accessor methods named by
/// <c>.addon</c>, <c>.removeon</c>, and <c>.fire</c>.
/// </summary>
/// <param name="Name">The event name.</param>
/// <param name="HandlerType">The delegate type.</param>
/// <param name="Attributes">The event attributes (<c>specialname</c>).</param>
/// <param name="AddOn">The add accessor.</param>
/// <param name="RemoveOn">The remove accessor.</param>
/// <param name="Fire">The raise accessor, or null.</param>
/// <param name="CustomAttributes">The custom attributes declared on the event.</param>
/// <param name="HeaderLine">The <c>.event</c> line as typed.</param>
/// <param name="Lines">The lines of the block as typed.</param>
public sealed record EventDeclaration(
    string Name,
    Type HandlerType,
    EventAttributes Attributes,
    MethodDeclaration AddOn,
    MethodDeclaration RemoveOn,
    MethodDeclaration? Fire,
    IReadOnlyList<CustomAttributeDeclaration> CustomAttributes,
    string HeaderLine,
    IReadOnlyList<string> Lines)
{
    /// <summary>
    /// Renders the event the way a listing shows it.
    /// </summary>
    /// <returns>The description, for example <c>event EventHandler Changed { add, remove }</c>.</returns>
    public string Describe()
    {
        var accessors = new List<string> { "add", "remove" };
        if (Fire is not null)
        {
            accessors.Add("fire");
        }

        return $"event {TypeNameFormatter.Pretty(HandlerType)} {Name} {{ {string.Join(", ", accessors)} }}";
    }
}
