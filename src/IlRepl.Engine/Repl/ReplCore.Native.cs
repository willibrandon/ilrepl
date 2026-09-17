using System.Collections;
using System.Globalization;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Captures native inspections and renders isolated compilation reports.
/// </summary>
public sealed partial class ReplCore
{
    private HandleResult Native(string argument, bool diff = false)
    {
        if (OperatingSystem.IsBrowser())
            throw new ReplException("native JIT inspection requires the terminal's CoreCLR host; it is unavailable in the browser demo");
        var options = NativeCommand.Parse(argument);
        if (diff)
        {
            var edit = options.Selector.Length == 0 ? Session.Edits.Count == 0 ? null : Session.Edits[^1]
                : Session.Edits.FirstOrDefault(item => item.Name == options.Selector);
            if (edit is null) throw new ReplException("no matching edit; create one with .edit first");
            if (options.Against is not null) throw new ReplException("use .jit with --against for arbitrary native comparisons");
            options = options with { Selector = edit.Name, Against = edit.Name, Original = true };
        }
        if (options.Assert && options.Against is null) throw new ReplException("--assert requires a native comparison");
        var left = options.Info ? new NativeTarget { Name = "host capabilities" } : CaptureNativeTarget(options.Selector, options);
        var right = options.Against is { } other
            ? CaptureNativeTarget(other, options with { Original = false }) : null;
        if (right is not null)
        {
            left = left with { Name = diff ? left.Name : "left: " + left.Name };
            right = right with { Name = diff ? options.Selector + " (edited)" : "right: " + right.Name };
        }
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(pair => (string)pair.Key, pair => (string)pair.Value!, StringComparer.Ordinal);
        var files = ComparisonCapture.Files(options.FixtureDirectory);
        var package = new NativePackage
        {
            Options = options, Left = left, Right = right, Environment = environment, Files = [.. files],
            Culture = CultureInfo.CurrentCulture.Name, UICulture = CultureInfo.CurrentUICulture.Name,
        };
        Note($"native inspection: {left.Name}" + (right is null ? "" : " against " + right.Name));
        return new HandleResult(true, false) { NativePackage = package };
    }

    private NativeTarget CaptureNativeTarget(string selector, NativeOptions options)
    {
        if (selector.Length != 0 && !selector.StartsWith("cell ", StringComparison.Ordinal))
            return NativeCapture.Create(Session, selector, options);
        if (selector.Length == 0 && !Session.Cell.IsEmpty) return NativeCapture.Create(Session, "", options);
        var document = CaptureSession(new SessionEditor());
        var number = selector.Length == 0
            ? document.Entries.LastOrDefault(entry => entry.Kind == SessionEntryKind.Run)?.Number
            : int.TryParse(selector.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null;
        if (number is null) throw new ReplException("there is no retained executable cell; use .jit cell <number>");
        var boundary = Array.FindIndex(document.Entries, entry => entry.Number == number && entry.Kind == SessionEntryKind.Run);
        if (boundary < 0) throw new ReplException($"cell {number} is not a retained executable cell");
        using var reconstructed = new ReplCore(new Session { DeferActivation = true }, ColdOptions());
        var historical = document with
        {
            Entries = document.Entries[..boundary], Cells = document.Cells.Where(cell => cell.Number < number).ToArray(),
        };
        var problems = reconstructed.ReopenSession(historical);
        if (problems.Length != 0) throw new ReplException(string.Join('\n', problems));
        return NativeCapture.Create(reconstructed.Session, "", options) with { Name = $"cell {number}" };
    }

    /// <summary>
    /// Appends native evidence without changing the session's execution state.
    /// </summary>
    /// <param name="reply">The isolated worker results.</param>
    internal void ReportNative(NativeReply reply)
    {
        Note("native inspection: " + reply.Outcome);
        var compared = reply.Right is not null && reply.Outcome is "equal" or "different";
        var addresses = reply.Left.Addresses.Concat(reply.Right?.Addresses ?? []).ToArray();
        RenderNativeSide(reply.Left, !compared || reply.Outcome == "equal", addresses);
        if (reply.Right is { } right) RenderNativeSide(right, !compared, addresses);
        foreach (var line in reply.Difference) NativeListing(NativeSymbolDisplay.Format(line, addresses));
    }

    private void NativeListing(string text) => Transcript.Add(new TranscriptLine(LineKind.Listing, NativeListingFormatter.Spans(text)));

    private void RenderNativeSide(NativeReport report, bool listing, NativeAddressFact[] addresses)
    {
        var runtime = report.Runtime.Split(';')[0];
        var platform = string.Join(' ', new[] { runtime, report.Architecture }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var summary = report.Name + ": " + report.Outcome;
        if (platform.Length != 0) summary += "; " + platform;
        if (!report.IsCapability)
            summary += $"; {(report.Collectible ? "collectible" : "noncollectible")}; {report.Invocations} workload invocations";
        Note(summary);
        if (report.Detail is not null) Note(report.Detail);
        if (report.IsCapability) RenderNativeInfo(report);
        else if (report.Collectible) Note("collectible static-access helpers; tiering unavailable");
        if (!report.IsCapability && report.Settings.GetValueOrDefault("DOTNET_ReadyToRun") == "0")
            Note("ReadyToRun disabled globally because the selected assembly name cannot be excluded");
        var compilations = listing ? report.Compilations : report.Compilations.TakeLast(1);
        foreach (var compilation in compilations)
        {
            var profile = compilation.Pgo == "Not applicable" ? "PGO not applicable" : compilation.Pgo + " PGO";
            Note($"{compilation.Method}: {compilation.Tier}; {profile}; {compilation.CodeSize} native bytes");
            if (listing || report.Raw)
                foreach (var header in NativeDisassembly.Headers(compilation, report.Raw)) NativeListing("  " + header);
            if (listing)
            {
                foreach (var line in compilation.Normalized)
                    NativeListing("  " + (report.Raw ? line : NativeSymbolDisplay.Format(line, addresses)));
                foreach (var inlinee in compilation.Inlinees) Note("inlined: " + inlinee);
            }
        }
        foreach (var problem in report.NormalizationProblems) Note("unresolved: " + problem);
        foreach (var raw in report.UnattributedListings)
        {
            Note("unattributed output; excluded from comparison:");
            foreach (var line in raw.Split('\n')) NativeListing("  " + line);
        }
        if (report.StandardOutput.Length != 0) Listing("stdout: " + report.StandardOutput);
        if (report.StandardError.Length != 0) Listing("stderr: " + report.StandardError);
    }

    private void RenderNativeInfo(NativeReport report)
    {
        Note(report.Runtime + "; " + report.OperatingSystem);
        Note("JIT: " + report.Jit);
        if (report.RuntimeIdentifier.Length != 0) Note("platform: " + report.RuntimeIdentifier);
        if (report.InstructionSets.Length != 0) Note("effective ISA: " + NativeInstructionSets.Format(report.InstructionSets));
        foreach (var (name, value) in report.Settings.OrderBy(pair => pair.Key, StringComparer.Ordinal)) Note(name + "=" + value);
    }
}
