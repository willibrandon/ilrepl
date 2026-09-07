using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// A method, constructor, or type initializer declared inside a <c>.class</c> block.
/// </summary>
/// <param name="Signature">The signature, including the ILAsm attributes it was declared with.</param>
/// <param name="Overrides">The <c>.override</c> lines written inside the body.</param>
/// <param name="HeaderLine">The <c>.method</c> line as typed.</param>
/// <param name="BodyLines">The body lines as typed, without the closing brace.</param>
/// <param name="Body">The validated body, or null for an abstract method.</param>
public sealed record MethodDeclaration(
    MethodSignature Signature,
    IReadOnlyList<OverrideDeclaration> Overrides,
    string HeaderLine,
    IReadOnlyList<string> BodyLines,
    CellState? Body)
{
    /// <summary>
    /// The method name.
    /// </summary>
    public string Name => Signature.Name;

    /// <summary>
    /// True for a static method or a type initializer.
    /// </summary>
    public bool IsStatic => Signature.IsStatic;

    /// <summary>
    /// True for <c>.ctor</c>.
    /// </summary>
    public bool IsConstructor => Signature.Name == ".ctor";

    /// <summary>
    /// True for <c>.cctor</c>.
    /// </summary>
    public bool IsTypeInitializer => Signature.Name == ".cctor";

    /// <summary>
    /// True for a virtual method.
    /// </summary>
    public bool IsVirtual => Signature.Attributes.HasFlag(MethodAttributes.Virtual);

    /// <summary>
    /// True for an abstract method, which has no body.
    /// </summary>
    public bool IsAbstract => Signature.Attributes.HasFlag(MethodAttributes.Abstract);

    /// <summary>
    /// Renders the method the way a listing shows it, for example <c>instance int32 Sum()</c>.
    /// </summary>
    /// <returns>The description.</returns>
    public string Describe() => Signature.DescribeMember();
}
