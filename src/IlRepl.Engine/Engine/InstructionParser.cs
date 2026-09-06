using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Turns one line of IL text into an <see cref="Instruction"/>: it strips comments, peels off
/// labels, looks up the opcode, and parses the operand in whatever form that opcode takes.
/// </summary>
public static class InstructionParser
{
    /// <summary>
    /// Removes <c>//</c> and <c>/* */</c> comments, leaving string literals untouched.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The line without comments.</returns>
    public static string StripComments(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var result = new System.Text.StringBuilder(line.Length);
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inString)
            {
                result.Append(c);
                if (c == '\\' && i + 1 < line.Length)
                {
                    result.Append(line[++i]);
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                result.Append(c);
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                break;
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                var end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    break;
                }

                i = end + 1;
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Splits leading labels from the rest of the line. <c>L1: L2: add</c> defines <c>L1</c> and <c>L2</c>.
    /// </summary>
    /// <param name="text">The line without comments.</param>
    /// <returns>The labels and the remaining text.</returns>
    public static (List<string> Labels, string Remainder) SplitLabels(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var labels = new List<string>();
        text = text.Trim();
        while (true)
        {
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                break;
            }

            var candidate = text[..colon];
            if (!IsIdentifier(candidate))
            {
                break;
            }

            if (colon + 1 < text.Length && text[colon + 1] == ':')
            {
                break;
            }

            labels.Add(candidate);
            text = text[(colon + 1)..].TrimStart();
        }

        return (labels, text);
    }

