namespace IlRepl.Engine;

/// <summary>
/// Retains zero, one, and original-receiver facts for one value on a filter path.
/// </summary>
internal readonly record struct FilterPathValue(bool? IsZero, bool? IsOne, bool IsThis);
