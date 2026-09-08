namespace IlRepl.Tui;

/// <summary>
/// What a store held when it was read: its entries, oldest first, and how many entries the store
/// itself had written by then, so a session can tell its own writes apart at the stored end.
/// </summary>
/// <param name="Entries">The entries, oldest first.</param>
/// <param name="Written">How many entries the store had written when the entries were read.</param>
public readonly record struct HistorySnapshot(IReadOnlyList<string> Entries, int Written);
