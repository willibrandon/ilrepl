using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// A cell after compilation: the dynamic assembly, the generated type, and the method to invoke.
/// </summary>
/// <param name="Assembly">The assembly builder that holds the cell.</param>
/// <param name="CellType">The generated type.</param>
/// <param name="EntryPoint">The method to invoke. For vararg cells this is a standard-convention wrapper.</param>
/// <param name="ArgumentValues">The values passed for the cell's declared arguments.</param>
public sealed record CompiledCell(AssemblyBuilder Assembly, Type CellType, MethodInfo EntryPoint, object?[] ArgumentValues)
{
    /// <summary>
    /// Invokes the cell, binding generic parameters first when the cell declares any.
    /// </summary>
    /// <param name="typeArguments">The types bound to <c>.typeparams</c>, or null when the cell is not generic.</param>
    /// <returns>The boxed value the cell returned.</returns>
    /// <exception cref="ReplException">The cell is generic and no type arguments were bound, or the count is wrong.</exception>
    /// <exception cref="CellException">The cell threw.</exception>
    public object? Invoke(IReadOnlyList<Type>? typeArguments)
    {
        var method = EntryPoint;
        if (method.IsGenericMethodDefinition)
        {
            var expected = method.GetGenericArguments().Length;
            if (typeArguments is null || typeArguments.Count != expected)
            {
                throw new ReplException($"the cell declares {expected} type parameter(s); bind them first, e.g. .typeargs (int32)");
            }

            method = method.MakeGenericMethod([.. typeArguments]);
        }

        try
        {
            return method.Invoke(null, ArgumentValues);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidProgramException invalid)
        {
            throw new ReplException(ExplainInvalidProgram(invalid), invalid);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new CellException(ex.InnerException.Message, ex.InnerException);
        }
    }

    private static string ExplainInvalidProgram(InvalidProgramException exception)
    {
        if (exception.Message.Contains("Vararg", StringComparison.OrdinalIgnoreCase))
        {
            return "the runtime only supports the vararg calling convention on Windows; this cell cannot run here";
        }

        return "the JIT rejected the cell: " + exception.Message + " (check .show for a stack mismatch between branches)";
    }
}
