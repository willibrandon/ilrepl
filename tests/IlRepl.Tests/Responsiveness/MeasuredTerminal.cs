using System.Diagnostics;
using System.Text;
using Hex1b;
using IlRepl.Tests.Tui;

namespace IlRepl.Tests.Responsiveness;

/// <summary>
/// Measures a packaged frontend through a real PTY and the first synchronized frame satisfying each input condition.
/// </summary>
internal sealed class MeasuredTerminal : IAsyncDisposable
{
    private readonly Hex1bTerminal _terminal;
    private readonly FrameRecorder _recorder;
    private readonly Task<int> _run;
    private readonly CancellationToken _cancellationToken;
    private readonly bool _namedSession;

    /// <summary>
    /// The timestamp immediately before launching the process, after the terminal observer has been constructed.
    /// </summary>
    public long Started { get; }

    /// <summary>
    /// The timestamp of the completed frame returned by the most recent measured input.
    /// </summary>
    public long LastPainted { get; private set; }

    /// <summary>
    /// Starts a package with independent process artifacts and optional saved-session arguments.
    /// </summary>
    public MeasuredTerminal(string frontend, string artifacts, string? session, CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _namedSession = session is not null;
        _recorder = new FrameRecorder();
        _terminal = Hex1bTerminal.CreateBuilder().WithPtyProcess(options =>
        {
            string[] arguments = session is null ? ["--no-history"] : ["--no-history", "--session", session];
            options.WorkingDirectory = RepoPaths.Root;
            if (OperatingSystem.IsWindows())
            {
                options.FileName = frontend;
                options.Arguments = arguments;
                options.Environment = new Dictionary<string, string>
                {
                    ["TERM"] = "xterm-256color", ["NO_COLOR"] = "", ["ILREPL_MEASUREMENTS_DIRECTORY"] = artifacts,
                };
            }
            else
            {
                options.FileName = "/usr/bin/env";
                options.Arguments = ["TERM=xterm-256color", "NO_COLOR=", "ILREPL_MEASUREMENTS_DIRECTORY=" + artifacts,
                    frontend, .. arguments];
            }
        }).WithHeadless().WithDimensions(120, 36).AddPresentationFilter(_recorder).Build();

        _recorder.Terminal = _terminal;
        Started = Stopwatch.GetTimestamp();
        _run = _terminal.RunAsync(cancellationToken);
    }

    /// <summary>
    /// Waits for a visible prompt with a painted editing caret.
    /// </summary>
    public Task<Frame> PromptAsync() => WaitAsync(0, frame => frame.Caret is not null && frame.Contains("il["));

    /// <summary>
    /// Waits for current painted state without accepting a matching frame from an older document.
    /// </summary>
    public Task<Frame> CurrentAsync(Func<Frame, bool> condition) => WaitAsync(Math.Max(0, _recorder.Count - 1), condition);

    /// <summary>
    /// Sends a bracketed paste and measures its first matching frame, preserving the exact source text.
    /// </summary>
    public Task<double> PasteAsync(string text, Func<Frame, bool> condition) =>
        InputAsync("\x1b[200~" + text + "\x1b[201~", condition);

    /// <summary>
    /// Sends actual terminal bytes and measures until the first matching completed frame.
    /// </summary>
    public async Task<double> InputAsync(string input, Func<Frame, bool> condition)
    {
        var first = _recorder.Count;
        var started = Stopwatch.GetTimestamp();
        var painted = WaitAsync(first, frame => frame.Timestamp >= started && condition(frame));
        await _terminal.SendInputAsync(Encoding.UTF8.GetBytes(input), _cancellationToken);
        var frame = await painted;
        LastPainted = frame.Timestamp;
        return Stopwatch.GetElapsedTime(started, frame.Timestamp).TotalMilliseconds;
    }

    /// <summary>
    /// Replaces an idle draft using the same clear and paste bindings available to a user.
    /// </summary>
    public async Task DraftAsync(string text)
    {
        var current = _recorder.Since(Math.Max(0, _recorder.Count - 1));
        if (current.Count == 0 || !IsEmptyPrompt(current[^1]))
        {
            await InputAsync("\x01\x7f", IsEmptyPrompt);
        }

        if (text.Length == 0)
        {
            return;
        }

        var tail = text.Split('\n')[^1];
        tail = tail[^Math.Min(20, tail.Length)..];
        await PasteAsync(text, frame => frame.CaretRow is { } row && row.Contains(tail, StringComparison.Ordinal));
    }

    /// <summary>
    /// Submits one command and waits for its concrete successful output before continuing the scenario.
    /// </summary>
    public async Task CommandAsync(string command, string output)
    {
        await DraftAsync(command);
        await InputAsync("\r", frame => frame.Contains(output) && !frame.Contains("sending ")
            && IsEmptyPrompt(frame));
    }

    private static bool IsEmptyPrompt(Frame frame)
    {
        var row = frame.CaretRow?.Trim();
        return row is not null && row.StartsWith("il[", StringComparison.Ordinal)
            && row.IndexOf("]>", StringComparison.Ordinal) == row.Length - 2;
    }

    private async Task<Frame> WaitAsync(int first, Func<Frame, bool> condition)
    {
        var completion = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(Frame frame)
        {
            if (frame.Index >= first && condition(frame))
            {
                completion.TrySetResult(frame);
            }
        }

        _recorder.FrameAdded += OnFrame;
        try
        {
            foreach (var frame in _recorder.Since(first))
            {
                OnFrame(frame);
            }

            var painted = completion.Task.WaitAsync(TimeSpan.FromMinutes(2), _cancellationToken);
            var completed = await Task.WhenAny(painted, _run);
            if (completed != painted)
            {
                throw new InvalidOperationException("The measured frontend exited before painting the expected frame.");
            }

            return await painted;
        }
        catch (TimeoutException exception)
        {
            var frames = _recorder.Frames;
            throw new TimeoutException("The measured terminal did not paint the expected state.\n"
                + (frames.Count == 0 ? "No frames recorded." : frames[^1].ToString()), exception);
        }
        finally
        {
            _recorder.FrameAdded -= OnFrame;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_run.IsCompleted)
            {
                await _terminal.SendInputAsync([0x11], CancellationToken.None);
                if (_namedSession)
                {
                    await CurrentAsync(frame => frame.Contains("Save changes to "));
                    await _terminal.SendInputAsync("\x1b[B\r"u8.ToArray(), CancellationToken.None);
                }

                await _run.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
            }
        }
        finally
        {
            await _terminal.DisposeAsync();
        }
    }
}
