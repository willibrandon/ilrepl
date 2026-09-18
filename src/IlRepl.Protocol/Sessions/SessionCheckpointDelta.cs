using System.Text.Json;

namespace IlRepl.Protocol;

/// <summary>
/// Creates incremental source and history snapshots without depending on retained record instances.
/// </summary>
public static class SessionCheckpointDelta
{
    /// <summary>
    /// Retains changed records and new assets while identifying the unchanged source prefix.
    /// </summary>
    /// <param name="document">The immutable current snapshot.</param>
    /// <param name="previous">The previously acknowledged immutable snapshot, or null for the first transfer.</param>
    /// <param name="publishedAssets">Content hashes already acknowledged by the receiver.</param>
    /// <returns>The incremental document and the number of source entries retained from the previous snapshot.</returns>
    public static (SessionDocument Document, int EntryPrefix) Create(SessionDocument document, SessionDocument? previous,
        IReadOnlySet<string> publishedAssets)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(publishedAssets);
        var prefix = 0;
        while (prefix < document.Entries.Length && prefix < (previous?.Entries.Length ?? 0)
            && EntryEquals(document.Entries[prefix], previous!.Entries[prefix]))
        {
            prefix++;
        }

        var cells = previous?.Cells.ToDictionary(cell => cell.Number) ?? [];
        return (document with
        {
            Entries = document.Entries[prefix..],
            Cells = [.. document.Cells.Where(cell => !cells.TryGetValue(cell.Number, out var old) || !CellEquals(cell, old))],
            Assets = [.. document.Assets.Where(asset => !publishedAssets.Contains(asset.Hash))],
        }, prefix);
    }

    /// <summary>
    /// Compares every persisted source transition field, including immutable edit inputs.
    /// </summary>
    internal static bool EntryEquals(SessionEntry left, SessionEntry right) => ReferenceEquals(left, right)
        || (left.Identity == right.Identity && left.Number == right.Number && left.Kind == right.Kind
            && left.Reference == right.Reference && left.Mark == right.Mark && left.Source.SequenceEqual(right.Source)
            && EditEquals(left.Edit, right.Edit) && FieldsEqual(left.Extensions, right.Extensions));

    /// <summary>
    /// Compares retained execution history, including styled output and additive fields.
    /// </summary>
    internal static bool CellEquals(SessionCell left, SessionCell right) => ReferenceEquals(left, right)
        || (left.Identity == right.Identity && left.Number == right.Number && left.Kind == right.Kind && left.State == right.State
            && left.Source.SequenceEqual(right.Source) && left.Inputs.SequenceEqual(right.Inputs)
            && left.Output.Length == right.Output.Length && left.Output.Zip(right.Output).All(pair =>
                ReferenceEquals(pair.First, pair.Second)
                || (pair.First.Kind == pair.Second.Kind && pair.First.Spans.SequenceEqual(pair.Second.Spans)))
            && FieldsEqual(left.Extensions, right.Extensions));

    private static bool EditEquals(SessionEditSnapshot? left, SessionEditSnapshot? right) => ReferenceEquals(left, right)
        || (left is not null && right is not null && left.Name == right.Name && left.Reference == right.Reference
            && left.Fingerprint == right.Fingerprint && left.OpensBlock == right.OpensBlock
            && left.BaselineReference == right.BaselineReference && left.Source.SequenceEqual(right.Source)
            && MethodEquals(left.Original, right.Original)
            && DictionaryEquals(left.PinnedMethods, right.PinnedMethods, MethodEquals)
            && DictionaryEquals(left.MethodAliases, right.MethodAliases, MethodEquals)
            && DictionaryEquals(left.SignatureHeaders, right.SignatureHeaders, static (first, second) => first == second)
            && DictionaryEquals(left.TypeAliases, right.TypeAliases, static (first, second) => first == second)
            && FieldsEqual(left.Extensions, right.Extensions));

    private static bool MethodEquals(SessionMethodIdentity? left, SessionMethodIdentity? right) => ReferenceEquals(left, right)
        || (left is not null && right is not null && left.Assembly == right.Assembly && left.Module == right.Module
            && left.Token == right.Token && left.TypeArguments.SequenceEqual(right.TypeArguments)
            && left.MethodArguments.SequenceEqual(right.MethodArguments) && FieldsEqual(left.Extensions, right.Extensions));

    private static bool FieldsEqual(Dictionary<string, JsonElement>? left, Dictionary<string, JsonElement>? right) =>
        DictionaryEquals(left, right, JsonElement.DeepEquals);

    private static bool DictionaryEquals<T>(Dictionary<string, T>? left, Dictionary<string, T>? right, Func<T, T, bool> equals) =>
        ReferenceEquals(left, right) || (left is not null && right is not null && left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var value) && equals(pair.Value, value)));
}
