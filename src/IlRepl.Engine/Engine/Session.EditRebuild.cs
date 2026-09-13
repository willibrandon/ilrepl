using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Rebuilds declarations affected by a committed method edit before publishing its bindings.
/// </summary>
public sealed partial class Session
{
    private void PublishEditBindings(MethodEdit edit, ImportedMethodFamily family, TypeTable table)
    {
        if (edit.Current is not { } previous)
        {
            _cell = BuildCell(Signatures(), table);
            _typeTable = table;
            return;
        }

        var closure = DefinitionReplacementPlanner.PlanFromTypes<SessionType, SessionMethod, Type>(
            previous.DependencyTypes,
            _types,
            _methods,
            type => type.Types.Values.Concat(type.Prototypes.Values.Select(entry => (Type)entry.Prototype)),
            (type, types, methods) => FamilyMentions(type.Declaration, types, methods),
            (method, types, methods) => BodyMentions(method.State, types, methods)
                || SignatureTypes(method.Signature).Any(type => Mentions(type, types)),
            method => method.Signature.Name,
            ReferenceEqualityComparer.Instance);
        if (closure.Families.Count == 0 && closure.Methods.Count == 0)
        {
            _cell = BuildCell(Signatures(), table);
            _typeTable = table;
            return;
        }

        var saved = _typeTable;
        _typeTable = table;
        try
        {
            RebuildDefinitions(null, null, null, ([.. closure.Families], [.. closure.Methods]),
                map => family.MapPrevious(previous, map), "edit " + edit.Name);
        }
        catch
        {
            _typeTable = saved;
            throw;
        }
    }
}
