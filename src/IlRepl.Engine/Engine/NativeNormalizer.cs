using System.Globalization;
using System.Text.RegularExpressions;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Symbolizes proven pointer operands while preserving numeric constants and native instruction structure.
/// </summary>
public static partial class NativeNormalizer
{
    /// <summary>
    /// Extends observed handles with known probe returns and normalizes one target compilation.
    /// </summary>
    /// <param name="compilation">The selected original listing.</param>
    /// <param name="facts">Runtime handle, object, and published-code observations.</param>
    /// <param name="probes">The separately identified compilation-only probes.</param>
    /// <param name="listings">All complete listings from this worker.</param>
    /// <param name="architecture">The worker architecture.</param>
    /// <param name="constants">Original numeric literals that cannot be treated as addresses.</param>
    /// <param name="pointerReturn">Whether metadata proves a pointer-capable or scalar return, when available.</param>
    /// <returns>The normalized instructions and unresolved evidence.</returns>
    public static NativeNormalization Normalize(
        NativeCompilation compilation,
        IReadOnlyList<NativeAddressFact> facts,
        IReadOnlyList<NativeProbe> probes,
        IReadOnlyList<NativeCompilation> listings,
        string architecture,
        IReadOnlyList<ulong> constants,
        bool? pointerReturn = null)
    {
        var addresses = facts.ToList();
        var arm = architecture.Equals("Arm64", StringComparison.OrdinalIgnoreCase);
        foreach (var probe in probes)
        {
            var blocks = listings.Where(block => block.Method.StartsWith(probe.Method + "(", StringComparison.Ordinal)).ToArray();
            if (blocks.Length != 1)
            {
                continue;
            }

            var returned = ReturnValues(NativeDisassembly.Instructions(blocks[0]), arm);
            if (returned.Length == 1 && returned[0] != 0)
            {
                addresses.Add(new NativeAddressFact
                {
                    Address = returned[0], Kind = probe.Kind, Symbol = probe.Symbol, DisplaySymbol = probe.DisplaySymbol,
                    Evidence = "compiled, never invoked, " + probe.Method + " return value",
                });
            }

            if (probe.Kind != "static-field")
            {
                continue;
            }

            foreach (var line in NativeDisassembly.Instructions(blocks[0]))
            {
                if (!line.TrimStart().StartsWith("test", StringComparison.Ordinal))
                {
                    continue;
                }

                var memory = AbsoluteMemory().Match(line);
                if (!memory.Success || !TryNumber(memory.Groups[1].Value, out var guard))
                {
                    continue;
                }

                addresses.Add(new NativeAddressFact
                {
                    Address = guard, Kind = "initialization-guard", Symbol = probe.Symbol.Split("::", StringSplitOptions.None)[0],
                    DisplaySymbol = probe.DisplaySymbol.Split("::", StringSplitOptions.None)[0],
                    Evidence = "compiled ldsflda initialization guard in " + probe.Method,
                });
            }
        }

        var lines = NativeDisassembly.Instructions(compilation);
        var pools = LiteralPools(lines);
        var locations = CodeLocations(compilation);
        var branchTargets = Targets(lines);
        var edits = new Dictionary<(int Line, int Start, int Length), string>();
        var problems = new HashSet<string>(StringComparer.Ordinal);
        var registers = new Dictionary<string, NativeRegisterValue>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            foreach (Match relocation in Relocation().Matches(line))
            {
                if (!TryNumber(relocation.Groups[2].Value, out var value))
                {
                    continue;
                }

                var fact = Find(value, memory: true);
                if (fact is null)
                {
                    problems.Add("unproven " + relocation.Groups[1].Value + " relocation " + relocation.Groups[2].Value);
                }
                else
                {
                    edits[(index, relocation.Groups[2].Index, relocation.Groups[2].Length)] =
                    (relocation.Groups[1].Value == "HIGH" ? "page(" : "lo12(") + Symbol(fact, value) + ")";
                }
            }

            foreach (var operand in OtherRelocation().Matches(line).Select(relocation => relocation.Groups[1]))
            {
                if (!TryNumber(operand.Value, out var value))
                {
                    continue;
                }

                var fact = Find(value, memory: true);
                if (fact is null)
                {
                    problems.Add("unproven relocation " + operand.Value);
                }
                else
                {
                    edits[(index, operand.Index, operand.Length)] = Symbol(fact, value);
                }
            }

            foreach (Match relative in RipRelative().Matches(line))
            {
                if (!locations.TryGetValue(index, out var location) || !TryNumber(relative.Groups[2].Value, out var displacement))
                {
                    problems.Add("missing instruction location for " + relative.Value);
                    continue;
                }

                var value = relative.Groups[1].Value == "-" ? location - displacement : location + displacement;
                var fact = Find(value, memory: true);
                if (fact is null)
                {
                    problems.Add("unproven PC-relative operand " + relative.Value);
                }
                else
                {
                    edits[(index, relative.Groups[1].Index,
                    relative.Groups[2].Index + relative.Groups[2].Length - relative.Groups[1].Index)] =
                    "+rel32(" + Symbol(fact, value) + ")";
                }
            }

            var table = JumpTable().Match(line);
            if (table.Success)
            {
                edits[(index, table.Groups[1].Index, table.Groups[1].Length)] = "<code:" + table.Groups[2].Value + ">";
            }

            var parsed = Instruction().Match(line);
            if (!parsed.Success)
            {
                if (branchTargets.Contains(line.TrimStart().Split(':')[0]))
                {
                    registers.Clear();
                }

                continue;
            }

            var operation = parsed.Groups[1].Value;
            var operands = parsed.Groups[2].Value.Split(';')[0].Split("//", StringSplitOptions.None)[0].TrimEnd();
            var parts = operands.Split(',', StringSplitOptions.TrimEntries);
            foreach (Match memory in AbsoluteMemory().Matches(line))
            {
                if (!TryNumber(memory.Groups[1].Value, out var value))
                {
                    continue;
                }

                var fact = Find(value, memory: true);
                if (fact is not null)
                {
                    edits[(index, memory.Groups[1].Index, memory.Groups[1].Length)] = Symbol(fact, value);
                }
                else
                {
                    problems.Add("unproven absolute memory operand " + memory.Groups[1].Value);
                }
            }

            foreach (var (register, value) in registers)
            {
                var memoryUse = RegisterMemory().Matches(line).FirstOrDefault(match => Canonical(match.Groups[1].Value, arm) == register);
                var usedValue = value.Value;
                var components = value.Parts;
                if (memoryUse is not null && memoryUse.Groups[2].Success && TryNumber(memoryUse.Groups[2].Value, out var displacement))
                {
                    usedValue = unchecked(usedValue + displacement);
                    if (value.Page)
                    {
                        components = [.. components, new NativeAddressPart(index, memoryUse.Groups[2].Index,
                            memoryUse.Groups[2].Length, -1, "lo12")];
                    }
                    else
                    {
                        components = [.. components.Select(part => part with { Adjustment = part.Adjustment + displacement })];
                    }
                }

                var branchesThroughIt = operation is "call" or "jmp" or "tail.jmp" or "blr" or "br" && operands.Trim() == register;
                var returnsIt = operation is "ret" && pointerReturn != false && register == (arm ? "x0" : "rax");
                var passesIt = operation is "call" or "bl" or "blr" && IsArgument(register, arm);
                var use = memoryUse is not null || branchesThroughIt || returnsIt || passesIt;
                if (!use)
                {
                    continue;
                }

                var fact = Find(usedValue, memory: false);
                if (fact is null)
                {
                    var returnsPointer = operation == "ret" && pointerReturn == true && value.Value != 0;
                    if ((memoryUse is not null || branchesThroughIt || value.Page || returnsPointer) && !constants.Contains(value.Value))
                    {
                        problems.Add("unproven pointer use through " + register);
                    }

                    continue;
                }

                foreach (var component in components)
                {
                    var label = Symbol(fact, usedValue);
                    if (component.Adjustment != 0)
                    {
                        label = "(" + label + "-0x"
                        + component.Adjustment.ToString("X", CultureInfo.InvariantCulture) + ")";
                    }

                    if (component.Shift >= 0)
                    {
                        label = $"bits{component.Shift}:{component.Shift + 15}({label})";
                    }

                    if (component.Operation == "~")
                    {
                        label = "~" + label;
                    }
                    else if (component.Operation.Length != 0)
                    {
                        label = component.Operation + "(" + label + ")";
                    }

                    edits[(component.Line, component.Start, component.Length)] = label;
                }
            }

            if (operation is "call" or "bl" or "blr")
            {
                foreach (var register in registers.Keys.Where(register => IsVolatile(register, arm)).ToArray())
                {
                    registers.Remove(register);
                }

                continue;
            }

            if (operation.StartsWith('j') || operation == "tail.jmp" || operation is "b" or "br" or "ret" ||
                operation.StartsWith("b.", StringComparison.Ordinal))
            {
                registers.Clear();
                continue;
            }

            Update(registers, line, index, operation, parts, arm, pools);
        }

