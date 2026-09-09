using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// The header of a <c>.property</c> block before its accessors are known.
/// </summary>
/// <param name="Name">The property name.</param>
/// <param name="Type">The property type.</param>
/// <param name="ParameterTypes">The index parameter types.</param>
/// <param name="IsStatic">True when the accessors are static.</param>
/// <param name="Attributes">The property attributes.</param>
/// <param name="OpensBlock">True when the header ended with <c>{</c>.</param>
public sealed record PropertyHeader(
    string Name,
    Type Type,
    IReadOnlyList<Type> ParameterTypes,
    bool IsStatic,
    PropertyAttributes Attributes,
    bool OpensBlock);
