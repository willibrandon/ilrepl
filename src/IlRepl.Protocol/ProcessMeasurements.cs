using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace IlRepl.Protocol;

/// <summary>
/// Captures opt-in process measurements without changing ordinary startup, execution, or shutdown behavior.
/// </summary>
public sealed class ProcessMeasurements : IDisposable
{
    private readonly string? _directory;
    private readonly string _role;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, long> _stages = [];
    private int _written;

    /// <summary>
    /// The measurements of this process, for code far from the entry point that has a stage to mark.
    /// </summary>
    public static ProcessMeasurements? Current { get; private set; }

    /// <summary>
    /// Enables measurements only when the private reference-run environment variable supplies an artifact directory.
    /// </summary>
    /// <param name="role">The independently measured process role.</param>
    public ProcessMeasurements(string role)
    {
        _role = role;
        _directory = Environment.GetEnvironmentVariable("ILREPL_MEASUREMENTS_DIRECTORY");
        Current = this;
        Mark("entry");
    }

    /// <summary>
    /// Records the first occurrence of a stage using the platform's monotonic clock.
    /// </summary>
    /// <param name="stage">The startup or execution boundary.</param>
    public void Mark(string stage)
    {
        if (string.IsNullOrEmpty(_directory))
        {
            return;
        }

        lock (_lock)
        {
            _stages.TryAdd(stage, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>
    /// Writes per-process evidence after measured operations finish, with collection excluded from latency samples.
    /// </summary>
    public void Dispose()
    {
        // The host writes on its hard exit as well as on its way out of Main, and the record is written once.
        if (string.IsNullOrEmpty(_directory) || Interlocked.Exchange(ref _written, 1) != 0)
        {
            return;
        }

        Mark("exit");
        try
        {
            Directory.CreateDirectory(_directory);
            using var process = Process.GetCurrentProcess();
            IReadOnlyDictionary<string, long> stages;
            lock (_lock)
            {
                stages = new Dictionary<string, long>(_stages);
            }

            var measurement = new ProcessMeasurement(_role, Environment.ProcessId, RuntimeInformation.FrameworkDescription,
                GC.GetTotalAllocatedBytes(precise: true), GC.GetTotalMemory(forceFullCollection: true), process.WorkingSet64, stages);
            File.WriteAllText(Path.Combine(_directory, $"{_role}-{Environment.ProcessId}.json"),
                JsonSerializer.Serialize(measurement, MeasurementJsonContext.Default.ProcessMeasurement));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A measurement artifact failure must not alter the product's exit status.
        }
    }
}
