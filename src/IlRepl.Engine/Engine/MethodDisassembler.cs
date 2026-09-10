using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// Reads a method body back into a listing: the bytes through <see cref="IlReader"/>, operands
/// through the module's metadata and its runtime resolution, exception clauses into blocks, and
/// every instruction into the same <see cref="Instruction"/> shape the stack simulator applies.
/// A token that does not resolve becomes a raw line and a note; the listing goes on.
/// </summary>
public static class MethodDisassembler
{
    /// <summary>
    /// Disassembles a method.
    /// </summary>
    /// <param name="requested">The method as the user named it; an instantiation is read through its definition.</param>
    /// <param name="session">The session, for its resolver, its methods, and its types.</param>
    /// <returns>The listing.</returns>
    /// <exception cref="ReplException">The method has no IL, or nothing could read it.</exception>
    public static DisassembledMethod Disassemble(MethodBase requested, Session session)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(session);
        var notes = new List<string>();
        var definition = IlAsmRenderer.DefinitionOf(requested);
        if (!ReferenceEquals(definition, requested) && definition != requested)
        {
            notes.Add($"showing the definition {MemberResolver.Describe(definition)}; the instantiation shares its body");
        }

        using var body = MethodBodySource.Open(definition, session.Resolver, notes);
        return new MethodBodyReader(definition, requested, session, body, notes).Read();
    }
}
