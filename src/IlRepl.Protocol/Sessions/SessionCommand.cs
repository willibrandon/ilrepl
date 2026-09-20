using System.Globalization;
using System.Text;

namespace IlRepl.Protocol;

/// <summary>
/// Parses workspace commands identically before and after an execution host is available.
/// </summary>
public static class SessionCommand
{
    /// <summary>
    /// Classifies normalized command text without accessing an execution runtime.
    /// </summary>
    /// <param name="text">Comment-free command text.</param>
    /// <param name="referenceActions">Whether file references use host dependency tooling.</param>
    /// <param name="action">The classified operation.</param>
    /// <returns>Whether the text is a workspace command.</returns>
    public static bool TryParse(string text, bool referenceActions, out SessionAction? action)
    {
        action = null;
        var separator = text.IndexOfAny([' ', '\t']);
        var command = separator < 0 ? text : text[..separator];
        if (command is not (".session" or ".save" or ".load"))
        {
            return false;
        }

        var words = SessionWords(separator < 0 ? "" : text[(separator + 1)..]);
        if (command is ".save" or ".load")
        {
            if (words.Count == 0)
            {
                return false;
            }

            var path = words[0];
            var isSession = SessionCodec.IsSessionPath(path);
            var isProject = path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) || Directory.Exists(path);
            bool IsReference() => referenceActions && (File.Exists(path) || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            bool IsDependency() => isProject || path.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase)
                || words.Contains("--reload") || IsReference();
            if (!isSession && (command != ".load" || !IsDependency()))
            {
                return false;
            }

            var operation = command == ".save" ? SessionOperation.Save : isSession ? SessionOperation.Open : SessionOperation.Load;
            action = ParseAction(operation, words);
            return true;
        }

        if (words.Count == 0)
        {
            action = new SessionAction();
            return true;
        }

        var verb = words[0];
        words.RemoveAt(0);
        var requested = verb switch
        {
            "save" => SessionOperation.Save,
            "open" => SessionOperation.Open,
            "restore" => SessionOperation.Restore,
            "restart" => SessionOperation.Restart,
            "cells" => SessionOperation.Cells,
            "cell" => SessionOperation.Cell,
            "run" => SessionOperation.Run,
            _ => throw new ArgumentException("usage: .session [save|open|restore|restart|cells|cell|run]"),
        };

        action = ParseAction(requested, words);
        return true;
    }

    private static SessionAction ParseAction(SessionOperation operation, List<string> words)
    {
        var action = new SessionAction { Operation = operation };
        var values = new List<string>();
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            action = word switch
            {
                "--force" when operation == SessionOperation.Open => action with { Force = true },
                "--embed" when operation == SessionOperation.Save => action with { Embed = true },
                "--build" when operation == SessionOperation.Restore => action with { Build = true },
                "--reload" when operation == SessionOperation.Load => action with { Reload = true },
                "--no-build" when operation == SessionOperation.Load => action with { NoBuild = true },
                "--framework" when operation == SessionOperation.Load => action with { Framework = NextValue(words, ref index) },
                "--configuration" when operation == SessionOperation.Load => action with { Configuration = NextValue(words, ref index) },
                _ when word.StartsWith("--", StringComparison.Ordinal) => throw new ArgumentException($"unsupported option '{word}'"),
                _ => action,
            };

            if (!word.StartsWith("--", StringComparison.Ordinal))
            {
                values.Add(word);
            }
        }

        if (operation is SessionOperation.Run or SessionOperation.Cell)
        {
            var numbers = new SortedSet<int>();
            foreach (var value in values)
            {
                var range = value.Split('-');
                if (range.Length is < 1 or > 2 || !Positive(range[0], out var first)
                    || !Positive(range[^1], out var last) || last < first || (long)last - first > 100000)
                {
                    throw new ArgumentException($"invalid cell number or range '{value}'");
                }

                for (long number = first; number <= last; number++)
                {
                    if (!numbers.Add((int)number))
                    {
                        throw new ArgumentException($"cell {number} was selected more than once");
                    }
                }
            }

            if (operation == SessionOperation.Cell && numbers.Count != 1)
            {
                throw new ArgumentException("usage: .session cell <number>");
            }

            return action with { Numbers = [.. numbers] };
        }

        if (operation is SessionOperation.Open or SessionOperation.Load && values.Count != 1)
        {
            throw new ArgumentException($"{operation.ToString().ToLowerInvariant()} requires one path or reference");
        }

        if (values.Count > (operation is SessionOperation.Save or SessionOperation.Open or SessionOperation.Load ? 1 : 0))
        {
            throw new ArgumentException("unexpected session argument");
        }

        return action with { Path = values.FirstOrDefault() };
    }

    private static bool Positive(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    private static string NextValue(List<string> words, ref int index)
    {
        if (++index >= words.Count || words[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("the option requires a value");
        }

        return words[index];
    }

    private static List<string> SessionWords(string text)
    {
        var words = new List<string>();
        var buffer = new StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var character in text)
        {
            if (character == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (started)
                {
                    words.Add(buffer.ToString());
                    buffer.Clear();
                    started = false;
                }
            }
            else
            {
                buffer.Append(character);
                started = true;
            }
        }

        if (quoted)
        {
            throw new ArgumentException("the path has an unterminated quote");
        }

        if (started)
        {
            words.Add(buffer.ToString());
        }

        return words;
    }

    /// <summary>
    /// Reads one path while preserving spaces within a quoted argument.
    /// </summary>
    /// <param name="text">The raw path argument.</param>
    /// <returns>The path without enclosing quotes.</returns>
    public static string UnquotePath(string text)
    {
        if (!text.StartsWith('"'))
        {
            return text;
        }

        var words = SessionWords(text);
        return words.Count == 1 ? words[0] : throw new ArgumentException("specify one quoted path");
    }
}
