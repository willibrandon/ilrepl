using System.Reflection;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// The header of an <c>.event</c> block before its accessors are known.
/// </summary>
/// <param name="Name">The event name.</param>
/// <param name="HandlerType">The delegate type.</param>
/// <param name="Attributes">The event attributes.</param>
/// <param name="OpensBlock">True when the header ended with <c>{</c>.</param>
public sealed record EventHeader(string Name, Type HandlerType, EventAttributes Attributes, bool OpensBlock)
{
    /// <summary>
    /// The complete handler type when annotations cannot be represented by its runtime projection.
    /// </summary>
    internal TypeSymbol? ExactHandlerType { get; init; }
}
