using System.Text.Json;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Formats typed comparison observations for the session transcript.
/// </summary>
public sealed partial class ReplCore
{
    /// <summary>
    /// Appends the isolated observations to the live transcript without running session code.
    /// </summary>
    /// <param name="comparison">The independently observed execution results.</param>
    internal void ReportComparison(ComparisonReply comparison)
    {
        Note($"{comparison.Name}: {comparison.Outcome} (original vs revision {comparison.Revision})");
        Side("original", comparison.Original);
        Side("edited", comparison.Edited);
    }

    private void Side(string name, ComparisonSide side)
    {
        Note(name + ": " + side.Outcome + (side.Detail is null ? "" : " — " + side.Detail));
        if (side.Exception is { } exception)
        {
            Listing($"    threw {exception.Type}: {exception.Message} (HRESULT 0x{exception.HResult:x8})");
        }
        else if (side.Result is { } result)
        {
            Listing("    return: " + Describe(result));
        }

        foreach (var call in side.Invocations.Select((invocation, index) => (invocation, index)))
        {
            foreach (var member in call.invocation.Outputs)
            {
                Listing($"    call {call.index + 1} {member.Name}: {Describe(member.Value)}");
            }
        }

        if (side.StandardOutput.Length != 0)
        {
            Listing("    stdout: " + JsonSerializer.Serialize(side.StandardOutput, ProtocolJsonContext.Default.String));
        }

        if (side.StandardError.Length != 0)
        {
            Listing("    stderr: " + JsonSerializer.Serialize(side.StandardError, ProtocolJsonContext.Default.String));
        }
    }

    private static string Describe(ObservedValue value) => value.Kind switch
    {
        "null" => "null",
        "scalar" => value.Type + " " + JsonSerializer.Serialize(value.Value, ProtocolJsonContext.Default.String),
        "reference" => "reference #" + value.Identity,
        "unavailable" => "unavailable: " + value.Value,
        _ => value.Type + (value.Identity is { } identity ? " #" + identity : "") + " { "
            + string.Join(", ", value.Members.Select(member => member.Name + " = " + Describe(member.Value))) + " }",
    };
}
