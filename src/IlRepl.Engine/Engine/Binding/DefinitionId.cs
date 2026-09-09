namespace IlRepl.Engine.Binding;

/// <summary>
/// Identifies a definition by its assembly instance, module, and token or declaration number.
/// </summary>
/// <remarks>
/// The identity of a type, method, or field definition, independent of any runtime object. A
/// loaded definition is its assembly instance, its module, and its metadata token: the same bytes
/// loaded twice are two identities, and a token alone is not one. A definition the session is
/// still writing, or a placeholder for one, is its assembly instance and a declaration number.
/// </remarks>
/// <param name="Assembly">The loaded instance identified by <see cref="RuntimeDefinitions.AssemblyInstance"/>.</param>
/// <param name="Module">The module version id of a loaded definition; empty for a declaration.</param>
/// <param name="Token">The metadata token of a loaded definition; 0 for a declaration.</param>
/// <param name="Declaration">The declaration number of a definition being written; 0 for a loaded definition.</param>
public readonly record struct DefinitionId(long Assembly, Guid Module, int Token, long Declaration)
{
    /// <summary>
    /// No definition: the identity a primitive or a constructed type carries in the slot.
    /// </summary>
    public static DefinitionId None => default;

    /// <summary>
    /// A loaded definition.
    /// </summary>
    /// <param name="assembly">The assembly instance.</param>
    /// <param name="module">The module version id.</param>
    /// <param name="token">The metadata token.</param>
    /// <returns>The identity.</returns>
    public static DefinitionId Loaded(long assembly, Guid module, int token) => new(assembly, module, token, 0);

    /// <summary>
    /// A definition being written, or a placeholder for one.
    /// </summary>
    /// <param name="assembly">The assembly instance the builder belongs to.</param>
    /// <param name="declaration">The declaration number.</param>
    /// <returns>The identity.</returns>
    public static DefinitionId ForDeclaration(long assembly, long declaration) => new(assembly, Guid.Empty, 0, declaration);

    /// <summary>
    /// True for a definition being written.
    /// </summary>
    public bool IsDeclaration => Declaration != 0;

    /// <summary>
    /// True for the empty identity.
    /// </summary>
    public bool IsNone => Assembly == 0 && Token == 0 && Declaration == 0;

    /// <inheritdoc/>
    public override string ToString() => IsDeclaration
        ? $"decl {Assembly}:{Declaration}"
        : $"{Assembly}:{Module:N}:{Token:x8}";
}
