using System.Globalization;
using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// A disassembled body projected into the ordinary editable method grammar, without transcript columns.
/// </summary>
/// <param name="Method">The source method definition.</param>
/// <param name="Listing">The immutable original disassembly.</param>
/// <param name="Source">The complete editable method definition.</param>
/// <param name="State">The validated body in its source declaring context.</param>
internal sealed record MethodEditBody(MethodBase Method, DisassembledMethod Listing, string Source, CellState State)
{
    internal static MethodEditBody Read(MethodBase method, Session session, IReadOnlyList<MethodSignature> signatures,
        TypeTable? types = null, Type? contextType = null)
    {
        var listing = MethodDisassembler.Disassemble(method, session);
        if (listing.Problems.Count != 0 || listing.Entries.Any(e => e.EffectUnknown || e.Kind == DisassembledEntryKind.Raw))
        {
            throw new ReplException($"cannot edit {MemberResolver.Describe(method)}: " +
                string.Join("; ", listing.Problems.Concat(listing.Notes).Concat(listing.Entries
                    .Where(entry => entry.EffectUnknown || entry.Kind == DisassembledEntryKind.Raw).Select(entry => entry.DisplayText))));
        }

        var lines = new List<string>
        {
            listing.Header + " {",
            ".maxstack " + listing.MaxStack.ToString(CultureInfo.InvariantCulture),
            (listing.InitLocals ? ".locals init (" : ".locals (") +
                string.Join(", ", listing.Locals.Select((local, index) =>
                    IlSignatureRenderer.IlAsmNamed(local) + " V_" + index.ToString(CultureInfo.InvariantCulture))) + ")",
        };
        lines.AddRange(listing.Clauses.Select(clause => clause.Describe()));
        foreach (var entry in listing.Entries.Where(e => e.Instruction is not null))
        {
            lines.Add(IlReader.LabelFor(entry.Offset) + ": " + entry.Instruction!.Text);
        }

        if (listing.Clauses.Count != 0)
        {
            lines.Add(IlReader.LabelFor(listing.CodeSize) + ":");
        }

        lines.Add("}");
        return Parse(listing, string.Join('\n', lines), session, signatures, types, contextType);
    }

    internal static MethodEditBody Parse(DisassembledMethod listing, string source, Session session,
        IReadOnlyList<MethodSignature> signatures, TypeTable? types = null, Type? contextType = null)
    {
        var method = listing.Method;
        var owner = contextType ?? method.DeclaringType ?? throw new ReplException("the method has no declaring type");
        var ownerParameters = owner.IsGenericType ? owner.GetGenericArguments() : Type.EmptyTypes;
        var methodParameters = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;
        var kind = owner.IsEnum ? TypeKind.Enum : owner.IsValueType ? TypeKind.Struct : owner.IsInterface ? TypeKind.Interface
            : TypeKind.Class;
        var header = new TypeHeader(owner.Attributes, kind, true,
            owner.IsExplicitLayout ? TypeLayoutKind.Explicit : owner.IsLayoutSequential ? TypeLayoutKind.Sequential : TypeLayoutKind.Auto,
            owner.Namespace ?? "", owner.Name, [], null, [], true, false);
        var context = new ParseContext([], [], new GenericContext(ownerParameters, methodParameters), session.Resolver,
            signatures, types ?? session.TypeTable);
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var first = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        if (first < 0 || !lines[first].TrimStart().StartsWith(".method ", StringComparison.Ordinal))
        {
            throw new ReplException("an edit must contain one complete .method definition");
        }

        var signature = MethodHeaderParser.ParseMember(lines[first].Trim()[8..], context, header,
            out var opens, out var closes, out _, names => names.Length == methodParameters.Length
                ? methodParameters
                : throw new ReplException("an edit must preserve the method's generic parameter count"));
        if (signature.Name != method.Name || signature.IsStatic != method.IsStatic || closes)
        {
            throw new ReplException("an edit must preserve the method name and instance/static calling convention");
        }

        var member = new MemberContext(owner, header, method.IsStatic ? null : owner.IsValueType ? owner.MakeByRefType() : owner,
            false, owner.FullName ?? owner.Name, owner.IsValueType ? "struct" : "class");
        var generics = context.Generics with
        {
            MethodParameterNames = signature.TypeParameters.Select(parameter => parameter.Name).ToArray(),
        };
        var state = new CellState(session.Resolver, generics, signatures, signature, opens, types ?? session.TypeTable, member);
        var ended = false;
        foreach (var line in lines.Skip(first + 1))
        {
            if (ended)
            {
                if (!string.IsNullOrWhiteSpace(InstructionParser.StripComments(line)))
                {
                    throw new ReplException("an edit must contain exactly one .method definition");
                }

                continue;
            }

            var result = state.Apply(line);
            ended = result.Outcome == LineOutcome.MethodEnd;
        }

        if (!ended)
        {
            throw new ReplException("close the edited .method with }");
        }

        state.RequireCompleteFlow();
        return new MethodEditBody(method, listing, source, state);
    }
}
