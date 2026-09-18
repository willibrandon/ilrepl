namespace IlRepl.Engine;

/// <summary>
/// Separates reconstructing inspectable definitions from activating user code.
/// </summary>
public sealed partial class Session
{
    private readonly HashSet<string> _activatedReferences = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Dependency identities that explicit execution has already bound in this runtime.
    /// </summary>
    internal IReadOnlySet<string> ActivatedReferences => _activatedReferences;

    private void RecordActivation(CompiledCell cell)
    {
        var owned = Resolver.LoadedAssemblies.Select(assembly => assembly.GetName().Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var references = cell.Definition.Assembly.GetReferencedAssemblies()
            .Concat(cell.Helpers.SelectMany(helper => helper.Assembly.GetReferencedAssemblies()));
        foreach (var reference in references.Where(reference => owned.Contains(reference.Name!)))
        {
            _activatedReferences.Add(reference.Name!);
        }
    }

    /// <summary>
    /// Restores a completed cell boundary without compiling or invoking the cell body.
    /// </summary>
    internal void RestoreRunBoundary()
    {
        ClearCell();
        CellsRun++;
        Submissions++;
    }
    /// <summary>
    /// Defers JIT preparation, delegates, and argument values while reconstructing source.
    /// </summary>
    public bool DeferActivation
    {
        get => Resolver.DeferActivation;
        set => Resolver.DeferActivation = value;
    }

    /// <summary>
    /// Binds reconstructed methods before explicit execution without replaying previous cells.
    /// </summary>
    public void Activate()
    {
        if (!DeferActivation)
        {
            return;
        }

        // A failed activation leaves the boundary pending so execution can retry it.
        foreach (var method in Methods)
        {
            method.Trampoline.BindInitial(method.Version.Implementation);
        }

        DeferActivation = false;
    }
}
