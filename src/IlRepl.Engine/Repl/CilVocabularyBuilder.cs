using System.Reflection.Emit;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Builds the tokenizer's vocabulary from the engine's own tables, so the words the front-end
/// colours are the words the engine accepts: every opcode with its operand kind, the directives
/// the REPL and a listing can show, the commands and their aliases, the ILAsm keywords, and the
/// primitive type names.
/// </summary>
public static class CilVocabularyBuilder
{
    // Dot-words from il_kywd.h that a listing may show though the prompt never takes them.
    private static readonly string[] ListingDirectives =
    [
        ".assembly", ".module", ".entrypoint", ".namespace", ".ver", ".publickey", ".publickeytoken", ".hash", ".locale",
        ".line", ".language", ".data", ".emitbyte", ".zeroinit", ".permission", ".permissionset", ".export", ".file",
        ".mresource", ".manifestres", ".subsystem", ".corflags", ".imagebase", ".stackreserve", ".vtfixup", ".vtentry",
        ".typedef", ".this", ".base", ".nester", ".interfaceimpl", ".mscorlib", ".template", ".typelist", ".vtable",
    ];

    // Spellings ReplCore.Command accepts beside the names the completer lists.
    private static readonly string[] CommandAliases = [".h", ".?", ".exit", ".q", ".list", ".ls", ".disassemble", ".u"];

    /// <summary>
    /// The vocabulary, built once.
    /// </summary>
    public static CilVocabulary Vocabulary { get; } = Build();

    private static CilVocabulary Build()
    {
        var opcodes = new Dictionary<string, CilOperandKind>(StringComparer.Ordinal);
        foreach (var name in OpcodeTable.Names)
        {
            if (!OpcodeTable.IsReserved(name))
            {
                opcodes[name] = Kind(OpcodeTable.ByName[name].OperandType);
            }
        }

        opcodes["no."] = CilOperandKind.Integer;
        var directives = ReplDirectives.Names.Concat(ListingDirectives).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // The completer lists directives beside commands; the core routes a directive before it
        // looks for a command, so here a word is one or the other.
        var commands = Completer.Commands.Select(c => c.Name).Concat(CommandAliases).Except(ReplDirectives.Names, StringComparer.Ordinal).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var keywords = TypeNameFormatter.IlAsmKeywordNames.Order(StringComparer.Ordinal).ToArray();
        var primitives = TypeParser.PrimitiveKeywords.Order(StringComparer.Ordinal).ToArray();
        return new CilVocabulary(opcodes, directives, commands, keywords, primitives);
    }

    private static CilOperandKind Kind(OperandType type) => type switch
    {
        OperandType.ShortInlineBrTarget or OperandType.InlineBrTarget => CilOperandKind.Branch,
        OperandType.InlineSwitch => CilOperandKind.Switch,
        OperandType.ShortInlineI or OperandType.InlineI or OperandType.InlineI8 => CilOperandKind.Integer,
        OperandType.ShortInlineR or OperandType.InlineR => CilOperandKind.Float,
        OperandType.ShortInlineVar or OperandType.InlineVar => CilOperandKind.Variable,
        OperandType.InlineString => CilOperandKind.String,
        OperandType.InlineType => CilOperandKind.Type,
        OperandType.InlineMethod => CilOperandKind.Member,
        OperandType.InlineField => CilOperandKind.Field,
        OperandType.InlineTok => CilOperandKind.Token,
        OperandType.InlineSig => CilOperandKind.Signature,
        _ => CilOperandKind.None,
    };
}
