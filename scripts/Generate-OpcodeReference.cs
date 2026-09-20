#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:project ../src/IlRepl.Engine/IlRepl.Engine.csproj

using System.Reflection.Emit;
using System.Text;
using IlRepl.Engine;

var root = FindRepoRoot();
var output = Path.Join(root, "docs", "src", "content", "docs", "reference", "opcodes.md");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);

var sb = new StringBuilder();
sb.AppendLine("---");
sb.AppendLine("title: Opcodes");
sb.AppendLine("description: Every CIL opcode ilrepl accepts, with its stack transition and operand.");
sb.AppendLine("---");
sb.AppendLine();
sb.AppendLine("Find an instruction's stack effect, behavior, and operand below. F1 shows the same help in the terminal.");
sb.AppendLine("The stack column reads pops then pushes, using the abbreviations ILAsm uses: `i` for int32 or native int,");
sb.AppendLine("`i8` for int64, `r4` and `r8` for floats, `ref` for an object reference, `1` for any single value, and `…`");
sb.AppendLine("when the count depends on the operand.");
sb.AppendLine();
sb.AppendLine("| Opcode | Stack | Operand |");
sb.AppendLine("| --- | --- | --- |");

var count = 0;
foreach (var name in OpcodeTable.Names)
{
    if (OpcodeTable.IsReserved(name))
    {
        continue;
    }

    var op = OpcodeTable.BySourceName[name];
    var stack = OpcodeTable.StackTransition(op).Replace("  ", " ", StringComparison.Ordinal).Trim();
    sb.Append("| [`").Append(name).Append("`](#").Append(InstructionReference.Anchor(name)).Append(") | `").Append(stack).Append("` | ")
      .Append(Operand(op)).AppendLine(" |");
    count++;
}

foreach (var name in OpcodeTable.Names.Where(name => !OpcodeTable.IsReserved(name)))
{
    var help = InstructionReference.For(name);
    sb.AppendLine();
    sb.Append("<h2 id=\"").Append(InstructionReference.Anchor(name)).Append("\"><code>").Append(name).AppendLine("</code></h2>");
    sb.AppendLine();
    sb.Append('`').Append(help.Syntax).AppendLine("`");
    sb.AppendLine();
    sb.Append("Stack: `").Append(help.StackEffect).AppendLine("`");
    sb.AppendLine();
    sb.AppendLine(help.Explanation);
    foreach (var note in help.Notes)
    {
        sb.AppendLine();
        sb.AppendLine(note);
    }

    sb.AppendLine();
    sb.Append('[').Append(name == "no." ? "CLI specification" : "Microsoft reference")
        .Append("](").Append(InstructionReference.ExternalUrl(name)).AppendLine(")");
}

File.WriteAllText(output, sb.ToString());
Console.WriteLine($"wrote {output} ({count} opcodes)");
return 0;

static string Operand(IlOpcode opcode) => opcode.IsSkipChecksPrefix ? "mask" : OperandName(opcode.OperandType);

static string OperandName(OperandType type) => type switch
{
    OperandType.InlineNone => "none",
    OperandType.ShortInlineI => "int8",
    OperandType.InlineI => "int32",
    OperandType.InlineI8 => "int64",
    OperandType.ShortInlineR => "float32",
    OperandType.InlineR => "float64",
    OperandType.InlineString => "string",
    OperandType.ShortInlineBrTarget or OperandType.InlineBrTarget => "label",
    OperandType.InlineSwitch => "labels",
    OperandType.ShortInlineVar or OperandType.InlineVar => "local or argument",
    OperandType.InlineType => "type",
    OperandType.InlineMethod => "method",
    OperandType.InlineField => "field",
    OperandType.InlineTok => "token",
    OperandType.InlineSig => "signature",
    _ => type.ToString(),
};

static string FindRepoRoot()
{
    var directory = Directory.GetCurrentDirectory();
    while (directory is not null)
    {
        if (File.Exists(Path.Join(directory, "IlRepl.slnx")))
        {
            return directory;
        }

        directory = Path.GetDirectoryName(directory);
    }

    throw new InvalidOperationException("run this from inside the repository");
}
