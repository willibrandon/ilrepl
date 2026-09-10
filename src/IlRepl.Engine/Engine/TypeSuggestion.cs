using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// A type offered for a mistyped name, with the spelling that binds to it.
/// </summary>
/// <param name="Entry">The index entry.</param>
/// <param name="Spelling">The shortest spelling that binds: a short name, a qualified path, or an assembly-qualified path.</param>
public sealed record TypeSuggestion(TypeIndexEntry Entry, string Spelling);
