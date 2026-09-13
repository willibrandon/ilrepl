using System.Globalization;
using System.Reflection.Emit;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Captures, validates, and commits independently callable method edits.
/// </summary>
public sealed partial class Session
{
    private readonly List<MethodEdit> _edits = [];

    /// <summary>
    /// The captured edits, including drafts that have not yet been committed.
    /// </summary>
    public IReadOnlyList<MethodEdit> Edits => _edits;

    /// <summary>
    /// Captures an original and prepares a complete definition without executing user code; an existing edit name reopens its draft.
    /// </summary>
    /// <param name="reference">A method reference or existing edit name.</param>
    /// <param name="name">An explicit new copy name, or null to generate an available name.</param>
    /// <returns>The prepared edit.</returns>
    public MethodEdit PrepareEdit(string reference, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (OpenMethod is not null || OpenType is not null)
        {
            throw new ReplException("close or abandon the open declaration before editing another method");
        }

        if (name is null && _edits.FirstOrDefault(edit => edit.Name == reference.Trim()) is { } existing)
        {
            return existing;
        }

        var resolved = MemberResolver.ResolveMethod(reference, InspectionContext, wantConstructor: false);
        var method = resolved.Method ?? _methods.First(m => m.Signature.Name == resolved.Definition!.Name).Version.Body;
        if (resolved.Declared is not null || method.DeclaringType is TypeBuilder)
        {
            throw new ReplException("the method belongs to an uncommitted declaration; close it first");
        }

        var chosen = name ?? new string(method.Name.Select(character => char.IsLetterOrDigit(character) || character == '_' ? character
            : '_').ToArray()) + "_Edit";
        if (chosen.Length == 0 || !(char.IsLetter(chosen[0]) || chosen[0] == '_') || chosen.Any(character =>
            !(char.IsLetterOrDigit(character) || character == '_')))
        {
            throw new ReplException(
                "an edit name must start with a letter or underscore and contain only letters, digits, and underscores");
        }

        bool Used(string candidate) => _edits.Any(edit => edit.Name == candidate) || _methods.Any(m => m.Signature.Name == candidate)
            || _typeTable.Entries.Any(entry => entry.FullName == candidate || entry.FullName.StartsWith("IlRepl.Edits." + candidate
                + ".", StringComparison.Ordinal));
        if (name is not null && Used(chosen))
        {
            throw new ReplException($"'{chosen}' already belongs to a session definition; reopen an edit by its name");
        }

        var stem = chosen;
        for (var suffix = 2; Used(chosen); suffix++)
        {
            chosen = stem + suffix.ToString(CultureInfo.InvariantCulture);
        }

        var family = ImportedMethodFamily.Capture(chosen, method, this);
        try
        {
            if (family.Problems.Count == 0)
            {
                family.Compile();
            }
        }
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            family.Reject(exception.Message);
        }

        var edit = new MethodEdit(chosen, reference, family);
        _edits.Add(edit);
        CompletionRevision++;
        return edit;
    }

    /// <summary>
    /// Validates and publishes one revision atomically, preserving the last successful copy on failure.
    /// </summary>
    /// <param name="name">The prepared edit name.</param>
    /// <param name="source">One complete method definition, without the surrounding .edit command.</param>
    /// <returns>The edit with its newly committed revision.</returns>
    public MethodEdit CommitEdit(string name, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(source);
        var edit = _edits.FirstOrDefault(edit => edit.Name == name) ?? throw new ReplException($"no edit '{name}' in the session");
        var family = edit.Baseline.Revise(source);
        try
        {
            family.Compile();
            var table = _typeTable.Clone();
            if (edit.Current is { } previous)
            {
                foreach (var type in previous.RuntimeTypes)
                {
                    table.Remove(type.FullName!.Replace('+', '/'));
                }
            }

            foreach (var type in family.RuntimeTypes)
            {
                table.Add(type.FullName!.Replace('+', '/'), type);
            }

            table.MethodAliases[name] = family.CallableEntryPoint!;
            var released = edit.Current?.Definition;
            PublishEditBindings(edit, family, table);
            edit.Current = family;
            edit.Source = source;
            edit.Revision++;
            Submissions++;
            Generation++;
            CompletionRevision++;
            if (released is not null)
            {
                SessionAssemblies.Release(released);
            }

            return edit;
        }
        catch
        {
            if (family.Definition is { } definition)
            {
                SessionAssemblies.Release(definition);
            }

            throw;
        }
    }
}
