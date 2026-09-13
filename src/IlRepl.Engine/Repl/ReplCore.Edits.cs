using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Handles editable method documents and instruction comparisons.
/// </summary>
public sealed partial class ReplCore
{
    private string? _lastDisassembled;
    private OpenEditBlock? _editBlock;

    /// <summary>
    /// Captures the accepted edit prefix together with the session's leased metadata and declarations.
    /// </summary>
    /// <returns>The independent input used by completion and analysis.</returns>
    internal EditingSeed CaptureEditingSeed()
    {
        var seed = Session.CaptureEditingSeed();
        return _editBlock is { } block ? seed with
        {
            OpenLines = [".edit " + block.Name + " {", .. block.Lines],
        } : seed;
    }

    private HandleResult Edit(string argument)
    {
        var block = argument.EndsWith('{');
        var reference = (block ? argument[..^1] : argument).Trim();
        var asIndex = reference.LastIndexOf(" as ", StringComparison.Ordinal);
        var name = asIndex >= 0 ? reference[(asIndex + 4)..].Trim() : null;
        if (asIndex >= 0)
        {
            reference = reference[..asIndex].Trim();
        }

        if (reference.Length == 0)
        {
            reference = _lastDisassembled
                ?? throw new ReplException("disassemble a method first, or use .edit <method-reference> [as Name]");
        }

        var existing = name is not null ? Session.Edits.FirstOrDefault(edit => edit.Name == name) : null;
        var edit = existing is not null && block && (reference == existing.Reference || reference == existing.Name)
            ? existing : Session.PrepareEdit(reference, name);
        foreach (var problem in edit.Problems)
        {
            Note("preflight: " + problem);
        }

        if (block)
        {
            _editBlock = new OpenEditBlock(edit.Name);
            Session.EditInputChanged();
            return new HandleResult(true, false);
        }

        var source = ".edit " + edit.Reference + " as " + edit.Name + " {\n" + edit.Source + "\n}";
        Note($"editing {edit.Name}; original {edit.Reference}");
        Note("the copy owns a distinct type and assembly identity; import runs no user code");
        return new HandleResult(true, false)
        {
            EditDocument = new EditDocument("edit:" + edit.Name + ":" + edit.Fingerprint, edit.Name, source,
                edit.Reference, edit.Fingerprint, edit.Revision)
            {
                Dependencies = edit.Dependencies.ToArray(),
                Problems = edit.Problems.ToArray(),
            },
        };
    }

    private HandleResult ContinueEdit(NormalizedLine normalized)
    {
        var block = _editBlock!;
        var comment = normalized.InBlockCommentBefore;
        var depth = block.Depth;
        foreach (var segment in CilLexer.Segments(normalized.Raw, ref comment).Where(segment => segment.Kind == CilSegmentKind.Code))
        {
            for (var index = segment.Start; index < segment.End; index++)
            {
                depth += normalized.Raw[index] switch { '{' => 1, '}' => -1, _ => 0 };
            }
        }

        if (depth <= 0)
        {
            if (depth != 0 || normalized.Text != "}")
            {
                throw new ReplException("close the outer .edit block with } on its own line");
            }

            var edit = Session.CommitEdit(block.Name, string.Join('\n', block.Lines));
            _editBlock = null;
            Note($"edit {edit.Name} committed as revision {edit.Revision}");
            Note(MemberResolver.Describe(edit.Method!));
            return new HandleResult(true, false);
        }

        block.Lines.Add(normalized.Raw);
        block.Depth = depth;
        Session.EditInputChanged();
        return new HandleResult(true, false);
    }

    private HandleResult Diff(string argument)
    {
        var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var raw = parts.Contains("--raw", StringComparer.Ordinal);
        var names = parts.Where(part => part != "--raw").ToArray();
        if (names.Length > 1)
        {
            throw new ReplException("usage: .diff [Name] [--raw]");
        }

        var edit = names.Length == 0 ? (Session.Edits.Count == 0 ? null : Session.Edits[^1]) : Session.Edits.FirstOrDefault(edit =>
            edit.Name == names[0]);
        if (edit is null)
        {
            throw new ReplException("no matching edit; create one with .edit first");
        }

        var diff = MethodEditDiff.Create(edit, Session, raw);
        Note($"{edit.Name}: original → revision {edit.Revision}" + (raw ? " (raw)" : ""));
        foreach (var row in diff.Rows)
        {
            switch (row.Kind)
            {
                case "equal":
                    Listing("    " + row.Edited + "    " + row.EditedStack);
                    break;
                case "removed":
                    Listing("  - " + row.Original + "    " + row.OriginalStack);
                    break;
                case "added":
                    Listing("  + " + row.Edited + "    " + row.EditedStack);
                    break;
                default:
                    Listing("  - " + row.Original + (row.OriginalStack is { } before ? "    " + before : ""));
                    Listing("  + " + row.Edited + (row.EditedStack is { } after ? "    " + after : ""));
                    break;
            }
        }

        if (!diff.HasChanges)
        {
            Note("no instruction, stack, or metadata differences");
        }

        return new HandleResult(true, false) { Diff = diff };
    }
}
