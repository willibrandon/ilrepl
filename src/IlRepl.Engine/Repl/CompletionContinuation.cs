using IlRepl.Engine.Binding;

namespace IlRepl.Repl;

/// <summary>
/// Holds a selected generic definition and the exact starter span whose edits can invalidate that selection.
/// </summary>
/// <param name="Target">The selected generic definition.</param>
/// <param name="Text">The inserted owner span through its opening angle bracket.</param>
/// <param name="Revision">The captured live-session revision.</param>
/// <param name="BindingEpoch">The captured assembly-binding epoch.</param>
/// <param name="DeclarationContext">The complete declaration environment of the selected definition.</param>
/// <param name="TypePaths">The selected environment's symbolic identities and full names.</param>
internal sealed record CompletionContinuation(
    GenericCompletionTarget Target, string Text, long Revision, long BindingEpoch, string DeclarationContext,
    IReadOnlyDictionary<DefinitionId, string> TypePaths)
{
    /// <summary>
    /// Rebinds a selection only when replay preserved the complete declaration environment.
    /// </summary>
    /// <param name="view">The freshly replayed context.</param>
    /// <returns>The selected definition with verified identity translations, or null after a declaration change.</returns>
    public GenericCompletionTarget? Rebind(EditingView view)
    {
        if (DeclarationContext != view.DeclarationContext)
        {
            return null;
        }

        var current = view.Snapshot.Types.Entries.ToDictionary(entry => entry.FullName, entry => entry.Type);
        var mapping = new Dictionary<DefinitionId, TypeSymbol>();
        foreach (var (identity, path) in TypePaths)
        {
            if (current.TryGetValue(path, out var type))
            {
                mapping[identity] = type;
            }
        }

        return Target with { Rebindings = mapping };
    }
}
