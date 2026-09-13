namespace IlRepl.Engine;

/// <summary>
/// An ordered exception clause whose exclusive ranges follow source labels as instructions move.
/// </summary>
/// <typeparam name="T">The catch-type representation.</typeparam>
/// <param name="Kind">The clause kind.</param>
/// <param name="TryStart">The first protected instruction.</param>
/// <param name="TryEnd">The exclusive protected end.</param>
/// <param name="HandlerStart">The first handler instruction.</param>
/// <param name="HandlerEnd">The exclusive handler end.</param>
/// <param name="FilterStart">The filter entry, when present.</param>
/// <param name="CatchType">The catch type, when present.</param>
public sealed record ExceptionRegion<T>(IlClauseKind Kind, string TryStart, string TryEnd,
    string HandlerStart, string HandlerEnd, string? FilterStart, T? CatchType) where T : class
{
    /// <summary>
    /// Every referenced boundary, in declaration order.
    /// </summary>
    public IEnumerable<string> Labels => new[] { TryStart, TryEnd, HandlerStart, HandlerEnd }
        .Concat(FilterStart is null ? [] : new[] { FilterStart });

    /// <summary>
    /// Renders the standard ILAsm range form.
    /// </summary>
    /// <param name="render">Formats the catch type.</param>
    /// <returns>The complete directive.</returns>
    public string Describe(Func<T, string> render) => $".try {TryStart} to {TryEnd} " + (Kind switch
    {
        IlClauseKind.Catch => "catch " + render(CatchType!),
        IlClauseKind.Filter => "filter " + FilterStart,
        IlClauseKind.Finally => "finally",
        _ => "fault",
    }) + $" handler {HandlerStart} to {HandlerEnd}";
}
