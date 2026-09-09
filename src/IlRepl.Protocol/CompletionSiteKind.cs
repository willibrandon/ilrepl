using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// What the caret is on in an operand, which decides what the palette lists there.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CompletionSiteKind>))]
public enum CompletionSiteKind
{
    /// <summary>
    /// Nothing to complete here: the first word, a literal, a keyword, a comment, a name being declared.
    /// </summary>
    None,

    /// <summary>
    /// A type: after a type opcode, in a declaration, in a parameter list, after <c>extends</c>.
    /// </summary>
    Type,

    /// <summary>
    /// The head of a member reference: the declaring type before a <c>::</c>, or a session method's bare name.
    /// </summary>
    MemberHead,

    /// <summary>
    /// A method name after <c>::</c>.
    /// </summary>
    Method,

    /// <summary>
    /// A constructor after <c>::</c>, for <c>newobj</c> and <c>.custom</c>.
    /// </summary>
    Constructor,

    /// <summary>
    /// A field name after <c>::</c>.
    /// </summary>
    Field,

    /// <summary>
    /// A local, by name or index.
    /// </summary>
    Local,

    /// <summary>
    /// An argument, by name or index.
    /// </summary>
    Argument,

    /// <summary>
    /// A branch target or a <c>switch</c> entry.
    /// </summary>
    Label,

    /// <summary>
    /// A generic parameter reference, <c>!N</c> or <c>!!N</c>.
    /// </summary>
    GenericParameter,

    /// <summary>
    /// A type argument inside <c>&lt;...&gt;</c>, of a type or of a method.
    /// </summary>
    TypeArgument,

    /// <summary>
    /// The position right after a generic method's closed <c>&gt;</c>, where its parameter list goes.
    /// </summary>
    Signature,
}
