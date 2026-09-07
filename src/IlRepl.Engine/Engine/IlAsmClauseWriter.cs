using System.Text;

namespace IlRepl.Engine;

/// <summary>
/// Writes the body of a disassembled method as native ILAsm: every instruction line with its
/// labels, and the exception clauses in the nesting ilasm reads, where a finally over a try and
/// its catch is an outer <c>.try { .try { } catch { } } finally { }</c>, not the REPL's folded
/// <c>} catch { } finally {</c>. Clauses braces cannot draw follow the body in offset form.
/// </summary>
public static class IlAsmClauseWriter
{
    /// <summary>
    /// Writes the body lines.
    /// </summary>
    /// <param name="method">The disassembled method.</param>
    /// <returns>The lines, indented for placing inside a <c>.method</c> block.</returns>
    public static string Write(DisassembledMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var layout = ClauseLayout.Build(method.Clauses, method.CodeSize, fold: false);
        var targets = new HashSet<int>(layout.ReferencedOffsets);
        foreach (var entry in method.Entries)
        {
            if (entry.Kind == DisassembledEntryKind.Label)
            {
                targets.Add(entry.Offset);
            }
        }

        var sb = new StringBuilder();
        var indent = 2;
        var boundary = 0;
        void Emit(int upTo)
        {
            while (boundary < layout.Boundaries.Count && layout.Boundaries[boundary].Offset <= upTo)
            {
                var b = layout.Boundaries[boundary++];
                if (b.Kind != BlockKind.Try)
                {
                    indent = Math.Max(2, indent - 2);
                }

                var text = b.Kind switch
                {
                    BlockKind.Try => ".try {",
                    BlockKind.Catch => "} catch " + CatchType(b.Clause!) + " {",
                    BlockKind.Filter => "} filter {",
                    BlockKind.FilterHandler => "} {",
                    BlockKind.Finally => "} finally {",
                    BlockKind.Fault => "} fault {",
                    _ => "}",
                };
                sb.Append(' ', indent).AppendLine(text);
                if (b.Kind != BlockKind.End)
                {
                    indent += 2;
                }
            }
        }

        foreach (var entry in method.Entries)
        {
            if (entry.Kind is not (DisassembledEntryKind.Instruction or DisassembledEntryKind.Raw))
            {
                continue;
            }

            Emit(entry.Offset);
            if (targets.Contains(entry.Offset))
            {
                sb.Append(IlReader.LabelFor(entry.Offset)).AppendLine(":");
            }

            sb.Append(' ', indent).AppendLine(entry.DisplayText);
        }

        Emit(method.CodeSize);
        if (targets.Contains(method.CodeSize))
        {
            sb.Append(IlReader.LabelFor(method.CodeSize)).AppendLine(":");
        }

        foreach (var clause in layout.Fallback)
        {
            sb.Append(' ', 2).AppendLine(clause.Describe());
        }

        return sb.ToString();
    }

    private static string CatchType(IlExceptionClause clause)
    {
        if (clause.CatchSignature is { } signature)
        {
            return IlSignatureRenderer.Declaring(signature);
        }

        return clause.CatchType is { } type ? TypeNameFormatter.IlAsmDeclaring(type) : $"0x{clause.CatchToken:x8}";
    }
}
