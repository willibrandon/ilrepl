using IlRepl.Engine;
using IlRepl.Protocol;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Exercises the ECMA-335 III.1.5 operand tables through raw metadata, independent verification, and the shared analyzer.
/// </summary>
[TestClass]
public sealed class ControlFlowOperandTableTests
{
    /// <summary>
    /// Supplies correctness and verifiability from the published tables, with integer, float, reference, and byref operands.
    /// </summary>
    public static IEnumerable<object[]> Cases
    {
        get
        {
            // Columns and rows: int32, int64, native int, F, O, &. Y is verifiable; U is correct but unverifiable.
            var tables = new Dictionary<string, string[]>
            {
                ["add"] = ["YNYNNU", "NYNNNN", "YNYNNU", "NNNYNN", "NNNNNN", "UNUNNN"],
                ["sub"] = ["YNYNNN", "NYNNNN", "YNYNNN", "NNNYNN", "NNNNNN", "UNUNNU"],
                ["mul"] = ["YNYNNN", "NYNNNN", "YNYNNN", "NNNYNN", "NNNNNN", "NNNNNN"],
                ["and"] = ["YNYNNN", "NYNNNN", "YNYNNN", "NNNNNN", "NNNNNN", "NNNNNN"],
                ["ceq"] = ["YNYNNN", "NYNNNN", "YNYNNU", "NNNYNN", "NNNNYN", "NNUNNY"],
                ["cgt"] = ["YNYNNN", "NYNNNN", "YNYNNN", "NNNYNN", "NNNNNN", "NNNNNY"],
                ["shl"] = ["YNYNNN", "YNYNNN", "YNYNNN", "NNNNNN", "NNNNNN", "NNNNNN"],
                ["add.ovf"] = ["YNYNNN", "NYNNNN", "YNYNNN", "NNNNNN", "NNNNNN", "NNNNNN"],
            };
            foreach (var (opcode, table) in tables)
            {
                for (var left = 0; left < 6; left++)
                {
                    for (var right = 0; right < 6; right++)
                    {
                        // Versioned ILVerification allowances are asserted separately from the portable operand table.
                        var verifierAllowance = opcode is "add" or "sub" or "mul" or "and" or "add.ovf"
                            && (left == 1 && right == 2 || left == 2 && right == 1)
                            || opcode is "ceq" or "cgt" && left == 1 && right == 0
                            || opcode == "ceq" && (left == 2 && right == 5 || left == 5 && right == 2)
                            || opcode == "cgt" && left == 4 && right == 4;
                        yield return [opcode, left, right, table[left][right], table[left][right] == 'Y' || verifierAllowance];
                    }
                }
            }
        }
    }

    /// <summary>
    /// Every operand pair is checked before only the correct fixtures are executed.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void OperandPair_AgreesWithPublishedTable(string opcode, int left, int right, char expected, bool verifierAccepts)
    {
        var session = new Session();
        var (_, image, fixture) = CecilFixture.Build((module, type) =>
        {
            var method = new MethodDefinition("M", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            method.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            type.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            Push(il, left);
            Push(il, right);
            var operation = typeof(OpCodes).GetFields().Select(field => field.GetValue(null)).OfType<OpCode>()
                .Single(op => op.Name == opcode);
            il.Emit(operation);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldc_I4, 42);
            il.Emit(OpCodes.Ret);
        }, session.Resolver);
        using var oracle = new IlVerificationOracle();
        var errors = oracle.Verify(image);
        Assert.AreEqual(verifierAccepts, errors.Count == 0, string.Join(", ", errors));
        var method = fixture.GetMethod("M")!;
        var diagnostics = StackAnalysis.Diagnostics(MethodDisassembler.Disassemble(method, session));
        Assert.AreEqual(expected != 'N', !diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error),
            string.Join("; ", diagnostics.Select(diagnostic => diagnostic.Message)));
        if (expected == 'U')
        {
            Assert.Contains(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Unverifiable, diagnostics);
        }

        if (expected != 'N')
        {
            Assert.AreEqual(42, method.Invoke(null, [1]));
        }
    }

    private static void Push(ILProcessor il, int category)
    {
        switch (category)
        {
            case 0:
                il.Emit(OpCodes.Ldc_I4_1);
                break;
            case 1:
                il.Emit(OpCodes.Ldc_I8, 1L);
                break;
            case 2:
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Conv_I);
                break;
            case 3:
                il.Emit(OpCodes.Ldc_R8, 1.0);
                break;
            case 4:
                il.Emit(OpCodes.Ldnull);
                break;
            case 5:
                il.Emit(OpCodes.Ldarga, (short)0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(category));
        }
    }
}
