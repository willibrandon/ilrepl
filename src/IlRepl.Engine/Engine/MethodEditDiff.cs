using System.Globalization;
using System.Reflection;
using System.Text.Json;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Computes deterministic instruction differences with independent control-flow stack analysis.
/// </summary>
public static class MethodEditDiff
{
    /// <summary>
    /// Compares the immutable baseline with the latest successfully committed revision.
    /// </summary>
    /// <param name="edit">The edit to compare.</param>
    /// <param name="session">The session that resolves metadata references.</param>
    /// <param name="raw">Whether to retain offsets, encoding forms, and copied identities.</param>
    /// <returns>The ordered instruction, stack, and metadata differences.</returns>
    public static EditDiff Create(MethodEdit edit, Session session, bool raw = false)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(session);
        var current = edit.Current ?? throw new ReplException($"edit '{edit.Name}' has no committed version");
        var original = edit.Original;
        var edited = MethodDisassembler.Disassemble(edit.Method!, session);
        var left = Instructions(original, current, raw, originalSide: true);
        var right = Instructions(edited, current, raw, originalSide: false);
        (string[] Original, string[] Edited)? anchors = raw ? null : AlignTargets(left, right);
        var rows = new List<EditDiffRow>();
        var leftMetadata = Metadata(original, current, raw, true, anchors?.Original);
        var rightMetadata = Metadata(edited, current, raw, false, anchors?.Edited);
        for (var index = 0; index < Math.Max(leftMetadata.Count, rightMetadata.Count); index++)
        {
            var before = index < leftMetadata.Count ? leftMetadata[index] : null;
            var after = index < rightMetadata.Count ? rightMetadata[index] : null;
            if (before?.Key != after?.Key)
            {
                rows.Add(new EditDiffRow("metadata", before?.Text, after?.Text, null, null));
            }
        }

        foreach (var (before, after) in SequenceDiff.Match(left.Select(row => row.Key).ToArray(), right.Select(row => row.Key).ToArray()))
        {
            var l = before < 0 ? null : left[before];
            var r = after < 0 ? null : right[after];
            rows.Add(new EditDiffRow(l is null ? "added" : r is null ? "removed" : l.Stack == r.Stack ? "equal" : "stack",
                l?.Text, r?.Text, l?.Stack, r?.Stack));
        }

