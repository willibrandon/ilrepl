using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// The declaration and prototype builder retained for an accepted runtime member reference.
/// </summary>
/// <param name="Signature">The declared signature.</param>
/// <param name="Builder">The prototype builder.</param>
internal sealed record RuntimeDeclaredMember(MethodSignature Signature, MethodBase Builder);
