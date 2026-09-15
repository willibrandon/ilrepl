#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:project ../src/IlRepl.Engine/IlRepl.Engine.csproj

using System.Globalization;
using System.Reflection.Emit;
using System.Text;
using IlRepl.Engine;

var root = FindRepoRoot();
var output = Path.Combine(root, "docs", "src", "content", "docs", "reference", "opcodes.md");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);

var sb = new StringBuilder();
sb.AppendLine("---");
sb.AppendLine("title: Opcodes");
sb.AppendLine("description: Every CIL opcode ilrepl accepts, with its stack transition and operand.");
sb.AppendLine("---");
sb.AppendLine();
sb.AppendLine("This table is generated from the engine's opcode table by `scripts/Generate-OpcodeReference.cs`.");
sb.AppendLine("The stack column reads pops then pushes, using the abbreviations ILAsm uses: `i` for int32 or native int,");
sb.AppendLine("`i8` for int64, `r4` and `r8` for floats, `ref` for an object reference, `1` for any single value, and `…`");
sb.AppendLine("when the count depends on the operand.");
sb.AppendLine();
sb.AppendLine("| Opcode | Stack | Operand | Description |");
sb.AppendLine("| --- | --- | --- | --- |");

var count = 0;
foreach (var name in OpcodeTable.Names)
{
    if (OpcodeTable.IsReserved(name))
    {
        continue;
    }

    var op = OpcodeTable.BySourceName[name];
    var stack = OpcodeTable.StackTransition(op).Replace("  ", " ", StringComparison.Ordinal).Trim();
    sb.Append("| `").Append(name).Append("` | `").Append(stack).Append("` | ")
      .Append(Operand(op)).Append(" | ").Append(OpcodeTable.Describe(op)).AppendLine(" |");
    count++;
}

sb.AppendLine();
sb.Append(count.ToString(CultureInfo.InvariantCulture))
  .AppendLine(" opcodes. `calli` takes a signature, `switch` takes a label list, and the prefixes");
sb.AppendLine("`constrained.`, `no.`, `readonly.`, `tail.`, `unaligned.`, and `volatile.` apply to the next instruction.");

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
        if (File.Exists(Path.Combine(directory, "IlRepl.slnx")))
        {
            return directory;
        }

        directory = Path.GetDirectoryName(directory);
    }

    throw new InvalidOperationException("run this from inside the repository");
}
