using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// A method body read back into the shape the listing prints: header facts, locals, the lines,
/// the exception clauses, and everything that went wrong on the way.
/// </summary>
/// <param name="Method">The definition that was read.</param>
/// <param name="Requested">The method the user named, which may be an instantiation of <paramref name="Method"/>.</param>
/// <param name="Header">The <c>.method</c> line without its braces.</param>
/// <param name="MaxStack">The declared maximum stack depth.</param>
/// <param name="InitLocals">True when the header asks for zeroed locals.</param>
/// <param name="Locals">The local types as the metadata spells them, by slot.</param>
/// <param name="Context">The context the stack simulator reads locals, arguments, and generics from.</param>
/// <param name="Entries">The lines, in order.</param>
/// <param name="Clauses">The exception clauses, whether or not the lines draw them as blocks.</param>
/// <param name="Notes">What the reader wants the user to know: the definition shown, a fallback, an operand that did not resolve.</param>
/// <param name="Problems">Faults in the bytes themselves.</param>
/// <param name="CodeSize">The number of IL bytes.</param>
/// <param name="BodySource">Where the bytes came from: <c>image</c> or <c>reflection</c>.</param>
public sealed record DisassembledMethod(
    MethodBase Method,
    MethodBase Requested,
    string Header,
    int MaxStack,
    bool InitLocals,
    IReadOnlyList<IlSignature> Locals,
    ParseContext Context,
    IReadOnlyList<DisassembledEntry> Entries,
    IReadOnlyList<IlExceptionClause> Clauses,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Problems,
    int CodeSize,
    string BodySource);
