using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Parses the operand of <c>calli</c>: <c>[instance] [vararg] RetType(Params)</c> for managed
/// pointers and <c>unmanaged [cdecl|stdcall|thiscall|fastcall] RetType(Params)</c> for native ones.
/// </summary>
public static class CalliSignatureParser
{
    /// <summary>
    /// Parses a calli signature.
    /// </summary>
    /// <param name="spec">The signature text.</param>
    /// <param name="context">The parse context.</param>
    /// <returns>The parsed signature.</returns>
    /// <exception cref="ReplException">The signature is malformed.</exception>
    public static CalliSignature Parse(string spec, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        var syntax = CilSyntaxParser.ParseCalliSignature(TypeParser.Normalize(spec).Trim());
        var scope = new RuntimeBindingScope(context);
        return new RuntimeBindingAdapter(scope).ToCalliSignature(SymbolBinder.BindSignature(syntax, scope));
    }
}
