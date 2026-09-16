using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// A loaded method body whose execution delegate is created only when the session explicitly activates it.
/// </summary>
public sealed class CompiledMethodVersion
{
    private readonly Lazy<Delegate> _implementation;

    /// <summary>
    /// Retains the loaded body without creating a delegate or activating its assembly.
    /// </summary>
    /// <param name="definition">The version's assembly.</param>
    /// <param name="body">The compiled method.</param>
    /// <param name="delegateType">The trampoline's delegate type.</param>
    public CompiledMethodVersion(DefinitionAssembly definition, MethodInfo body, Type delegateType)
    {
        Definition = definition;
        Body = body;
        _implementation = new Lazy<Delegate>(() => Delegate.CreateDelegate(delegateType, body));
    }

    /// <summary>
    /// The loaded definition retained for inspection and export.
    /// </summary>
    public DefinitionAssembly Definition { get; }

    /// <summary>
    /// The method metadata available before execution.
    /// </summary>
    public MethodInfo Body { get; }

    /// <summary>
    /// Creates or retrieves the execution delegate, which may activate the target assembly.
    /// </summary>
    public Delegate Implementation => _implementation.Value;
}
