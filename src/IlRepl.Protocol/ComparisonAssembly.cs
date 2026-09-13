namespace IlRepl.Protocol;

/// <summary>
/// A captured dependency image loaded by identity in an isolated comparison runtime.
/// </summary>
/// <param name="Name">The full assembly identity.</param>
/// <param name="Image">The retained PE image.</param>
public sealed record ComparisonAssembly(string Name, byte[] Image);
