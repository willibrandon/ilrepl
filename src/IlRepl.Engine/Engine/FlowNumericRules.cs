namespace IlRepl.Engine;

/// <summary>
/// Applies the CLI operand categories in ECMA-335 III.1.5, independently of verification refinements.
/// </summary>
internal static class FlowNumericRules
{
    /// <summary>
    /// Tests the operand table for a binary numeric, comparison, or shift instruction.
    /// </summary>
    public static bool Binary(string op, StackCategory left, StackCategory right)
    {
        var integerLeft = Integer(left);
        var integerRight = Integer(right);
        var sameNumeric = left == right && (integerLeft || left == StackCategory.Float);
        var nativePair = left is StackCategory.Int32 or StackCategory.NativeInt
            && right is StackCategory.Int32 or StackCategory.NativeInt;
        var equality = op is "ceq" or "beq" or "beq.s" or "bne.un" or "bne.un.s";
        if (Comparison(op))
        {
            return sameNumeric || nativePair || left == StackCategory.ByRef && right == StackCategory.ByRef
                || left == StackCategory.ObjectReference && right == StackCategory.ObjectReference && (equality || op == "cgt.un")
                || equality && (left == StackCategory.ByRef && right == StackCategory.NativeInt
                    || left == StackCategory.NativeInt && right == StackCategory.ByRef);
        }

        if (op is "shl" or "shr" or "shr.un")
        {
            return integerLeft && right is StackCategory.Int32 or StackCategory.NativeInt;
        }

        if (left == StackCategory.ByRef || right == StackCategory.ByRef)
        {
            // ECMA-335 table III.7 keeps these unsigned overflow forms as correct but unverifiable pointer arithmetic.
            var addition = op is "add" or "add.ovf.un";
            var subtraction = op is "sub" or "sub.ovf.un";
            return left == StackCategory.ByRef && right == StackCategory.ByRef && subtraction
                || left == StackCategory.ByRef && right is StackCategory.Int32 or StackCategory.NativeInt && (addition || subtraction)
                || right == StackCategory.ByRef && left is StackCategory.Int32 or StackCategory.NativeInt && addition;
        }

        var integral = op is "and" or "or" or "xor" or "div.un" or "rem.un" || op.Contains("ovf", StringComparison.Ordinal);
        return (sameNumeric || nativePair) && (!integral || integerLeft && integerRight);
    }

    /// <summary>
    /// Identifies the binary comparison instructions and their short branch forms.
    /// </summary>
    public static bool Comparison(string op) => op is "ceq" or "cgt" or "cgt.un" or "clt" or "clt.un"
        or "beq" or "beq.s" or "bne.un" or "bne.un.s" or "bge" or "bge.s" or "bge.un" or "bge.un.s"
        or "bgt" or "bgt.s" or "bgt.un" or "bgt.un.s" or "ble" or "ble.s" or "ble.un" or "ble.un.s"
        or "blt" or "blt.s" or "blt.un" or "blt.un.s";

    /// <summary>
    /// Identifies integer stack categories, including a native integer.
    /// </summary>
    public static bool Integer(StackCategory kind) => kind is StackCategory.Int32 or StackCategory.Int64 or StackCategory.NativeInt;
}
