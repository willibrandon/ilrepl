namespace IlRepl.Engine;

/// <summary>
/// Retains zero, one, and original-receiver facts for one value on a filter path.
/// </summary>
/// <param name="IsZero">Whether the value is known to be zero.</param>
/// <param name="IsOne">Whether the value is known to be one.</param>
/// <param name="IsThis">Whether the value is the original receiver.</param>
/// <param name="ReceiverSource">The single receiver source, when known.</param>
/// <param name="ReceiverSources">The possible receiver sources, when more than one is known.</param>
/// <param name="HasNonSourceAlternative">Whether the value can also come from an unknown source.</param>
/// <param name="IntegerValue">The exact integer value, when known.</param>
/// <param name="ExcludedIntegers">Integer values excluded by the path.</param>
/// <param name="ComparedSource">The source tested by an equality result.</param>
/// <param name="ComparedInteger">The constant tested by an equality result.</param>
/// <param name="IsNull">Whether the value is known to be a null reference.</param>
/// <param name="ComparedOtherSource">The other source tested by an equality result.</param>
/// <param name="ComparedWithNull">Whether the source was compared with a null reference.</param>
/// <param name="EqualSources">Sources known to equal this source.</param>
/// <param name="ExcludedSources">Sources known not to equal this source.</param>
/// <param name="MayBeNaN">Whether a floating-point source can be unordered.</param>
/// <param name="IsNaN">Whether a floating-point source is known to be NaN.</param>
internal readonly record struct FilterPathValue(bool? IsZero, bool? IsOne, bool IsThis,
    int? ReceiverSource = null, IReadOnlyList<int>? ReceiverSources = null,
    bool HasNonSourceAlternative = false, long? IntegerValue = null,
    IReadOnlyList<long>? ExcludedIntegers = null, int? ComparedSource = null,
    long? ComparedInteger = null, bool? IsNull = null, int? ComparedOtherSource = null,
    bool ComparedWithNull = false, IReadOnlyList<int>? EqualSources = null,
    IReadOnlyList<int>? ExcludedSources = null, bool MayBeNaN = false, bool? IsNaN = null);