    /// <summary>
    /// True when <paramref name="s"/> is a label or local name: a letter or underscore followed by letters, digits, or underscores.
    /// </summary>
    /// <param name="s">The candidate name.</param>
    /// <returns>True for an identifier.</returns>
    public static bool IsIdentifier(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_'))
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses an instruction (opcode plus operand) with no labels or comments.
    /// </summary>
    /// <param name="text">The instruction text.</param>
    /// <param name="context">The parse context.</param>
    /// <returns>The instruction.</returns>
    /// <exception cref="ReplException">The opcode is unknown or the operand is invalid.</exception>
    public static Instruction Parse(string text, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(context);
        text = text.Trim();
        var space = IndexOfWhitespace(text);
        var mnemonic = space < 0 ? text : text[..space];
        var operandText = space < 0 ? "" : text[(space + 1)..].Trim();

        if (mnemonic == "no.")
        {
            throw new ReplException("the 'no.' prefix has no ILGenerator representation and cannot be emitted");
        }

        if (!OpcodeTable.TryGet(mnemonic, out var op) || op.Name is null || OpcodeTable.IsReserved(op.Name))
        {
            var suggestion = Suggest(mnemonic);
            throw new ReplException($"unknown opcode '{mnemonic}'" + (suggestion is null ? "" : $" (did you mean '{suggestion}'?)"));
        }

        var opName = op.Name;
        var implicitLocal = opName switch
        {
            "ldloc.0" or "stloc.0" => 0,
            "ldloc.1" or "stloc.1" => 1,
            "ldloc.2" or "stloc.2" => 2,
            "ldloc.3" or "stloc.3" => 3,
            _ => (int?)null,
        };
        if (implicitLocal is int localIndex)
        {
            if (localIndex >= context.Locals.Count)
            {
                throw new ReplException($"local {localIndex} is not declared (declare it with .locals)");
            }

            RequireNoOperand(op, operandText);
            return new Instruction { Op = op, Text = text, Kind = OperandKind.None, LocalIndex = localIndex };
        }

        var implicitArgument = opName switch
        {
            "ldarg.0" => 0,
            "ldarg.1" => 1,
            "ldarg.2" => 2,
            "ldarg.3" => 3,
            _ => (int?)null,
        };
        if (implicitArgument is int argumentIndex)
        {
            if (argumentIndex >= context.Arguments.Count)
            {
                throw new ReplException($"argument {argumentIndex} is not declared (declare it with .args)");
            }

            RequireNoOperand(op, operandText);
            return new Instruction { Op = op, Text = text, Kind = OperandKind.None, ArgumentIndex = argumentIndex };
        }

        if (opName == "arglist")
        {
            RequireNoOperand(op, operandText);
            return new Instruction { Op = op, Text = text };
        }

        switch (op.OperandType)
        {
            case OperandType.InlineNone:
                RequireNoOperand(op, operandText);
                return new Instruction { Op = op, Text = text };

            case OperandType.ShortInlineI:
                if (opName == "ldc.i4.s")
                {
                    var v = LiteralParser.ParseInteger(operandText, opName);
                    if (v is < sbyte.MinValue or > sbyte.MaxValue)
                    {
                        throw new ReplException($"{v} does not fit ldc.i4.s (int8); use ldc.i4");
                    }

                    return new Instruction { Op = op, Text = text, Kind = OperandKind.SByte, Operand = (sbyte)v };
                }

                {
                    var v = LiteralParser.ParseInteger(operandText, opName);
                    if (v is < byte.MinValue or > byte.MaxValue)
                    {
                        throw new ReplException($"{v} does not fit an unsigned byte operand");
                    }

                    return new Instruction { Op = op, Text = text, Kind = OperandKind.Byte, Operand = (byte)v };
                }

            case OperandType.InlineI:
            {
                var v = LiteralParser.ParseInteger(operandText, opName);
                if (v is < int.MinValue or > uint.MaxValue)
                {
                    throw new ReplException($"{v} does not fit int32; use ldc.i8");
                }

                return new Instruction { Op = op, Text = text, Kind = OperandKind.Int32, Operand = unchecked((int)v) };
            }

            case OperandType.InlineI8:
                return new Instruction { Op = op, Text = text, Kind = OperandKind.Int64, Operand = LiteralParser.ParseInteger(operandText, opName) };

            case OperandType.ShortInlineR:
                return new Instruction { Op = op, Text = text, Kind = OperandKind.Single, Operand = (float)LiteralParser.ParseFloat(operandText, opName) };

            case OperandType.InlineR:
                return new Instruction { Op = op, Text = text, Kind = OperandKind.Double, Operand = LiteralParser.ParseFloat(operandText, opName) };

            case OperandType.InlineString:
                if (operandText.Length == 0)
                {
                    throw new ReplException("ldstr needs a string operand, e.g. ldstr \"hello\"");
                }

                return new Instruction { Op = op, Text = text, Kind = OperandKind.String, Operand = LiteralParser.ParseString(operandText) };

            case OperandType.ShortInlineBrTarget:
            case OperandType.InlineBrTarget:
                if (!IsIdentifier(operandText))
                {
                    throw new ReplException($"'{opName}' needs a label name, e.g. {opName} LOOP");
                }

                return new Instruction { Op = op, Text = text, Kind = OperandKind.Label, Operand = operandText };

            case OperandType.InlineSwitch:
            {
                var inner = operandText.Trim();
                if (inner.StartsWith('(') && inner.EndsWith(')'))
                {
                    inner = inner[1..^1];
                }

                var labels = inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (labels.Length == 0 || labels.Any(l => !IsIdentifier(l)))
                {
                    throw new ReplException("switch needs a list of labels: switch (A, B, C)");
                }

                return new Instruction { Op = op, Text = text, Kind = OperandKind.Labels, Operand = labels };
            }

            case OperandType.ShortInlineVar:
            case OperandType.InlineVar:
                if (opName.StartsWith("ldarg", StringComparison.Ordinal) || opName.StartsWith("starg", StringComparison.Ordinal))
                {
                    var index = ResolveArgument(operandText, context);
                    return new Instruction { Op = op, Text = text, Kind = OperandKind.Argument, Operand = index, ArgumentIndex = index };
                }

                {
                    var index = ResolveLocal(operandText, context);
                    return new Instruction { Op = op, Text = text, Kind = OperandKind.Local, Operand = index, LocalIndex = index };
                }

            case OperandType.InlineType:
                if (operandText.Length == 0)
                {
                    throw new ReplException($"'{opName}' needs a type operand");
                }

                return new Instruction { Op = op, Text = text, Kind = OperandKind.Type, Operand = TypeParser.Parse(operandText, context) };

            case OperandType.InlineMethod:
                if (operandText.Length == 0)
                {
                    throw new ReplException($"'{opName}' needs a method reference, e.g. {opName} void Console::WriteLine(string)");
                }

                return new Instruction
                {
                    Op = op,
                    Text = text,
                    Kind = OperandKind.Method,
                    Operand = MemberResolver.ResolveMethod(operandText, context, op == OpCodes.Newobj),
                };

            case OperandType.InlineField:
                if (operandText.Length == 0)
                {
                    throw new ReplException($"'{opName}' needs a field reference, e.g. {opName} string String::Empty");
                }

                return new Instruction { Op = op, Text = text, Kind = OperandKind.Field, Operand = MemberResolver.ResolveField(operandText, context) };

            case OperandType.InlineTok:
            {
                object token;
                if (operandText.StartsWith("method ", StringComparison.Ordinal))
                {
                    token = MemberResolver.ResolveMethod(operandText[7..], context, wantConstructor: false).Method;
                }
                else if (operandText.StartsWith("field ", StringComparison.Ordinal))
                {
                    token = MemberResolver.ResolveField(operandText[6..], context);
                }
                else
                {
                    token = TypeParser.Parse(operandText, context);
                }

                return new Instruction { Op = op, Text = text, Kind = OperandKind.Token, Operand = token };
            }

            case OperandType.InlineSig:
                return new Instruction { Op = op, Text = text, Kind = OperandKind.Signature, Operand = CalliSignatureParser.Parse(operandText, context) };

            default:
                throw new ReplException($"unsupported operand type {op.OperandType} for '{opName}'");
        }
    }

    /// <summary>
    /// Resolves a local operand written as a name or an index.
    /// </summary>
    /// <param name="operand">The operand text.</param>
    /// <param name="context">The parse context.</param>
    /// <returns>The local index.</returns>
    /// <exception cref="ReplException">No such local is declared.</exception>
    public static int ResolveLocal(string operand, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(operand);
        ArgumentNullException.ThrowIfNull(context);
        var locals = context.Locals;
        if (operand.Length == 0)
        {
            throw new ReplException("expected a local name or index");
        }

        if (int.TryParse(operand, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            if (index < 0 || index >= locals.Count)
            {
                throw new ReplException($"local {index} is not declared ({locals.Count} declared; use .locals)");
            }

            return index;
        }

        var name = Unquote(operand);
        for (var i = 0; i < locals.Count; i++)
        {
            if (locals[i].Name == name)
            {
                return i;
            }
        }

        throw new ReplException(locals.Count == 0
            ? $"no local '{name}'; declare one with: .locals init (int32 {name})"
            : $"no local '{name}'; declared: {string.Join(", ", locals.Select((l, i) => $"{i}:{l.Name ?? "?"}"))}");
    }

    /// <summary>
    /// Resolves an argument operand written as a name or an index.
    /// </summary>
    /// <param name="operand">The operand text.</param>
    /// <param name="context">The parse context.</param>
    /// <returns>The argument index.</returns>
    /// <exception cref="ReplException">No such argument is declared.</exception>
    public static int ResolveArgument(string operand, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(operand);
        ArgumentNullException.ThrowIfNull(context);
        var arguments = context.Arguments;
        if (operand.Length == 0)
        {
            throw new ReplException("expected an argument name or index");
        }

        if (int.TryParse(operand, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            if (index < 0 || index >= arguments.Count)
            {
                throw new ReplException($"argument {index} is not declared ({arguments.Count} declared; use .args)");
            }

            return index;
        }

        var name = Unquote(operand);
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Name == name)
            {
                return i;
            }
        }

        throw new ReplException(arguments.Count == 0
            ? $"no argument '{name}'; declare one with: .args (int32 {name} = 0)"
            : $"no argument '{name}'; declared: {string.Join(", ", arguments.Select((a, i) => $"{i}:{a.Name ?? "?"}"))}");
    }

    private static string Unquote(string s) =>
        s.Length > 2 && s[0] == '\'' && s[^1] == '\'' ? s[1..^1] : s;

    private static void RequireNoOperand(OpCode op, string operandText)
    {
        if (operandText.Length > 0)
        {
            throw new ReplException($"'{op.Name}' takes no operand");
        }
    }

    private static int IndexOfWhitespace(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static string? Suggest(string typo)
    {
        string? best = null;
        var bestDistance = 3;
        foreach (var name in OpcodeTable.Names)
        {
            if (OpcodeTable.IsReserved(name))
            {
                continue;
            }

            var d = Levenshtein(typo, name);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = name;
            }
        }

        return best;
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
