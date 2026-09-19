using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Tracks metadata returned or written through callback outputs before a callable value reaches external code.
/// </summary>
internal sealed partial class ReflectionValueResolver
{
    private bool CopiedCallbackMetadata(MethodBase method, Func<object, bool> isCopiedMember)
    {
        if (CopiedMethodReturn(method, isCopiedMember))
        {
            return true;
        }

        if (!bodies.TryGetValue(IlAsmRenderer.DefinitionOf(method), out var body) || body is null)
        {
            return false;
        }

        for (var position = 0; position < body.State.Entries.Count; position++)
        {
            var instruction = body.State.Entries[position].Instruction;
            if (instruction is null)
            {
                continue;
            }

            var op = instruction.Op;
            if (op.Name?.StartsWith("stelem", StringComparison.Ordinal) == true
                && HasExternalArrayStorage(body, position, 3)
                && HasCopiedMetadata(body, position, 1, isCopiedMember))
            {
                return true;
            }

            if (op != OpCodes.Stobj && op != OpCodes.Cpobj && op.Name?.StartsWith("stind.", StringComparison.Ordinal) != true)
            {
                continue;
            }

            var address = MetadataAddressAt(body, position, 2);
            if ((address.Unknown || address.Slots.OfType<(MethodBase Method, int Index, bool Argument)>()
                    .Any(slot => slot.Argument && slot.Method == body.Method))
                && HasCopiedMetadata(body, position, 1, isCopiedMember))
            {
                return true;
            }
        }

        return false;
    }
}
