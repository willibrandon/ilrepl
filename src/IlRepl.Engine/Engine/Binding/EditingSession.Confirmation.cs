namespace IlRepl.Engine.Binding;

public sealed partial class EditingSession
{
    /// <summary>
    /// Checks a completed declaration against a disposable checkpoint without accepting it into the preview.
    /// </summary>
    /// <param name="line">The complete current line after applying its candidate edit.</param>
    /// <returns>Whether the declaration passes the same structural checks as replay.</returns>
    public bool ConfirmDeclaration(string line)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(line);
        var original = _state;
        var skipped = _skipped.ToArray();
        try
        {
            _state = original.Clone();
            _skipped.Clear();
            ApplyLine(line, _prefix.Length);
            return _skipped.Count == 0;
        }
        finally
        {
            _state = original;
            _skipped.Clear();
            _skipped.AddRange(skipped);
        }
    }
}
