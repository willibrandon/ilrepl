using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

public sealed partial class Session
{
    /// <summary>
    /// The current binding context, including a class header when no member body is open.
    /// </summary>
    internal ParseContext CompletionContext => _openMember is null && _openType is { } type ? TypeContext(type) : State.Context;

    /// <summary>
    /// Copies the live session's source and binding identities without creating any preview runtime definitions.
    /// </summary>
    /// <returns>An independently owned metadata lease and immutable source records.</returns>
    internal EditingSeed CaptureEditingSeed()
    {
        var assemblies = _types.Where(type => type.Definition is not null).Select(type => type.Definition!.Assembly)
            .Concat(_methods.SelectMany(method => new[] { method.Trampoline.Definition.Assembly, method.Version.Definition.Assembly }));
        var snapshot = BindingSnapshot.Capture(InspectionContext, assemblies);
        try
        {
            var definitions = new List<EditingDefinition>();
            foreach (var family in _types)
            {
                definitions.Add(new EditingDefinition(
                    family.FullName,
                    true,
                    family.Declaration.HeaderLine,
                    [.. family.Declaration.Lines],
                    [.. family.Types.Values.Concat(family.Prototypes.Values.Select(value => (Type)value.Prototype))
                        .Select(RuntimeSymbolImporter.Import)],
                    [.. FamilyReferencedTypes(family.Declaration).Select(RuntimeSymbolImporter.Import)],
                    family.Declaration.Family.SelectMany(type => type.Methods)
                        .Where(method => method.Body is not null)
                        .SelectMany(method => ReferencedSessionMethods(method.Body!))
                        .ToHashSet(StringComparer.Ordinal)));
            }

            foreach (var method in _methods)
            {
                var referenced = SessionMentions.Types(method.State)
                    .Concat(SignatureTypes(method.Signature))
                    .Concat(method.Signature.ReturnRequiredModifiers)
                    .Concat(method.Signature.ReturnOptionalModifiers)
                    .Concat(method.Signature.Parameters.SelectMany(parameter =>
                        parameter.RequiredModifiers.Concat(parameter.OptionalModifiers)));
                definitions.Add(new EditingDefinition(
                    method.Signature.Name,
                    false,
                    method.HeaderLine,
                    [.. method.BodyLines],
                    [],
                    [.. referenced.Select(RuntimeSymbolImporter.Import)],
                    ReferencedSessionMethods(method.State).ToHashSet(StringComparer.Ordinal)));
            }

            IReadOnlyList<string> openLines = _openType is { } block
                ? [block.Outermost.HeaderLine, .. block.Outermost.Lines]
                : _open is { } openMethod ? [openMethod.HeaderLine, .. openMethod.BodyLines] : [];
            return new EditingSeed(
                snapshot, definitions, [.. _declarationLines], [.. _bodyLines],
                openLines, TypeArguments?.Select(RuntimeSymbolImporter.Import).ToArray(), InBlockComment, CompletionRevision);
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private static IEnumerable<string> ReferencedSessionMethods(CellState body) =>
        body.Entries.Select(entry => entry.Instruction?.Operand)
            .OfType<ResolvedMethod>()
            .Where(method => method.Definition is not null)
            .Select(method => method.Definition!.Name);
}
