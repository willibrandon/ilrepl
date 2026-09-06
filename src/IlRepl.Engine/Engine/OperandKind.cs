namespace IlRepl.Engine;

/// <summary>
/// The kind of operand an <see cref="Instruction"/> carries, which decides which
/// <c>ILGenerator.Emit</c> overload is used.
/// </summary>
public enum OperandKind
{
    /// <summary>
    /// No operand.
    /// </summary>
    None,

    /// <summary>
    /// A signed byte immediate (<c>ldc.i4.s</c>).
    /// </summary>
    SByte,

    /// <summary>
    /// An unsigned byte immediate (<c>unaligned.</c>).
    /// </summary>
    Byte,

    /// <summary>
    /// A 32-bit integer immediate.
    /// </summary>
    Int32,

    /// <summary>
    /// A 64-bit integer immediate.
    /// </summary>
    Int64,

    /// <summary>
    /// A 32-bit float immediate.
    /// </summary>
    Single,

    /// <summary>
    /// A 64-bit float immediate.
    /// </summary>
    Double,

    /// <summary>
    /// A string literal.
    /// </summary>
    String,

    /// <summary>
    /// A branch target label name.
    /// </summary>
    Label,

    /// <summary>
    /// A <c>switch</c> table of label names.
    /// </summary>
    Labels,

    /// <summary>
    /// A local variable index.
    /// </summary>
    Local,

    /// <summary>
    /// A cell argument index.
    /// </summary>
    Argument,

    /// <summary>
    /// A type.
    /// </summary>
    Type,

    /// <summary>
    /// A resolved method or constructor.
    /// </summary>
    Method,

    /// <summary>
    /// A field.
    /// </summary>
    Field,

    /// <summary>
    /// A metadata token: a type, method, or field for <c>ldtoken</c>.
    /// </summary>
    Token,

    /// <summary>
    /// A <c>calli</c> signature.
    /// </summary>
    Signature,
}
