using System.Globalization;
using System.Text;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Classifies workspace commands once for the terminal, batch runner, and browser coordinator.
/// </summary>
public sealed partial class ReplCore
{
    private bool TrySessionAction(NormalizedLine line, out SessionAction? action)
    {
        action = null;
        if (line.Kind != SourceLineKind.Text)
        {
            return false;
        }

        var separator = line.Text.IndexOfAny([' ', '\t']);
        var command = separator < 0 ? line.Text : line.Text[..separator];
        if (command is not (".session" or ".save" or ".load"))
        {
            return false;
        }

        var words = SessionWords(separator < 0 ? "" : line.Text[(separator + 1)..]);
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
            if (!isSession && (command != ".load" || (!isProject
                && !path.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase) && !words.Contains("--reload")
                && !(ReferenceActions && (File.Exists(path) || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))))))
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
            "cells" => SessionOperation.Cells,
            "cell" => SessionOperation.Cell,
            "run" => SessionOperation.Run,
            _ => throw new ReplException("usage: .session [save|open|restore|cells|cell|run]"),
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
                _ when word.StartsWith("--", StringComparison.Ordinal) => throw new ReplException($"unsupported option '{word}'"),
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
                    throw new ReplException($"invalid cell number or range '{value}'");
                }

                for (long number = first; number <= last; number++)
                {
                    if (!numbers.Add((int)number))
                    {
                        throw new ReplException($"cell {number} was selected more than once");
                    }
                }
            }

            if (operation == SessionOperation.Cell && numbers.Count != 1)
            {
                throw new ReplException("usage: .session cell <number>");
            }

            return action with { Numbers = [.. numbers] };
        }

        if (operation is SessionOperation.Open or SessionOperation.Load && values.Count != 1)
        {
            throw new ReplException($"{operation.ToString().ToLowerInvariant()} requires one path or reference");
        }

        if (values.Count > (operation is SessionOperation.Save or SessionOperation.Open or SessionOperation.Load ? 1 : 0))
        {
            throw new ReplException("unexpected session argument");
        }

        return action with { Path = values.FirstOrDefault() };
    }

    private static bool Positive(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    private static string NextValue(List<string> words, ref int index)
    {
        if (++index >= words.Count || words[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ReplException("the option requires a value");
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
            throw new ReplException("the path has an unterminated quote");
        }

        if (started)
        {
            words.Add(buffer.ToString());
        }

        return words;
    }

    private static string UnquotePath(string text)
    {
        if (!text.StartsWith('"'))
        {
            return text;
        }

        var words = SessionWords(text);
        return words.Count == 1 ? words[0] : throw new ReplException("specify one quoted path");
    }
}
