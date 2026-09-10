using System.Reflection.Emit;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Validates handler grammar and ordering for both runtime bodies and editing previews.
/// </summary>
internal static class BlockTransitionParser
{
    /// <summary>
    /// Reads a handler boundary without resolving a type or changing the caller's frame stack.
    /// </summary>
    /// <param name="text">The normalized boundary line.</param>
    /// <param name="frames">The active region stack.</param>
    /// <param name="previous">The previous instruction, if the last entry was an instruction.</param>
    /// <returns>The validated transition.</returns>
    public static BlockTransition Parse(string text, IReadOnlyList<BlockKind> frames, OpCode? previous)
    {
        var rest = text.StartsWith('}') ? text[1..].Trim() : text;
        if (rest.Length == 0)
        {
            if (frames.Count == 0)
            {
                throw new ReplException("unexpected '}': no protected region is open");
            }

            if (frames[^1] == BlockKind.Try)
            {
                throw new ReplException(
                    "a .try needs a handler before it closes: } catch T {, } finally {, } fault {, or } filter {");
            }

            if (frames[^1] == BlockKind.Filter)
            {
                throw new ReplException("a filter needs a handler block before the region closes: } handler {");
            }

            return new BlockTransition(BlockKind.End, null, "end of protected region");
        }

        if (rest.EndsWith('{'))
        {
            rest = rest[..^1].Trim();
        }

        if (frames.Count == 0)
        {
            throw new ReplException($"'{rest}' needs an open .try region");
        }

        var current = frames[^1];
        BlockKind kind;
        string? catchType = null;
        string message;
        if (rest.StartsWith("catch", StringComparison.Ordinal))
        {
            catchType = rest[5..].Trim();
            kind = BlockKind.Catch;
            message = "catch";
        }
        else if (rest == "filter")
        {
            kind = BlockKind.Filter;
            message = "filter (end it with endfilter, then } handler {)";
        }
        else if (rest == "handler")
        {
            if (current != BlockKind.Filter)
            {
                throw new ReplException("'} handler {' is only valid after a filter block");
            }

            if (previous != OpCodes.Endfilter)
            {
                throw new ReplException("a filter must end with endfilter, leaving one int32 on the stack");
            }

            kind = BlockKind.FilterHandler;
            message = "filter handler";
        }
        else if (rest == "finally")
        {
            kind = BlockKind.Finally;
            message = "finally";
        }
        else if (rest == "fault")
        {
            kind = BlockKind.Fault;
            message = "fault";
        }
        else
        {
            throw new ReplException($"unknown handler '{rest}'; expected catch T, filter, handler, finally, or fault");
        }

        if (kind != BlockKind.FilterHandler && current == BlockKind.Filter)
        {
            throw new ReplException("a filter needs a handler block (} handler {) before the next handler");
        }

        if (kind != BlockKind.FilterHandler && current is BlockKind.Finally or BlockKind.Fault)
        {
            throw new ReplException("finally and fault must be the last handler of a region");
        }

        return new BlockTransition(kind, catchType, message);
    }
}
