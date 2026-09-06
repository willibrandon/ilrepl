#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:project ../src/IlRepl.Engine/IlRepl.Engine.csproj

using System.Globalization;
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

    var op = OpcodeTable.ByName[name];
    var stack = OpcodeTable.StackTransition(op).Replace("  ", " ", StringComparison.Ordinal).Trim();
    sb.Append("| `").Append(name).Append("` | `").Append(stack).Append("` | ")
      .Append(Operand(op.OperandType)).Append(" | ").Append(OpcodeTable.Describe(op)).AppendLine(" |");
    count++;
}

sb.AppendLine();
sb.Append(count.ToString(CultureInfo.InvariantCulture)).AppendLine(" opcodes. `calli` takes a signature, `switch` takes a label list, and the prefixes");
sb.AppendLine("`constrained.`, `unaligned.`, `volatile.`, `tail.`, and `readonly.` apply to the next instruction.");

File.WriteAllText(output, sb.ToString());
Console.WriteLine($"wrote {output} ({count} opcodes)");
return 0;

static string Operand(System.Reflection.Emit.OperandType type) => type switch
{
    System.Reflection.Emit.OperandType.InlineNone => "none",
    System.Reflection.Emit.OperandType.ShortInlineI => "int8",
    System.Reflection.Emit.OperandType.InlineI => "int32",
    System.Reflection.Emit.OperandType.InlineI8 => "int64",
    System.Reflection.Emit.OperandType.ShortInlineR => "float32",
    System.Reflection.Emit.OperandType.InlineR => "float64",
    System.Reflection.Emit.OperandType.InlineString => "string",
    System.Reflection.Emit.OperandType.ShortInlineBrTarget or System.Reflection.Emit.OperandType.InlineBrTarget => "label",
    System.Reflection.Emit.OperandType.InlineSwitch => "labels",
    System.Reflection.Emit.OperandType.ShortInlineVar or System.Reflection.Emit.OperandType.InlineVar => "local or argument",
    System.Reflection.Emit.OperandType.InlineType => "type",
    System.Reflection.Emit.OperandType.InlineMethod => "method",
    System.Reflection.Emit.OperandType.InlineField => "field",
    System.Reflection.Emit.OperandType.InlineTok => "token",
    System.Reflection.Emit.OperandType.InlineSig => "signature",
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
