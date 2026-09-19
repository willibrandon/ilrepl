using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace IlRepl.Engine;

/// <summary>
/// Rejects comparisons and identity hashes whose assembly or module operands change when a method is copied.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private const string MetadataReferenceReason = "assembly and module reference inspection cannot preserve the original identity";

    private static string? MetadataReferenceProblem(MethodBase method)
    {
        var type = method.DeclaringType;
        return type?.Assembly == typeof(Assembly).Assembly
            && (typeof(Assembly).IsAssignableFrom(type) || typeof(Module).IsAssignableFrom(type) || type == typeof(ModuleHandle))
            && method.Name is "op_Equality" or "op_Inequality" or nameof(Equals) or nameof(GetHashCode)
                ? MetadataReferenceReason : null;
    }

    private static bool IsObjectReferenceInspection(MethodBase method) => method.DeclaringType == typeof(object)
        && method.Name is nameof(ReferenceEquals) or nameof(Equals) or nameof(GetHashCode)
        || method.DeclaringType == typeof(RuntimeHelpers) && method.Name == nameof(RuntimeHelpers.GetHashCode);

    private void ValidateMetadataReference(ReflectionValueResolver values, MethodEditBody body, int position, Instruction instruction)
    {
        var op = instruction.Op;
        var comparison = op == OpCodes.Ceq || op == OpCodes.Beq || op == OpCodes.Beq_S
            || op == OpCodes.Bne_Un || op == OpCodes.Bne_Un_S;
        var target = body.Method;
        var count = 2;
        if (!comparison)
        {
            if (instruction.Operand is not ResolvedMethod resolved || op == OpCodes.Ldtoken)
            {
                return;
            }

            target = resolved.Method ?? _pinned[resolved.Definition!.Name];
            if (!IsObjectReferenceInspection(target))
            {
                return;
            }

            // An unbound function pointer can receive metadata objects after it leaves the copied family.
            if (op == OpCodes.Ldftn)
            {
                RejectReflection(body, instruction, target, "indirect reflection cannot prove a supported target");
                return;
            }

            count = op == OpCodes.Ldvirtftn ? 1 : target.GetParameters().Length + (target.IsStatic ? 0 : 1);
        }

        // Null tests depend only on presence, so they remain valid for copied metadata objects.
        if (count == 2 && Enumerable.Range(1, count).Any(index => values.Stack(body, position, index) is { Length: > 0 } candidates
            && candidates.All(candidate => candidate is null)))
        {
            return;
        }

        if (Enumerable.Range(1, count).Any(index => values.HasMetadataReference(body, position, index)))
        {
            RejectReflection(body, instruction, target, MetadataReferenceReason);
        }
    }
}
