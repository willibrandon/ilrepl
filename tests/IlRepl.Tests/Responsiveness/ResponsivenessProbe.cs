using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using IlRepl.Protocol;
using IlRepl.Processes;

namespace IlRepl.Tests.Responsiveness;

/// <summary>
/// Runs explicit packaged-terminal reference measurements outside normal test discovery and CI test-leg budgets.
/// </summary>
internal static class ResponsivenessProbe
{
    /// <summary>
    /// Handles the opt-in measurement mode and retains every raw latency and process-memory observation.
    /// </summary>
    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--responsiveness-measure") return false;
        if (args.Length is < 3 or > 6)
            throw new ArgumentException("Use --responsiveness-measure <packaged-frontend> <artifact-directory> "
                + "[scenario] [--quick] [--prepare].");
        var frontend = Path.GetFullPath(args[1]);
        if (!File.Exists(frontend)) throw new FileNotFoundException("The packaged frontend does not exist.", frontend);
        var output = Path.GetFullPath(args[2]);
        var quick = args.Contains("--quick", StringComparer.Ordinal);
        var prepare = args.Contains("--prepare", StringComparer.Ordinal);
        var requested = args.Skip(3).FirstOrDefault(argument => argument is not ("--quick" or "--prepare"));
        string[] scenarios = requested is null
            ? ["empty", "catalog", "session", "draft-200", "draft-2000", "generic-4", "generic-16", "generic-64", "combined"]
            : [requested];
        output = Path.Combine(output, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(output);
        Console.WriteLine("Responsiveness artifacts: " + output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
        var token = timeout.Token;
        var frontendHash = SessionCodec.Hash(await File.ReadAllBytesAsync(frontend, token));
        var cache = Path.Combine(RepoPaths.Root, "artifacts", "responsiveness", "fixtures");
        var assembly = ResponsivenessFixtures.Assembly(cache, quick ? 100 : 10_000, 10);
        var sessionBytes = scenarios.Any(scenario => scenario is "session" or "combined")
            ? await RetainedSessionAsync(frontend, cache, quick ? 50 : 10_000, quick ? 10 : 1_000, token)
            : SessionCodec.Write(new SessionDocument());
        var sessionHash = SessionCodec.Hash(sessionBytes);
        var session = Path.Combine(cache, sessionHash + ".ilrepl.json");
        await File.WriteAllBytesAsync(session, sessionBytes, token);
        var fixture = ResponsivenessFixtures.Version + ":" + SessionCodec.Hash(await File.ReadAllBytesAsync(assembly, token))
            + ":" + sessionHash;
        if (prepare)
        {
            Console.WriteLine("Prepared " + fixture);
            return true;
        }
        var sdk = await DescribeAsync("dotnet", ["--version"], token);
        var commit = await DescribeAsync("git", ["rev-parse", "HEAD"], token);
        var dirty = await DescribeAsync("git", ["status", "--porcelain", "--untracked-files=no"], token);
        if (dirty.Length > 0) commit += "+dirty";
        var cpu = await CpuAsync(token);
        var failures = new List<string>();
        foreach (var scenario in scenarios)
        {
            var metrics = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            var launches = new List<(long Started, long Prompt, long FirstEdit, long Accepted)>();
            void Record(string name, double sample)
            {
                if (!metrics.TryGetValue(name, out var values)) metrics.Add(name, values = []);
                values.Add(sample);
            }
            var artifacts = Path.Combine(output, scenario, "processes");
            Directory.CreateDirectory(artifacts);
            var retained = scenario is "session" or "combined" ? session : null;
            var catalog = scenario is "catalog" or "combined";
            for (var sample = 0; sample < (quick ? 2 : 30); sample++)
            {
                await using var terminal = new MeasuredTerminal(frontend, artifacts, retained, token);
                var prompt = await terminal.PromptAsync();
                Record("prompt-painted", Stopwatch.GetElapsedTime(terminal.Started, prompt.Timestamp).TotalMilliseconds);
                await terminal.PasteAsync("// editable", frame => frame.Contains("// editable"));
                var firstEdit = terminal.LastPainted;
                Record("prompt-editable", Stopwatch.GetElapsedTime(terminal.Started, prompt.Timestamp).TotalMilliseconds);
                Record("first-edit-painted", Stopwatch.GetElapsedTime(terminal.Started, firstEdit).TotalMilliseconds);
                await terminal.DraftAsync("ldc.i4.s 42\nret");
                await terminal.CurrentAsync(frame => frame.Contains("stack ") && !frame.Contains("starting execution host")
                    && !frame.Contains("host unavailable"));
                await terminal.InputAsync("\r", frame => frame.Contains("= 42 : int32"));
                launches.Add((terminal.Started, prompt.Timestamp, firstEdit, terminal.LastPainted));
                Record("first-accepted-submission", Stopwatch.GetElapsedTime(terminal.Started, terminal.LastPainted).TotalMilliseconds);
                if (catalog)
                {
                    await terminal.CommandAsync(".load " + assembly, Path.GetFileName(assembly)[..16]);
                    await terminal.DraftAsync("");
                    Record("cold-completion", await terminal.PasteAsync("call Responsiveness.CatalogType00000::Meth",
                        frame => frame.Contains("members") && !frame.Contains("updating members") && frame.Contains("Method00")));
                }
                if (sample != 0) continue;
                var draft = scenario switch
                {
                    "draft-200" => ResponsivenessFixtures.Draft(200),
                    "draft-2000" => ResponsivenessFixtures.Draft(2000),
                    "combined" => CombinedDraft(),
                    "generic-4" => ResponsivenessFixtures.Generic(4),
                    "generic-16" => ResponsivenessFixtures.Generic(16),
                    "generic-64" => ResponsivenessFixtures.Generic(64),
                    _ => "// input",
                };
                await terminal.DraftAsync(draft);
                var suffix = draft.Split('\n')[^1];
                suffix = suffix[^Math.Min(20, suffix.Length)..];
                for (var index = 0; index < (quick ? 25 : 1_025); index++)
                {
                    var typed = await terminal.InputAsync("x", frame => frame.CaretRow?.Contains(suffix + "x", StringComparison.Ordinal)
                        == true);
                    await terminal.InputAsync("\x7f", frame => frame.CaretRow?.Contains(suffix + "x",
                        StringComparison.Ordinal) == false);
                    var restored = await terminal.CurrentAsync(frame => frame.Caret is not null);
                    var draftCaret = restored.Caret!.Value;
                    var moved = await terminal.InputAsync("\x1b[D", frame => frame.Caret == (draftCaret.X - 1, draftCaret.Y));
                    await terminal.InputAsync("\x1b[C", frame => frame.Caret == draftCaret);
                    if (index >= (quick ? 5 : 25))
                    {
                        Record("input-painted", typed);
                        Record("caret-painted", moved);
                    }
                }
                var completionSource = scenario.StartsWith("draft-", StringComparison.Ordinal) || scenario == "combined"
                    ? string.Join('\n', draft.Split('\n')[..^2]) + "\ncall Console::Wr"
                    : scenario.StartsWith("generic-", StringComparison.Ordinal)
                        ? ".locals (" + draft["ldtoken ".Length..] + " value)\ncall Console::Wr" : "call Console::Wr";
                await terminal.DraftAsync(completionSource);
                var ready = await terminal.CurrentAsync(frame => frame.Contains("members") && !frame.Contains("updating members")
                    && frame.Contains("Write("));
                var caret = ready.Caret ?? throw new InvalidOperationException("The completed page has no editing caret.");
                for (var index = 0; index < (quick ? 25 : 1_025); index++)
                {
                    var append = index % 2 == 0;
                    var column = caret.X + (append ? 1 : 0);
                    var completed = await terminal.InputAsync(append ? "i" : "\x7f",
                        frame => frame.Caret?.X == column && frame.Contains("members") && !frame.Contains("updating members")
                            && frame.Contains("Write("));
                    if (index >= (quick ? 5 : 25)) Record("warm-completion", completed);
                }
                Console.WriteLine($"Measured {scenario}: {metrics["input-painted"].Count} interaction samples.");
            }
            var processes = new List<ProcessMeasurement>();
            foreach (var path in Directory.EnumerateFiles(artifacts, "*.json"))
            {
                var measured = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, token),
                    MeasurementJsonContext.Default.ProcessMeasurement);
                if (measured is not null) processes.Add(measured);
            }
            var startups = new List<StartupSample>();
            for (var index = 0; index < launches.Count; index++)
            {
                var launch = launches[index];
                var next = index + 1 == launches.Count ? long.MaxValue : launches[index + 1].Started;
                var process = processes.SingleOrDefault(item => item.Role == "frontend"
                    && item.Stages["entry"] >= launch.Started && item.Stages["entry"] < next);
                if (process is null) throw new InvalidOperationException("A frontend did not retain its measurement artifact.");
                string[] roles = OperatingSystem.IsWindows() ? ["host"] : ["host", "supervisor"];
                foreach (var role in roles)
                    if (!processes.Any(item => item.Role == role && item.Stages["entry"] >= launch.Started
                        && item.Stages["entry"] < next))
                        throw new InvalidOperationException($"A {role} did not retain its measurement artifact.");
                var entry = process.Stages["entry"];
                startups.Add(new StartupSample(launch.Started, launch.Prompt, launch.FirstEdit, launch.Accepted,
                    process.Stages["host-ready"]));
                Record("launch-to-entry", Stopwatch.GetElapsedTime(launch.Started, entry).TotalMilliseconds);
                Record("entry-to-prompt", Stopwatch.GetElapsedTime(entry, launch.Prompt).TotalMilliseconds);
                Record("entry-to-editable", Stopwatch.GetElapsedTime(entry, launch.Prompt).TotalMilliseconds);
                Record("entry-to-first-edit", Stopwatch.GetElapsedTime(entry, launch.FirstEdit).TotalMilliseconds);
                if (process.Stages.TryGetValue("frontend-prepared", out var prepared))
                {
                    Record("entry-to-prepared", Stopwatch.GetElapsedTime(entry, prepared).TotalMilliseconds);
                    Record("prepared-to-prompt", Stopwatch.GetElapsedTime(prepared, launch.Prompt).TotalMilliseconds);
                }
            }
            var record = new ResponsivenessRecord(1, commit, fixture, scenario, sdk, RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription, RuntimeInformation.RuntimeIdentifier, cpu, Environment.MachineName,
                quick ? "Release NativeAOT; harness smoke, not a reference baseline" : "Release NativeAOT; reference",
                frontend, frontendHash, Stopwatch.Frequency, [.. startups],
                metrics.ToDictionary(pair => pair.Key, pair => LatencySamples.From(pair.Value)), [.. processes]);
            await File.WriteAllTextAsync(Path.Combine(output, scenario + ".json"),
                JsonSerializer.Serialize(record, ResponsivenessJsonContext.Default.ResponsivenessRecord), token);
            if (!quick) failures.AddRange(CheckBudgets(record));
        }
        if (failures.Count > 0)
            throw new InvalidOperationException("Reference budgets exceeded; retained raw measurements:\n" + string.Join('\n', failures));
        return true;
    }

    private static IEnumerable<string> CheckBudgets(ResponsivenessRecord record)
    {
        using var budgets = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "tests", "responsiveness-baselines.json")));
        foreach (var target in budgets.RootElement.GetProperty("targetsMilliseconds").EnumerateObject())
        {
            if (!record.Metrics.TryGetValue(target.Name, out var measured)) continue;
            foreach (var percentile in target.Value.EnumerateObject())
            {
                var actual = percentile.Name == "p99" ? measured.P99 : measured.P95;
                var maximum = percentile.Value.GetDouble();
                if (actual > maximum)
                    yield return $"{record.Scenario}: {target.Name} {percentile.Name} {actual:F2} ms exceeds {maximum:F2} ms";
            }
        }
    }

    private static async Task<byte[]> RetainedSessionAsync(string frontend, string cache, int submissions, int definitions,
        CancellationToken cancellationToken)
    {
        var distribution = Path.Combine(Path.GetDirectoryName(frontend)!, "host");
        var host = Path.Combine(distribution, "ilrepl-host.dll");
        var hashes = new List<string>();
        foreach (var assembly in new[] { "ilrepl-host.dll", "IlRepl.Engine.dll", "IlRepl.Protocol.dll" })
            hashes.Add(SessionCodec.Hash(await File.ReadAllBytesAsync(Path.Combine(distribution, assembly), cancellationToken)));
        var package = SessionCodec.Hash(Encoding.UTF8.GetBytes(string.Join(':', hashes)));
        var path = Path.Combine(cache, ResponsivenessFixtures.Version, $"session-{submissions}-{definitions}-{package}.ilrepl.json");
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path, cancellationToken);
        await using var engine = await HostProcessEngine.StartAsync(host, RepoPaths.Root, cancellationToken);
        for (var index = 0; index < submissions; index++)
        {
            string[] source = index < definitions
                ? [$".method int32 Retained{index}() {{", "ldc.i4.1", "ret", "}"] : ["ldc.i4.1", "ret"];
            foreach (var line in source)
            {
                var reply = await engine.HandleAsync(line, cancellationToken);
                if (!reply.Succeeded)
                    throw new InvalidOperationException(string.Join('\n', reply.Lines.Select(item => item.PlainText)));
            }
            if ((index + 1) % 1000 == 0) Console.WriteLine($"Prepared {index + 1} of {submissions} retained submissions.");
        }
        var snapshot = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Capture },
        }, cancellationToken);
        var document = snapshot.Document ?? throw new InvalidOperationException("The fixture host did not return its source.");
        if (document.Cells.Length != submissions)
            throw new InvalidOperationException("The fixture host did not retain every actual submission.");
        var bytes = SessionCodec.Write(document);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        return bytes;
    }

    private static string CombinedDraft()
    {
        var lines = ResponsivenessFixtures.Draft(2000).Split('\n');
        lines[2] = ResponsivenessFixtures.Generic(64);
        lines[3] = "pop";
        return string.Join('\n', lines);
    }

    private static async Task<string> DescribeAsync(string executable, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = RepoPaths.Root };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var result = await ToolProcess.RunAsync(start, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
        return result.StandardOutput.Trim();
    }

    private static async Task<string> CpuAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            var lines = await File.ReadAllLinesAsync("/proc/cpuinfo", cancellationToken);
            return lines.FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal))
                ?? RuntimeInformation.ProcessArchitecture.ToString();
        }
        if (OperatingSystem.IsMacOS()) return await DescribeAsync("sysctl", ["-n", "machdep.cpu.brand_string"], cancellationToken);
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString();
    }
}
