namespace IlRepl.Tests;

/// <summary>
/// Distinguishes an assembler rejection from infrastructure failure and retains the assembled image on success.
/// </summary>
/// <param name="Tool">The assembler's actual status and diagnostics.</param>
/// <param name="Image">The image written by a successful assembler, otherwise an empty array.</param>
internal sealed record IlasmResult(ToolResult Tool, byte[] Image);
