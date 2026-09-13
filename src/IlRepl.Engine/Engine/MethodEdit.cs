using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// A captured original and the last successfully committed session-owned version of a method.
/// </summary>
public sealed class MethodEdit
{
    internal MethodEdit(string name, string reference, ImportedMethodFamily baseline)
    {
        Name = name;
        Reference = reference;
        Baseline = baseline;
        Source = baseline.Selected.Source;
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            baseline.Selected.Method.Module.ModuleVersionId + ":" + baseline.Selected.Method.MetadataToken + ":" + Source)));
    }

    /// <summary>
    /// The stable name chosen for the copy.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The original method reference used to create this edit.
    /// </summary>
    public string Reference { get; }

    /// <summary>
    /// The immutable original disassembly.
    /// </summary>
    public DisassembledMethod Original => Baseline.Selected.Listing;

    /// <summary>
    /// The captured executable with pinned helper bindings and independent declaring context.
    /// </summary>
    public MethodBase OriginalMethod => Baseline.EntryPoint
        ?? throw new ReplException("the original context cannot be reproduced: " + string.Join(Environment.NewLine, Baseline.Problems));

    /// <summary>
    /// The replayable method definition of the last successful revision, or initial draft.
    /// </summary>
    public string Source { get; internal set; }

    /// <summary>
    /// The compiled copy, or null until the first successful commit.
    /// </summary>
    public MethodBase? Method => Current?.EntryPoint;

    /// <summary>
    /// The identity of the captured original, retained across revisions.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>
    /// The number of successful commits to this copy.
    /// </summary>
    public int Revision { get; internal set; }

    /// <summary>
    /// The exact symbols reached while preparing the current revision.
    /// </summary>
    public IReadOnlyList<EditDependency> Dependencies => (Current ?? Baseline).Dependencies;

    /// <summary>
    /// The current draft's preflight blockers; an edited body can remove dependencies that caused them.
    /// </summary>
    public IReadOnlyList<string> Problems => (Current ?? Baseline).Problems;

    /// <summary>
    /// The immutable executable family captured when the edit was first prepared.
    /// </summary>
    internal ImportedMethodFamily Baseline { get; }

    /// <summary>
    /// The last successfully published family, or null before the first commit.
    /// </summary>
    internal ImportedMethodFamily? Current { get; set; }
}
