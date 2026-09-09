using System.Globalization;

namespace IlRepl.Engine;

/// <summary>
/// Parses and renders the sizes and lower bounds retained in CLI array signatures.
/// </summary>
internal static class ArraySignatureShape
{
    /// <summary>
    /// Reads ILAsm dimensions, including the zero-filled prefixes required by the signature encoding.
    /// </summary>
    /// <param name="shape">The text between the array brackets.</param>
    /// <returns>The encoded sizes and lower bounds.</returns>
    public static (IReadOnlyList<int> Sizes, IReadOnlyList<int> LowerBounds) Parse(string shape)
    {
        var dimensions = shape.Split(',');
        var sizes = new int[dimensions.Length];
        var bounds = new int[dimensions.Length];
        var sizeCount = 0;
        var boundCount = 0;
        for (var index = 0; index < dimensions.Length; index++)
        {
            var dimension = dimensions[index].Trim();
            if (dimension is "" or "...")
            {
                continue;
            }

            var separator = dimension.IndexOf("...", StringComparison.Ordinal);
            if (separator == 0 && dimension.StartsWith("...+", StringComparison.Ordinal))
            {
                sizes[index] = Number(dimension[4..], 0, 0x1fffffff);
                sizeCount = index + 1;
                continue;
            }

            if (separator < 0)
            {
                sizes[index] = Number(dimension, 0, 0x1fffffff);
                sizeCount = boundCount = index + 1;
                continue;
            }

            bounds[index] = Number(dimension[..separator], -0x10000000, 0x0fffffff);
            boundCount = index + 1;
            if (separator + 3 < dimension.Length)
            {
                var upper = Number(dimension[(separator + 3)..], int.MinValue, int.MaxValue);
                var size = (long)upper - bounds[index] + 1;
                if (size <= 0 || size > 0x1fffffff)
                {
                    throw new ReplException($"invalid array bounds '{dimension}'");
                }

                sizes[index] = (int)size;
                sizeCount = index + 1;
            }
        }

        return (sizes[..sizeCount], bounds[..boundCount]);
    }

    /// <summary>
    /// Renders every dimension without replacing omitted bounds or sizes with explicit values.
    /// </summary>
    /// <param name="rank">The number of dimensions.</param>
    /// <param name="sizes">The leading dimensions with declared sizes.</param>
    /// <param name="lowerBounds">The leading dimensions with declared lower bounds.</param>
    /// <returns>The bracketed ILAsm array suffix.</returns>
    public static string Render(int rank, IReadOnlyList<int> sizes, IReadOnlyList<int> lowerBounds)
    {
        var dimensions = new string[rank];
        for (var index = 0; index < rank; index++)
        {
            var hasSize = index < sizes.Count;
            var hasLower = index < lowerBounds.Count;
            dimensions[index] = (hasLower, hasSize) switch
            {
                (true, true) when sizes[index] == 0 && lowerBounds[index] != 0 && index + 1 < sizes.Count
                    => lowerBounds[index].ToString(CultureInfo.InvariantCulture) + "...",
                (true, true) when lowerBounds[index] == 0 => sizes[index].ToString(CultureInfo.InvariantCulture),
                (true, true) => lowerBounds[index].ToString(CultureInfo.InvariantCulture) + "..."
                    + ((long)lowerBounds[index] + sizes[index] - 1).ToString(CultureInfo.InvariantCulture),
                (true, false) => lowerBounds[index].ToString(CultureInfo.InvariantCulture) + "...",
                (false, true) => "...+" + sizes[index].ToString(CultureInfo.InvariantCulture),
                _ => rank == 1 ? "..." : "",
            };
        }

        return "[" + string.Join(",", dimensions) + "]";
    }

    /// <summary>
    /// Renders a native ILAsm suffix, refusing shapes that its grammar cannot preserve exactly.
    /// </summary>
    /// <param name="rank">The number of dimensions.</param>
    /// <param name="sizes">The leading dimensions with declared sizes.</param>
    /// <param name="lowerBounds">The leading dimensions with declared lower bounds.</param>
    /// <returns>The bracketed native ILAsm array suffix.</returns>
    public static string RenderNative(int rank, IReadOnlyList<int> sizes, IReadOnlyList<int> lowerBounds)
    {
        if (sizes.Count > lowerBounds.Count)
        {
            throw new ReplException("native ILAsm cannot preserve an array size with an omitted lower bound; use .save for exact metadata");
        }

        return Render(rank, sizes, lowerBounds);
    }

    private static int Number(string text, int minimum, int maximum)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < minimum || value > maximum)
        {
            throw new ReplException($"invalid array bound or size '{text}'");
        }

        return value;
    }
}
