namespace IlRepl.Engine;

/// <summary>
/// Finds deterministic shortest edit sequences with bounded memory for large rewrites.
/// </summary>
internal static class SequenceDiff
{
    internal static IReadOnlyList<(int Original, int Edited)> Match(IReadOnlyList<string> original, IReadOnlyList<string> edited)
    {
        var maximum = original.Count + edited.Count;
        if (maximum == 0)
        {
            return [];
        }

        var frontier = new Dictionary<int, int> { [1] = 0 };
        var trace = new List<Dictionary<int, int>>();
        for (var distance = 0; distance <= maximum; distance++)
        {
            if (distance == 256)
            {
                return LinearMatch(original, edited);
            }

            trace.Add(new Dictionary<int, int>(frontier));
            for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
            {
                var x = diagonal == -distance || (diagonal != distance
                    && frontier.GetValueOrDefault(diagonal - 1) < frontier.GetValueOrDefault(diagonal + 1))
                    ? frontier.GetValueOrDefault(diagonal + 1) : frontier.GetValueOrDefault(diagonal - 1) + 1;
                var y = x - diagonal;
                while (x < original.Count && y < edited.Count && original[x] == edited[y])
                {
                    x++;
                    y++;
                }

                frontier[diagonal] = x;
                if (x >= original.Count && y >= edited.Count)
                {
                    return Reconstruct(trace, x, y);
                }
            }
        }

        throw new InvalidOperationException("the sequence frontier did not reach its endpoint");
    }

    private static List<(int, int)> LinearMatch(IReadOnlyList<string> original, IReadOnlyList<string> edited)
    {
        var result = new List<(int, int)>();
        Align(0, original.Count, 0, edited.Count);
        return result;

        void Align(int leftStart, int leftEnd, int rightStart, int rightEnd)
        {
            var common = new HashSet<string>(StringComparer.Ordinal);
            for (var index = leftStart; index < leftEnd; index++)
            {
                common.Add(original[index]);
            }

            var matching = -1;
            for (var index = rightStart; index < rightEnd; index++)
            {
                if (common.Contains(edited[index]))
                {
                    matching = index;
                    break;
                }
            }

            if (matching < 0 || leftEnd - leftStart == 1)
            {
                for (var index = leftStart; index < leftEnd; index++)
                {
                    if (matching < 0)
                    {
                        result.Add((index, -1));
                    }
                }

                for (var index = rightStart; index < rightEnd; index++)
                {
                    result.Add(index == matching ? (leftStart, index) : (-1, index));
                }

                return;
            }

            var middle = leftStart + (leftEnd - leftStart) / 2;
            var split = Split(leftStart, middle, leftEnd, rightStart, rightEnd);
            Align(leftStart, middle, rightStart, split);
            Align(middle, leftEnd, split, rightEnd);
        }

        int Split(int leftStart, int middle, int leftEnd, int rightStart, int rightEnd)
        {
            var length = rightEnd - rightStart;
            var forward = new int[length + 1];
            var backward = new int[length + 1];
            for (var left = leftStart; left < middle; left++)
            {
                var diagonal = 0;
                for (var right = 0; right < length; right++)
                {
                    var previous = forward[right + 1];
                    forward[right + 1] = original[left] == edited[rightStart + right] ? diagonal + 1
                        : Math.Max(forward[right], previous);
                    diagonal = previous;
                }
            }

            for (var left = leftEnd - 1; left >= middle; left--)
            {
                var diagonal = 0;
                for (var right = length - 1; right >= 0; right--)
                {
                    var previous = backward[right];
                    backward[right] = original[left] == edited[rightStart + right] ? diagonal + 1
                        : Math.Max(backward[right + 1], previous);
                    diagonal = previous;
                }
            }

            var best = 0;
            for (var index = 1; index <= length; index++)
            {
                if (forward[index] + backward[index] > forward[best] + backward[best])
                {
                    best = index;
                }
            }

            return rightStart + best;
        }
    }

    private static List<(int, int)> Reconstruct(List<Dictionary<int, int>> trace, int x, int y)
    {
        var result = new List<(int, int)>();
        for (var distance = trace.Count - 1; distance >= 0; distance--)
        {
            var frontier = trace[distance];
            var diagonal = x - y;
            var previousDiagonal = diagonal == -distance || (diagonal != distance
                && frontier.GetValueOrDefault(diagonal - 1) < frontier.GetValueOrDefault(diagonal + 1))
                ? diagonal + 1 : diagonal - 1;
            var previousX = frontier.GetValueOrDefault(previousDiagonal);
            var previousY = previousX - previousDiagonal;
            while (x > previousX && y > previousY)
            {
                result.Add((--x, --y));
            }

            if (distance > 0)
            {
                result.Add(x == previousX ? (-1, --y) : (--x, -1));
            }
        }

        result.Reverse();
        return result;
    }
}
