namespace IlRepl.Engine;

/// <summary>
/// What a method body inside a <c>.class</c> block knows about its type: the type being written,
/// the type of <c>this</c> for an instance member, and whether the body is allowed at all.
/// </summary>
/// <param name="Owner">The prototype of the type being written.</param>
/// <param name="Header">The type's header.</param>
/// <param name="ThisType">The type of argument 0 for an instance member: the type, or a reference to it for a struct; null for a static member.</param>
/// <param name="IsAbstract">True when the method is abstract and may have no body.</param>
/// <param name="Path">The ILAsm path of the type, for messages.</param>
/// <param name="KindWord">The word for the type's kind: class, struct, interface, or enum.</param>
public sealed record MemberContext(Type Owner, TypeHeader Header, Type? ThisType, bool IsAbstract, string Path, string KindWord)
{
    /// <summary>
    /// The access scope of a body in this type.
    /// </summary>
    public AccessScope Scope => new(Owner, KindWord + " " + Path);
}
