using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Arms explicit runtime replacement only after a notice for the current operation has been rendered.
/// </summary>
internal sealed class InterruptState
{
    private readonly Lock _lock = new();
    private ExecutionProgress _progress = new("", 0, ExecutionPhase.Cooperative, false);
    private string? _requestedIdentity;
    private TimeSpan _requestedAt;
    private long _builtRevision = -1;
    private long _renderedRevision = -1;
    private bool _settled;

    /// <summary>
    /// Applies a phase change while retracting notices belonging to an earlier phase or operation.
    /// </summary>
    internal void Update(ExecutionProgress progress, TimeSpan now)
    {
        lock (_lock)
        {
            if (progress.Identity == _progress.Identity && progress.Sequence <= _progress.Sequence)
            {
                return;
            }

            if (_requestedIdentity == _progress.Identity && _progress.IsRunning
                && (!progress.IsRunning || progress.Identity != _progress.Identity))
            {
                _settled = true;
            }

            if (_requestedIdentity == progress.Identity && progress.Phase != _progress.Phase)
            {
                _requestedAt = now;
            }

            _progress = progress;
            _builtRevision = _renderedRevision = -1;
            if (!progress.IsRunning && _requestedIdentity == progress.Identity)
            {
                _settled = true;
            }
        }
    }

    /// <summary>
    /// Classifies a press against the current operation and the last acknowledged rendered notice.
    /// </summary>
    internal InterruptAction Press(TimeSpan now, out ExecutionProgress progress)
    {
        lock (_lock)
        {
            progress = _progress;
            if (!_progress.IsRunning)
            {
                return _settled ? InterruptAction.Consume : InterruptAction.None;
            }

            if (_requestedIdentity == _progress.Identity)
            {
                return _renderedRevision == _progress.Sequence ? InterruptAction.Restart : InterruptAction.Consume;
            }

            _requestedIdentity = _progress.Identity;
            _requestedAt = now;
            _settled = false;
            return InterruptAction.Cancel;
        }
    }

    /// <summary>
    /// Captures the notice included in the next frame without authorizing replacement before that frame is flushed.
    /// </summary>
    internal string? BuildNotice(TimeSpan now)
    {
        lock (_lock)
        {
            if (!_progress.IsRunning || _requestedIdentity != _progress.Identity)
            {
                return null;
            }

            var grace = _progress.Phase switch
            {
                ExecutionPhase.UserCode => TimeSpan.FromMilliseconds(250),
                ExecutionPhase.CannotStop => TimeSpan.Zero,
                _ => TimeSpan.FromSeconds(5),
            };

            if (now - _requestedAt >= grace)
            {
                _builtRevision = _progress.Sequence;
                return "Press Ctrl+C again to restart the runtime; objects and static values will be lost";
            }

            return _progress.Phase == ExecutionPhase.UserCode ? "Interrupt requested"
                : "Cancelling " + _progress.Name;
        }
    }

    /// <summary>
    /// Acknowledges the actual output flush for a notice whose phase has not changed while drawing.
    /// </summary>
    internal void FrameFlushed(string identity, long revision)
    {
        lock (_lock)
        {
            if (_progress.IsRunning && identity == _progress.Identity && revision == _progress.Sequence)
            {
                _renderedRevision = revision;
            }
        }
    }

    /// <summary>
    /// Tags the rendered frame with the exact operation and phase whose notice it contains.
    /// </summary>
    internal string? TakeFrameMarker()
    {
        lock (_lock)
        {
            if (_builtRevision != _progress.Sequence || !_progress.IsRunning)
            {
                return null;
            }

            _builtRevision = -1;
            return "\u001b]7777;ilrepl-interrupt:" + _progress.Identity + ":" + _progress.Sequence + "\u0007";
        }
    }

    /// <summary>
    /// Releases the repeated-key guard after a subsequent editor action.
    /// </summary>
    internal void Edited()
    {
        lock (_lock)
        {
            _settled = false;
        }
    }
}
