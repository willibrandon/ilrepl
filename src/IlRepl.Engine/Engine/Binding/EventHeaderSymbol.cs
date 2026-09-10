using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// The header of an <c>.event</c> block before its accessors are known.
/// </summary>
/// <param name="Name">The event name.</param>
/// <param name="HandlerType">The delegate type.</param>
/// <param name="Attributes">The event attributes.</param>
/// <param name="OpensBlock">True when the header ended with <c>{</c>.</param>
public sealed record EventHeaderSymbol(
    string Name,
    TypeSymbol HandlerType,
    EventAttributes Attributes,
    bool OpensBlock);
