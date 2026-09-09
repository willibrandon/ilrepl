using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// The declaration and prototype builder retained for an accepted runtime field reference.
/// </summary>
/// <param name="Declaration">The field declaration.</param>
/// <param name="Builder">The prototype builder.</param>
internal sealed record RuntimeDeclaredField(FieldDeclaration Declaration, FieldInfo Builder);