        return new EditDiff(edit.Name, edit.Fingerprint, edit.Revision, raw, rows);
    }

    private static List<DiffInstruction> Instructions(DisassembledMethod listing, ImportedMethodFamily family, bool raw, bool originalSide)
    {
        var stacks = StackAnalysis.Run(listing);
        var entries = listing.Entries.Where(entry => entry.Instruction is not null || entry.Raw is not null).ToArray();
        var labels = entries.Select((entry, index) => (Label: IlReader.LabelFor(entry.Offset), index))
            .ToDictionary(pair => pair.Label, pair => pair.index, StringComparer.Ordinal);
        labels[IlReader.LabelFor(listing.CodeSize)] = entries.Length;
        var rows = new List<DiffInstruction>();
        for (var index = 0; index < listing.Entries.Count; index++)
        {
            var entry = listing.Entries[index];
            if (entry.Instruction is not { } instruction)
            {
                if (entry.Raw is not null)
                {
                    rows.Add(new DiffInstruction(entry.DisplayText, entry.DisplayText, stacks[index]));
                }

                continue;
            }

            var key = raw ? IlReader.LabelFor(entry.Offset) + ": " + instruction.Text : Normalize(instruction, family,
                originalSide);
            var stack = stacks[index];
            if (!raw && stack is not null)
            {
                stack = family.NormalizeNames(stack);
            }

            rows.Add(new DiffInstruction(key, raw ? key : instruction.Text, stack)
            {
                Targets = instruction.Kind switch
                {
                    OperandKind.Label => [labels[(string)instruction.Operand!]],
                    OperandKind.Labels => ((string[])instruction.Operand!).Select(label => labels[label]).ToArray(),
                    _ => [],
                },
            });
        }

        return rows;
    }

    private static (string[] Original, string[] Edited) AlignTargets(List<DiffInstruction> original, List<DiffInstruction> edited)
    {
        var left = Enumerable.Range(0, original.Count).Select(index => "original " + index).Append("end").ToArray();
        var right = Enumerable.Range(0, edited.Count).Select(index => "edited " + index).Append("end").ToArray();
        foreach (var (before, after) in SequenceDiff.Match(original.Select(row => row.Key).ToArray(),
            edited.Select(row => row.Key).ToArray()))
        {
            if (before >= 0 && after >= 0)
            {
                left[before] = right[after] = "matched " + before.ToString(CultureInfo.InvariantCulture);
            }
        }

        void Apply(List<DiffInstruction> instructions, string[] anchors)
        {
            for (var index = 0; index < instructions.Count; index++)
            {
                var instruction = instructions[index];
                if (instruction.Targets.Count != 0)
                {
                    instructions[index] = instruction with
                    {
                        Key = instruction.Key + " " + string.Join(",", instruction.Targets.Select(target => anchors[target])),
                    };
                }
            }
        }

        Apply(original, left);
        Apply(edited, right);
        return (left, right);
    }

    private static string Normalize(Instruction instruction, ImportedMethodFamily family, bool originalSide)
    {
        var op = instruction.DecodedPrefixName ?? instruction.Op.Name!;
        if (instruction.ArgumentIndex is { } argument)
        {
            return op.Split('.')[0] + " " + argument.ToString(CultureInfo.InvariantCulture);
        }

        if (instruction.LocalIndex is { } local)
        {
            return op.Split('.')[0] + " " + local.ToString(CultureInfo.InvariantCulture);
        }

        if (op.StartsWith("ldc.i4.", StringComparison.Ordinal))
        {
            if (op == "ldc.i4.m1")
            {
                return "ldc.i4 -1";
            }

            if (char.IsDigit(op[^1]))
            {
                return "ldc.i4 " + op[^1];
            }
        }

        if (op.EndsWith(".s", StringComparison.Ordinal))
        {
            op = op[..^2];
        }

        string Member(MemberInfo member)
        {
            var source = originalSide ? member : family.OriginalMember(member);
            return source.Module.ModuleVersionId + ":" + source.MetadataToken.ToString("x8", CultureInfo.InvariantCulture)
                + (member.DeclaringType is { IsConstructedGenericType: true } owner
                    ? " on " + family.NormalizeNames(TypeNameFormatter.IlAsm(owner)) : "")
                + (member is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } generic
                    ? "<" + string.Join(",", generic.GetGenericArguments().Select(type =>
                        family.NormalizeNames(TypeNameFormatter.IlAsm(type)))) + ">" : "");
        }

        var operand = instruction.Operand switch
        {
            null => "",
            string when instruction.Kind == OperandKind.Label => "target",
            string[] targets when instruction.Kind == OperandKind.Labels => "targets " + targets.Length,
            string text => JsonSerializer.Serialize(text, ProtocolJsonContext.Default.String),
            float value => "0x" + BitConverter.SingleToInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture),
            double value => "0x" + BitConverter.DoubleToInt64Bits(value).ToString("x16", CultureInfo.InvariantCulture),
            ResolvedMethod { Definition: { } definition } => family.PinnedIdentity(definition.Name),
            ResolvedMethod { Method: { } method } => Member(method),
            Type type => family.NormalizeNames(TypeNameFormatter.IlAsm(type)),
            MemberInfo member => Member(member),
            IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
            _ => family.NormalizeNames(instruction.Text[(instruction.Op.Name!.Length)..].Trim()),
        };
        return op + " " + operand;
    }

    private static List<DiffInstruction> Metadata(
        DisassembledMethod listing,
        ImportedMethodFamily family,
        bool raw,
        bool originalSide,
        string[]? anchors)
    {
        string Text(string text) => raw || originalSide ? text : family.NormalizeNames(text);
        var headers = new[]
        {
            Text(listing.Header),
            ".maxstack " + listing.MaxStack.ToString(CultureInfo.InvariantCulture),
            ".initlocals " + listing.InitLocals,
            ".locals (" + string.Join(", ", listing.Locals.Select(local => Text(IlSignatureRenderer.IlAsm(local)))) + ")",
        };
        var rows = headers.Select(text => new DiffInstruction(text, text, null)).ToList();
        var offsets = listing.Entries.Where(entry => entry.Instruction is not null).Select((entry, index) => (entry.Offset, index))
            .ToDictionary(pair => pair.Offset, pair => pair.index);
        offsets[listing.CodeSize] = offsets.Count;
        string Boundary(int offset) => anchors![offsets[offset]];
        foreach (var clause in listing.Clauses)
        {
            var key = raw ? clause.Describe() : $".try {Boundary(clause.TryStart)} to {Boundary(clause.TryEnd)} {clause.Kind} "
                + $"{(clause.FilterStart is { } filter ? Boundary(filter) : "")} "
                + $"handler {Boundary(clause.HandlerStart)} to {Boundary(clause.HandlerEnd)} "
                + (clause.CatchSignature is { } caught ? Text(IlSignatureRenderer.IlAsm(caught)) : "");
            rows.Add(new DiffInstruction(key, Text(clause.Describe()), null));
        }

        return rows;
    }
}