        for (var index = 0; index < lines.Length; index++)
        {
            foreach (var edit in edits.Where(edit => edit.Key.Line == index).OrderByDescending(edit => edit.Key.Start))
            {
                lines[index] = lines[index].Remove(edit.Key.Start, edit.Key.Length).Insert(edit.Key.Start, edit.Value);
            }

            // Relocations are explicit runtime evidence of a process-dependent value, never a guessed large integer.
            if (lines[index].Contains("reloc", StringComparison.OrdinalIgnoreCase)
                && Relocation().Matches(lines[index]).Any(match => TryNumber(match.Groups[2].Value, out _)))
            {
                problems.Add("unresolved relocation: " + lines[index].Trim());
            }
        }

        return new NativeNormalization { Lines = lines, Addresses = [.. addresses.Distinct()], Problems = [.. problems] };

        NativeAddressFact? Find(ulong value, bool memory)
        {
            if (!memory && constants.Contains(value))
            {
                return null;
            }

            var matching = addresses.Where(fact => value >= fact.Address && value - fact.Address < fact.Length
                && (memory || fact.Kind != "initialization-guard")).ToArray();
            if (matching.Select(fact => (fact.Kind, fact.Symbol)).Distinct().Count() != 1)
            {
                return null;
            }

            return matching.Length == 0 ? null : matching[0];
        }
    }

    private static ulong[] ReturnValues(string[] lines, bool arm)
    {
        var values = new List<ulong>();
        var pools = LiteralPools(lines);
        var branchTargets = Targets(lines);
        var registers = new Dictionary<string, NativeRegisterValue>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            var parsed = Instruction().Match(lines[index]);
            if (!parsed.Success)
            {
                if (branchTargets.Contains(lines[index].TrimStart().Split(':')[0]))
                {
                    registers.Clear();
                }

                continue;
            }

            var operation = parsed.Groups[1].Value;
            if (operation == "ret" && registers.TryGetValue(arm ? "x0" : "rax", out var returned))
            {
                values.Add(returned.Value);
            }

            if (operation is "call" or "bl" or "blr")
            {
                registers.Clear();
            }
            else
            {
                Update(registers, lines[index], index, operation,
                    parsed.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries), arm, pools);
            }
        }

        return [.. values.Distinct()];
    }

    private static void Update(
        Dictionary<string, NativeRegisterValue> registers,
        string line,
        int index,
        string operation,
        string[] operands,
        bool arm,
        Dictionary<string, NativeRegisterValue> pools)
    {
        if (operation == "ldp" && operands.Length > 1)
        {
            registers.Remove(Canonical(operands[0], arm));
            registers.Remove(Canonical(operands[1], arm));
            return;
        }

        if (!arm && operation is "mul" or "div" or "idiv" or "cpuid" or "cqo" or "cdq")
        {
            registers.Remove("rax");
            registers.Remove("rdx");
            if (operation == "cpuid")
            {
                registers.Remove("rbx");
                registers.Remove("rcx");
            }
        }

        if (operation == "xchg" && operands.Length > 1)
        {
            registers.Remove(Canonical(operands[1], arm));
        }

        if (operands.Length == 0 || !Register().IsMatch(operands[0]))
        {
            return;
        }

        var destination = Canonical(operands[0], arm);
        if (operation is "cmp" or "test" or "tst" or "str" or "stp" or "push" or "cbz" or "cbnz")
        {
            return;
        }

        if (operands.Length < 2)
        {
            registers.Remove(destination);
            return;
        }

        var literal = PoolReference().Match(operands[1]);
        if (operation is "ldr" or "mov" && literal.Success && pools.TryGetValue(literal.Groups[1].Value, out var pooled))
        {
            registers[destination] = pooled;
            return;
        }

        var relocation = Relocation().Match(operands[1]);
        // CoreCLR prints shifted Arm64 immediates as "#0x1234 LSL #16", without a separating comma.
        var shifted = Shift().Match(operands.Length > 2 ? operands[2] : operands[1]);
        var immediateText = operands.Length == 2 && shifted.Success ? operands[1][..shifted.Index].TrimEnd() : operands[1];
        var number = relocation.Success ? relocation.Groups[2] : Number().Match(immediateText);
        var sourceIndex = line.IndexOf(operands[1], line.IndexOf(',') + 1, StringComparison.Ordinal);
        var narrow = operands[0].StartsWith('e') || operands[0].StartsWith('r') && operands[0].EndsWith('d');
        var width32 = arm ? operands[0].StartsWith('w') : narrow;
        var mask = width32 ? uint.MaxValue : ulong.MaxValue;
        if (operation is "mov" or "movabs" or "movz" or "movn" or "movk" && number.Success
            && TryNumber(number.Value, out var immediate))
        {
            var shift = shifted.Success ? int.Parse(shifted.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            if (shift is < 0 or > 48 || (shift % 16) != 0 || width32 && shift > 16)
            {
                registers.Remove(destination);
                return;
            }

            var value = (immediate << shift) & mask;
            var components = new List<NativeAddressPart>();
            if (operation == "movn")
            {
                value = ~value & mask;
            }

            if (operation == "movk")
            {
                if (!registers.TryGetValue(destination, out var prior))
                {
                    registers.Remove(destination);
                    return;
                }

                value = (prior.Value & ~(0xffffUL << shift) | value) & mask;
                components.AddRange(prior.Parts.Where(part => part.Shift != shift));
            }

            components.Add(new NativeAddressPart(index, sourceIndex + number.Index, number.Length,
                arm && operation != "mov" ? shift : -1, operation == "movn" ? "~" : ""));
            registers[destination] = new NativeRegisterValue(value, components);
            return;
        }

        if (operation == "mov" && registers.TryGetValue(Canonical(operands[1], arm), out var copied))
        {
            registers[destination] = copied with { Value = copied.Value & mask };
            return;
        }

        if (arm && operation is "adr" or "adrp" && number.Success && TryNumber(number.Value, out var page))
        {
            registers[destination] = new NativeRegisterValue(operation == "adrp" ? page & ~0xfffUL : page,
                [new NativeAddressPart(index, sourceIndex + number.Index, number.Length, -1, operation == "adrp" ? "page" : "")],
                Page: operation == "adrp");
            return;
        }

        var low = operands.Length > 2 ? Relocation().Match(operands[2]) : Match.Empty;
        var offsetText = low.Success ? low.Groups[2].Value : operands.ElementAtOrDefault(2) ?? "";
        if (arm && operation == "add" && operands.Length > 2
            && registers.TryGetValue(Canonical(operands[1], true), out var basis) && TryNumber(offsetText, out var offset))
        {
            if (low.Success)
            {
                offset &= 0xfff;
            }

            var components = basis.Page ? basis.Parts.ToList()
                : basis.Parts.Select(part => part with { Adjustment = part.Adjustment + offset }).ToList();
            if (basis.Page && !low.Success)
            {
                var offsetIndex = line.IndexOf(operands[2], sourceIndex + operands[1].Length, StringComparison.Ordinal);
                components.Add(new NativeAddressPart(index, offsetIndex, operands[2].Length, -1, "lo12"));
            }

            registers[destination] = new NativeRegisterValue((basis.Value + offset) & mask, components);
            return;
        }

        registers.Remove(destination);
    }

    private static Dictionary<string, NativeRegisterValue> LiteralPools(string[] lines)
    {
        var result = new Dictionary<string, NativeRegisterValue>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            var entry = PoolEntry().Match(lines[index]);
            if (!entry.Success || !ulong.TryParse(entry.Groups[2].Value, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            result[entry.Groups[1].Value] = new NativeRegisterValue(value,
                [new NativeAddressPart(index, entry.Groups[2].Index, entry.Groups[2].Length + 1, -1)]);
        }

        return result;
    }

    private static Dictionary<int, ulong> CodeLocations(NativeCompilation compilation)
    {
        var result = new Dictionary<int, ulong>();
        if (compilation.Address == 0)
        {
            return result;
        }

        var raw = NativeDisassembly.Instructions(compilation, raw: true);
        var offset = 0UL;
        for (var index = 0; index < raw.Length; index++)
        {
            var bytes = EncodingBytes().Match(raw[index]);
            if (!bytes.Success)
            {
                continue;
            }

            offset += (ulong)bytes.Groups[1].Length / 2;
            result[index] = compilation.Address + offset;
        }

        // A cold region or omitted bytes cannot establish a contiguous instruction location.
        return offset == (ulong)compilation.CodeSize ? result : [];
    }

    private static HashSet<string> Targets(string[] lines) => lines.Where(line => Instruction().IsMatch(line))
        .SelectMany(line => BranchLabel().Matches(line).Select(match => match.Value))
        .ToHashSet(StringComparer.Ordinal);

    private static bool TryNumber(string text, out ulong value)
    {
        text = text.Trim().TrimStart('#');
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string Symbol(NativeAddressFact fact, ulong value) => "<" + fact.Kind + ":" + fact.Symbol
        + (value == fact.Address ? "" : "+0x" + (value - fact.Address).ToString("X", CultureInfo.InvariantCulture)) + ">";

    private static string Canonical(string register, bool arm)
    {
        if (arm)
        {
            return register.StartsWith('w') ? "x" + register[1..] : register;
        }

        if (register.StartsWith('e'))
        {
            return "r" + register[1..];
        }

        if (register.Length == 2 && "abcd".Contains(register[0]) && "lh".Contains(register[1]))
        {
            return "r" + register[0] + "x";
        }

        if (register is "ax" or "bx" or "cx" or "dx" or "si" or "di" or "bp" or "sp")
        {
            return "r" + register;
        }

        var numbered = register.Length > 2 && register[0] == 'r' && char.IsDigit(register[1]) && register[^1] is 'd' or 'w' or 'b';
        return numbered ? register[..^1] : register;
    }

    private static bool IsArgument(string register, bool arm) =>
        arm ? register is "x0" or "x1" or "x2" or "x3" or "x4" or "x5" or "x6" or "x7"
        : register is "rcx" or "rdx" or "r8" or "r9" or "rdi" or "rsi";

    private static bool IsVolatile(string register, bool arm) => IsArgument(register, arm)
        || (arm ? register is "x8" or "x9" or "x10" or "x11" or "x12" or "x13" or "x14" or "x15" or "x16" or "x17"
            : register is "rax" or "r10" or "r11");

    [GeneratedRegex(@"^\s+([a-z][a-z0-9.]*)\s*(.*?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Instruction();

    [GeneratedRegex(@"(?<!FS:)(?<!GS:)\[\s*(0x[0-9A-Fa-f]+)\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteMemory();

    [GeneratedRegex(@"(?<!HIGH )(?<!LOW )\breloc\s+(0x[0-9A-Fa-f]+)", RegexOptions.CultureInvariant)]
    private static partial Regex OtherRelocation();

    [GeneratedRegex(@"\[rip\s*([+-])\s*(0x[0-9A-Fa-f]+|\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex RipRelative();

    [GeneratedRegex(@"^\s+([0-9A-Fa-f]{2,64})\s+(?=[a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex EncodingBytes();

    [GeneratedRegex(@"\b(HIGH|LOW) RELOC\s+(#?(?:0x[0-9A-Fa-f]+|\d+))", RegexOptions.CultureInvariant)]
    private static partial Regex Relocation();

    [GeneratedRegex(@"\bdq\s+([0-9A-Fa-f]+h)\s*;\s*case\s+(L\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex JumpTable();

    [GeneratedRegex(@"^\s*((?:RWD|CNS)\d+)\s+dq\s+([0-9A-Fa-f]{1,16})h\b", RegexOptions.CultureInvariant)]
    private static partial Regex PoolEntry();

    [GeneratedRegex(@"\[@((?:RWD|CNS)\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex PoolReference();

    [GeneratedRegex(@"\[\s*((?:[xw]\d+|r(?:ax|bx|cx|dx|si|di|bp|sp|\d+)))(?:\s*(?:\+|,)\s*(#?(?:0x[0-9A-Fa-f]+|\d+)))?\s*\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex RegisterMemory();

    [GeneratedRegex(@"\b0x[0-9A-Fa-f]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex Hex();

    [GeneratedRegex(@"^#?(?:0x[0-9A-Fa-f]+|\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"^(?:[xw]\d+|[re](?:ax|bx|cx|dx|si|di|bp|sp)|r\d+(?:[dwb])?|[abcd][lh]|(?:ax|bx|cx|dx|si|di|bp|sp))$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Register();

    [GeneratedRegex(@"\bL\d+\b", RegexOptions.CultureInvariant)]
    private static partial Regex BranchLabel();

    [GeneratedRegex(@"\blsl\s+#?(\d+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Shift();
}
