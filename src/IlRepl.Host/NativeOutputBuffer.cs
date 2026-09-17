using System.Buffers;
using System.Text;

namespace IlRepl.Host;

/// <summary>
/// Bounds worker output and separates CoreCLR startup diagnostics from explicitly invoked user output.
/// </summary>
public sealed class NativeOutputBuffer
{
    private readonly ArrayBufferWriter<byte> _startup = new();
    private readonly ArrayBufferWriter<byte> _output = new();
    private readonly byte[] _marker;
    private readonly int _limit;
    private bool _started;

    /// <summary>
    /// Creates a bounded capture with an optional private marker preceding all user code.
    /// </summary>
    /// <param name="limit">The maximum retained bytes.</param>
    /// <param name="marker">The worker's unique startup boundary, or null for unframed error output.</param>
    public NativeOutputBuffer(int limit, string? marker = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _limit = limit;
        _marker = marker is null ? [] : Encoding.UTF8.GetBytes(marker);
        _started = _marker.Length == 0;
    }

    /// <summary>
    /// Whether startup or user output exceeded the configured byte limit.
    /// </summary>
    public bool Overflowed { get; private set; }

    /// <summary>
    /// The user output, or startup diagnostics when the worker failed before reaching its startup boundary.
    /// </summary>
    public string Text => Encoding.UTF8.GetString((_started ? _output : _startup).WrittenSpan);

    /// <summary>
    /// Creates a private boundary unique to this worker's control directory.
    /// </summary>
    /// <param name="root">The worker's randomly named control directory.</param>
    /// <returns>The marker written before loading or invoking any captured code.</returns>
    public static string StartMarker(string root) => "\u001eilrepl-native:" + Path.GetFileName(root) + "\u001f";

    /// <summary>
    /// Consumes one arbitrary pipe fragment without interpreting any text written after the startup boundary.
    /// </summary>
    /// <param name="bytes">The next bytes from the worker's output pipe.</param>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (Overflowed) return;
        if (!_started)
        {
            _startup.Write(bytes);
            var index = _startup.WrittenSpan.IndexOf(_marker);
            if (index < 0)
            {
                if (_startup.WrittenCount > _limit)
                {
                    var prefix = _startup.WrittenSpan[.._limit].ToArray();
                    _startup.Clear();
                    _startup.Write(prefix);
                    Overflowed = true;
                }
                return;
            }
            _started = true;
            AppendUser(_startup.WrittenSpan[(index + _marker.Length)..]);
            _startup.Clear();
            return;
        }
        AppendUser(bytes);
    }

    private void AppendUser(ReadOnlySpan<byte> bytes)
    {
        var remaining = _limit - _output.WrittenCount;
        _output.Write(bytes[..Math.Min(bytes.Length, remaining)]);
        if (bytes.Length > remaining) Overflowed = true;
    }
}
