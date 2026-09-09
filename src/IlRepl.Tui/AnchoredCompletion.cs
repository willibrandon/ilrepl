namespace IlRepl.Tui;

/// <summary>
/// Keeps a generic owner's defining text anchored to absolute document offsets across nonoverlapping edits.
/// </summary>
/// <param name="Start">The first offset in the defining span.</param>
/// <param name="End">The offset after its opening angle bracket.</param>
/// <param name="Token">The server's selected-definition token.</param>
/// <param name="Text">The exact defining text accepted by the user.</param>
internal sealed record AnchoredCompletion(int Start, int End, string Token, string Text);
