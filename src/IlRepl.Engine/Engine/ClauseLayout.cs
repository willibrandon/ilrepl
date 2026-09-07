namespace IlRepl.Engine;

/// <summary>
/// Decides how exception clauses are drawn. Clauses that nest the way ILGenerator, the C#
/// compiler, and the REPL's own emitter lay them out become <c>.try { } catch T { } finally { }</c>
/// blocks: a finally or fault whose protected range is exactly another region's try plus its
/// handlers folds into that region as its trailing handler, and regions nest inside a try, a
/// filter, or a handler. Anything else keeps its clauses in ildasm's offset form.
/// </summary>
public static class ClauseLayout
{
    /// <summary>
    /// Lays the clauses out.
    /// </summary>
    /// <param name="clauses">The normalized clauses.</param>
    /// <param name="codeSize">The number of IL bytes, which a clause may end at.</param>
    /// <param name="fold">True to fold a finally or fault over a try and its handlers into that region, the REPL's
    /// own <c>} catch { } finally {</c> shape; false to keep every protected range its own region, which is how
    /// native ILAsm reads the same clauses.</param>
    /// <returns>The block lines, the fallback clauses, and the offsets the fallback names.</returns>
    public static ClauseLayoutResult Build(IReadOnlyList<IlExceptionClause> clauses, int codeSize, bool fold = true)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        var fallback = new List<IlExceptionClause>();
        var regions = clauses
            .GroupBy(c => (c.TryStart, c.TryEnd))
            .Select(g => new Region(g.Key.TryStart, g.Key.TryEnd, [.. g.OrderBy(c => c.LexicalStart)]))
            .ToList();

        // A finally or fault over another region's try and handlers is that region's last handler.
        var folded = fold;
        while (folded)
        {
            folded = false;
            foreach (var trailing in regions)
            {
                if (trailing.Handlers.Count != 1 || trailing.Handlers[0].Kind is not (IlClauseKind.Finally or IlClauseKind.Fault) || trailing.Handlers[0].LexicalStart != trailing.TryEnd)
                {
                    continue;
                }

                var owner = regions.FirstOrDefault(r => !ReferenceEquals(r, trailing) && r.TryStart == trailing.TryStart && r.End == trailing.TryEnd);
                if (owner is null)
                {
                    continue;
                }

                owner.Handlers.Add(trailing.Handlers[0]);
                regions.Remove(trailing);
                folded = true;
                break;
            }
        }

        // Braces can only draw a region whose parts follow each other without a gap.
        var contiguous = new List<Region>();
        foreach (var region in regions)
        {
            if (region.IsContiguous)
            {
                contiguous.Add(region);
            }
            else
            {
                fallback.AddRange(region.Handlers);
            }
        }

        // And only regions that nest: inside a try, a filter, or a handler of another, or apart.
        var accepted = new List<Region>();
        foreach (var region in contiguous.OrderBy(r => r.TryStart).ThenByDescending(r => r.Extent))
        {
            if (accepted.All(outer => Fits(region, outer)))
            {
                accepted.Add(region);
            }
            else
            {
                fallback.AddRange(region.Handlers);
            }
        }

        var boundaries = new List<BlockBoundary>();
        foreach (var region in accepted)
        {
            int Open(int extent) => (1 << 30) + (codeSize - extent);
            boundaries.Add(new BlockBoundary(region.TryStart, Open(region.Extent), BlockKind.Try, null));
            foreach (var handler in region.Handlers)
            {
                switch (handler.Kind)
                {
                    case IlClauseKind.Catch:
                        boundaries.Add(new BlockBoundary(handler.HandlerStart, Open(region.Extent), BlockKind.Catch, handler));
                        break;
                    case IlClauseKind.Filter:
                        boundaries.Add(new BlockBoundary(handler.FilterStart!.Value, Open(region.Extent), BlockKind.Filter, handler));
                        boundaries.Add(new BlockBoundary(handler.HandlerStart, Open(region.Extent), BlockKind.FilterHandler, handler));
                        break;
                    case IlClauseKind.Finally:
                        boundaries.Add(new BlockBoundary(handler.HandlerStart, Open(region.Extent), BlockKind.Finally, handler));
                        break;
                    default:
                        boundaries.Add(new BlockBoundary(handler.HandlerStart, Open(region.Extent), BlockKind.Fault, handler));
                        break;
                }
            }

            boundaries.Add(new BlockBoundary(region.End, region.Extent, BlockKind.End, null));
        }

        var referenced = new HashSet<int>();
        foreach (var clause in fallback)
        {
            referenced.Add(clause.TryStart);
            referenced.Add(clause.TryEnd);
            referenced.Add(clause.HandlerStart);
            referenced.Add(clause.HandlerEnd);
            if (clause.FilterStart is int filter)
            {
                referenced.Add(filter);
            }
        }

        return new ClauseLayoutResult([.. boundaries.OrderBy(b => b.Offset).ThenBy(b => b.Order)], fallback, referenced);
    }

    private static bool Fits(Region inner, Region outer)
    {
        if (inner.End <= outer.TryStart || inner.TryStart >= outer.End)
        {
            return true;
        }

        if (inner.TryStart < outer.TryStart || inner.End > outer.End)
        {
            return false;
        }

        if (inner.TryStart >= outer.TryStart && inner.End <= outer.TryEnd)
        {
            return true;
        }

        foreach (var handler in outer.Handlers)
        {
            if (handler.FilterStart is int filter && inner.TryStart >= filter && inner.End <= handler.HandlerStart)
            {
                return true;
            }

            if (inner.TryStart >= handler.HandlerStart && inner.End <= handler.HandlerEnd)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class Region(int tryStart, int tryEnd, List<IlExceptionClause> handlers)
    {
        public int TryStart { get; } = tryStart;

        public int TryEnd { get; } = tryEnd;

        public List<IlExceptionClause> Handlers { get; } = handlers;

        public int End => Handlers[^1].HandlerEnd;

        public int Extent => End - TryStart;

        public bool IsContiguous
        {
            get
            {
                if (TryEnd <= TryStart || Handlers[0].LexicalStart != TryEnd)
                {
                    return false;
                }

                for (var i = 0; i < Handlers.Count; i++)
                {
                    var h = Handlers[i];
                    if (h.HandlerEnd <= h.HandlerStart || (h.FilterStart is int filter && filter >= h.HandlerStart))
                    {
                        return false;
                    }

                    if (i > 0 && Handlers[i - 1].HandlerEnd != h.LexicalStart)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
