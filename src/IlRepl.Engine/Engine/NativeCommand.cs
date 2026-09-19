using System.Globalization;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Parses native inspection selectors and explicit workloads without loading or executing a target.
/// </summary>
public static class NativeCommand
{
    /// <summary>
    /// Recognizes a native comparison flag outside quoted names and workload literals.
    /// </summary>
    /// <param name="text">The text following .diff.</param>
    /// <returns>Whether the command selects the native comparison grammar.</returns>
    internal static bool IsNativeComparison(string text) => Words(text).Contains("--native", StringComparer.Ordinal);

    /// <summary>
    /// Parses the text following .jit or .diff with --native.
    /// </summary>
    /// <param name="text">The selector and options.</param>
    /// <returns>The validated inspection settings.</returns>
    public static NativeOptions Parse(string text)
    {
        var words = Words(text);
        var options = new NativeOptions();
        var selector = new List<string>();
        var against = new List<string>();
        var right = false;
        var iterations = false;
        var pgo = false;
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            string Value()
            {
                if (++index >= words.Count)
                {
                    throw new ReplException($"{word} requires a value");
                }

                return words[index];
            }

            switch (word)
            {
                case "--native": break;
                case "--against":
                    if (right)
                    {
                        throw new ReplException("--against may appear only once");
                    }

                    right = true;
                    break;
                case "--original": options = options with { Original = true }; break;
                case "--info": options = options with { Info = true }; break;
                case "--raw": options = options with { Raw = true }; break;
                case "--assert": options = options with { Assert = true }; break;
                case "--collectible": options = options with { Collectible = true }; break;
                case "--run": options = options with { Run = true }; break;
                case "--allow-initializers": options = options with { AllowInitializers = true }; break;
                case "--tier":
                    var tier = Value().ToLowerInvariant();
                    if (tier == "optimized")
                    {
                        tier = "fullopts";
                    }

                    if (tier is not ("fullopts" or "tier0" or "tier1"))
                    {
                        throw new ReplException("--tier requires fullopts, tier0, or tier1");
                    }

                    options = options with { Tier = tier };
                    break;
                case "--pgo":
                    var profile = Value();
                    if (profile is not ("on" or "off"))
                    {
                        throw new ReplException("--pgo requires on or off");
                    }

                    options = options with { Pgo = profile == "on" };
                    pgo = true;
                    break;
                case "--iterations":
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
                    {
                        throw new ReplException("--iterations requires a positive integer");
                    }

                    options = options with { Iterations = count };
                    iterations = true;
                    break;
                case "--timeout": options = options with { TimeoutMilliseconds = ComparisonDuration.Parse(Value()) }; break;
                case "--stdin": options = options with { StandardInput = LiteralParser.ParseString(Value()) }; break;
                case "--files": options = options with { FixtureDirectory = Unquote(Value()) }; break;
                case "--env":
                    var assignment = Unquote(Value());
                    var separator = assignment.IndexOf('=');
                    if (separator < 1 || assignment.Contains('\0'))
                    {
                        throw new ReplException("--env requires NAME=VALUE");
                    }

                    var key = assignment[..separator];
                    if (!options.Environment.TryAdd(key, assignment[(separator + 1)..]))
                    {
                        throw new ReplException($"environment override '{key}' was supplied more than once");
                    }

                    break;
                case "using":
                    if (options.Scenario is not null || options.Arguments is not null)
                    {
                        throw new ReplException("supply one literal workload or one scenario");
                    }

                    options = options with { Scenario = InstructionParser.Unquote(Value()), Run = true };
                    break;
                default:
                    if (word.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ReplException($"unknown native option '{word}'");
                    }

                    if (word.StartsWith('('))
                    {
                        if (options.Scenario is not null || options.Arguments is not null)
                        {
                            throw new ReplException("supply one literal workload or one scenario");
                        }

                        var call = ComparisonCommand.Parse("native " + word);
                        options = options with { Arguments = [.. call.Arguments], Run = true };
                    }
                    else
                    {
                        (right ? against : selector).Add(word);
                    }

                    break;
            }
        }

        if (right && against.Count == 0)
        {
            throw new ReplException("--against requires a selector");
        }

        options = options with
        {
            Selector = string.Join(' ', selector), Against = right ? string.Join(' ', against) : null,
            Iterations = iterations ? options.Iterations : options.Tier == "tier1" ? 1000 : 1,
        };

        if (options.Collectible && options.Tier != "fullopts")
        {
            throw new ReplException("collectible methods cannot be tiered; remove --collectible or use --tier fullopts");
        }

        if (options.Tier == "tier1" && !options.Run)
        {
            throw new ReplException("Tier1 requires explicit execution: supply (literals), using Scenario, or --run");
        }

        if (iterations && !options.Run)
        {
            throw new ReplException("--iterations requires an explicit workload");
        }

        if (pgo && options.Tier == "fullopts")
        {
            throw new ReplException("--pgo applies to tier0 and tier1");
        }

        if (options.Info && (options.Selector.Length != 0 || options.Against is not null || options.Run))
        {
            throw new ReplException("--info cannot be combined with a target or workload");
        }

        return options;
    }

    private static string Unquote(string value) => value.StartsWith('"') || value.StartsWith('\'')
        ? LiteralParser.ParseString(value) : value;

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var start = 0;
        var depth = 0;
        var quote = '\0';
        for (var index = 0; index <= text.Length; index++)
        {
            var current = index == text.Length ? ' ' : text[index];
            if (quote != '\0')
            {
                if (current == '\\')
                {
                    index++;
                }
                else if (current == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
            }
            else if (current is '(' or '[' or '<')
            {
                depth++;
            }
            else if (current is ')' or ']' or '>')
            {
                depth--;
            }
            else if (char.IsWhiteSpace(current) && depth == 0)
            {
                if (index > start)
                {
                    words.Add(text[start..index]);
                }

                start = index + 1;
            }

            if (depth < 0)
            {
                throw new ReplException("unbalanced native selector or literal arguments");
            }
        }

        if (quote != '\0' || depth != 0)
        {
            throw new ReplException("unterminated native selector or literal arguments");
        }

        return words;
    }
}
