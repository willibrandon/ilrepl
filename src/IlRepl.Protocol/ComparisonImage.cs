namespace IlRepl.Protocol;

/// <summary>
/// A frozen comparison assembly and the fully specified invocation to perform in its fresh runtime.
/// </summary>
/// <param name="Image">The PE image containing the scenario and selected method.</param>
/// <param name="EntryType">The entry method's declaring type path.</param>
/// <param name="EntryMethod">The entry method name.</param>
/// <param name="EntryToken">The method definition token, or zero for a named scenario.</param>
/// <param name="TypeArguments">The closed declaring-type generic arguments.</param>
/// <param name="MethodArguments">The closed method generic arguments.</param>
/// <param name="Arguments">The typed literal source of each fixed argument.</param>
/// <param name="TypeNames">Generated type names mapped to the source identities used for structural observations.</param>
public sealed record ComparisonImage(byte[] Image, string EntryType, string EntryMethod, int EntryToken,
    IReadOnlyList<string> TypeArguments, IReadOnlyList<string> MethodArguments, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> TypeNames)
{
    /// <summary>
    /// The original external assembly when its declaring context cannot be copied.
    /// </summary>
    public string? OriginalAssembly { get; init; }

    /// <summary>
    /// The required module identity for an original invocation.
    /// </summary>
    public Guid? OriginalModule { get; init; }
}
