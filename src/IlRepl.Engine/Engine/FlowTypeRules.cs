using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Supplies identity and assignment operations to the shared control-flow rules.
/// </summary>
/// <typeparam name="T">The type representation.</typeparam>
internal sealed class FlowTypeRules<T>(
    IStackTypeAlgebra<T> algebra,
    Func<T, StackCategory> category,
    Func<T, T, bool> assignable,
    Func<T, T?> baseType,
    Func<T?, string> name,
    Func<T?, T?> boxedType,
    Func<T, FlowParameter<T>> parameter,
    Func<T, (int Rank, bool Vector)> arrayShape,
    Func<T, int, bool, T> makeArray,
    Func<T, T> underlyingType) where T : class
{
    /// <summary>
    /// The type operations used by opcode transfer.
    /// </summary>
    public IStackTypeAlgebra<T> Algebra { get; } = algebra;

    /// <summary>
    /// Classifies a concrete stack type.
    /// </summary>
    public Func<T, StackCategory> Category { get; } = category;

    /// <summary>
    /// Determines whether the first type can be assigned to the second.
    /// </summary>
    public Func<T, T, bool> Assignable { get; } = assignable;

    /// <summary>
    /// Retrieves a type's base without materializing a declaration.
    /// </summary>
    public Func<T, T?> BaseType { get; } = baseType;

    /// <summary>
    /// Formats a type for the stack column.
    /// </summary>
    public Func<T?, string> Name { get; } = name;

    /// <summary>
    /// Retrieves the type retained by a boxed stack value.
    /// </summary>
    public Func<T?, T?> BoxedType { get; } = boxedType;

    private readonly Func<T, FlowParameter<T>> _parameter = parameter;
    private readonly Func<T, (int Rank, bool Vector)> _arrayShape = arrayShape;
    private readonly Func<T, int, bool, T> _makeArray = makeArray;
    private readonly Func<T, T> _underlyingType = underlyingType;

    /// <summary>
    /// Merges stack types without treating incompatible categories as unknown.
    /// </summary>
    public bool TryMerge(T? left, T? right, out T? result)
    {
        result = left;
        if (left is null || right is null)
        {
            result = null;
            return true;
        }

        if (Algebra.Same(left, right))
        {
            return true;
        }

        if (Algebra.IsGenericParameter(left) && !IsReferenceParameter(left, [])
            || Algebra.IsGenericParameter(right) && !IsReferenceParameter(right, []))
        {
            return false;
        }

        var kind = Category(left);
        if (kind != Category(right))
        {
            return false;
        }

        result = kind switch
        {
            StackCategory.Int32 => Algebra.Primitive("int32"),
            StackCategory.Int64 => Algebra.Primitive("int64"),
            StackCategory.NativeInt => Algebra.Primitive("native int"),
            StackCategory.Float => Algebra.Primitive("float64"),
            _ => left,
        };
        if (kind != StackCategory.ObjectReference)
        {
            return kind == StackCategory.ByRef ? SameLocation(Algebra.ElementOf(left)!, Algebra.ElementOf(right)!)
                : kind != StackCategory.ValueType;
        }

        if (Algebra.IsArray(left) && Algebra.IsArray(right) && _arrayShape(left) == _arrayShape(right))
        {
            var a = Algebra.ElementOf(left)!;
            var b = Algebra.ElementOf(right)!;
            if (Category(a) == StackCategory.ObjectReference && Category(b) == StackCategory.ObjectReference
                && TryMerge(a, b, out var element) && element is not null)
            {
                var shape = _arrayShape(left);
                result = _makeArray(element, shape.Rank, shape.Vector);
                return true;
            }
        }

        if (Algebra.Same(left, Algebra.NullReference))
        {
            result = right;
        }
        else if (Algebra.Same(right, Algebra.NullReference))
        {
            result = left;
        }
        else if (Algebra.Same(left, Algebra.UnknownReference) || Algebra.Same(right, Algebra.UnknownReference))
        {
            result = Algebra.UnknownReference;
        }
        else if (CanAssign(left, right))
        {
            result = right;
        }
        else if (!CanAssign(right, left))
        {
            result = Algebra.Primitive("object");
            for (var candidate = BaseType(BoxedType(left) ?? left); candidate is not null; candidate = BaseType(candidate))
            {
                if (CanAssign(right, candidate))
                {
                    result = candidate;
                    break;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Checks assignment while preserving boxed and generic parameter identities.
    /// </summary>
    public bool CanAssign(T? actual, T declared)
    {
        if (actual is null)
        {
            return true;
        }

        if (Algebra.Same(actual, Algebra.UnknownReference))
        {
            return Category(declared) == StackCategory.ObjectReference && !Algebra.IsGenericParameter(declared);
        }

        if (Algebra.Same(actual, declared))
        {
            return true;
        }

        if (Algebra.Same(actual, Algebra.NullReference))
        {
            return Category(declared) == StackCategory.ObjectReference
                && (!Algebra.IsGenericParameter(declared) || IsReferenceParameter(declared, []));
        }

        if (BoxedType(actual) is { } boxed)
        {
            if (Algebra.IsGenericParameter(boxed))
            {
                return ParameterAssigns(boxed, declared, []);
            }

            return Category(declared) == StackCategory.ObjectReference && Assignable(boxed, declared);
        }

        if (Algebra.IsGenericParameter(declared))
        {
            return false;
        }

        if (Algebra.IsGenericParameter(actual))
        {
            return IsReferenceParameter(actual, []) && ParameterAssigns(actual, declared, []);
        }

        var expected = Category(declared);
        var found = Category(actual);
        return expected switch
        {
            StackCategory.Int32 or StackCategory.Int64 or StackCategory.Float => expected == found,
            StackCategory.NativeInt => found is StackCategory.NativeInt or StackCategory.Int32,
            StackCategory.ObjectReference => found == expected && Assignable(actual, declared),
            StackCategory.ByRef => found == expected && SameLocation(Algebra.ElementOf(actual)!, Algebra.ElementOf(declared)!),
            _ => Algebra.Same(actual, declared),
        };
    }

    /// <summary>
    /// True when a type is a zero-based, one-dimensional array.
    /// </summary>
    public bool IsVector(T type) => Algebra.IsArray(type) && _arrayShape(type) == (1, true);

    /// <summary>
    /// Applies the CLI's array-element compatibility relation, including enum and signedness reductions.
    /// </summary>
    public bool ArrayElementCompatible(T actual, T expected)
    {
        actual = _underlyingType(actual);
        expected = _underlyingType(expected);
        return Algebra.Same(actual, expected) || Assignable(actual, expected) || SameReducedType(actual, expected)
            || SameLocation(actual, expected);
    }

    /// <summary>
    /// Applies the CLI's verification-type equivalence for managed storage locations.
    /// </summary>
    /// <param name="left">The first storage type.</param>
    /// <param name="right">The second storage type.</param>
    /// <returns>True when both have the same verification type.</returns>
    public bool SameVerificationLocation(T left, T right) => SameLocation(left, right);

    /// <summary>
    /// True when a type can be used as the operand of <c>box</c>.
    /// </summary>
    /// <param name="type">The operand type.</param>
    /// <returns>True when the type is boxable.</returns>
    public bool IsBoxable(T type) => !Algebra.IsByRef(type) && !Algebra.IsPointer(type) && !Algebra.IsByRefLike(type)
        && !Algebra.Same(type, Algebra.Primitive("void")) && !Algebra.Same(type, Algebra.Primitive("typedref"));

    private bool SameReducedType(T left, T right)
    {
        bool Pair(string signed, string unsigned) => Algebra.Same(left, Algebra.Primitive(signed))
            && Algebra.Same(right, Algebra.Primitive(unsigned))
            || Algebra.Same(left, Algebra.Primitive(unsigned)) && Algebra.Same(right, Algebra.Primitive(signed));
        return Pair("int8", "uint8") || Pair("int16", "uint16") || Pair("int32", "uint32")
            || Pair("int64", "uint64") || Pair("native int", "native uint");
    }

    private bool SameLocation(T left, T right)
    {
        left = _underlyingType(left);
        right = _underlyingType(right);
        if (Algebra.Same(left, right))
        {
            return true;
        }

        // ECMA-335 I.8.7 defines these managed-pointer verification types explicitly.
        bool Group(string first, string second, string? third = null)
        {
            bool Member(T type) => Algebra.Same(type, Algebra.Primitive(first))
                || Algebra.Same(type, Algebra.Primitive(second))
                || third is not null && Algebra.Same(type, Algebra.Primitive(third));
            return Member(left) && Member(right);
        }

        return Group("bool", "int8", "uint8") || Group("char", "int16", "uint16")
            || Group("int32", "uint32") || Group("int64", "uint64") || Group("native int", "native uint");
    }

    private bool IsReferenceParameter(T type, List<T> visited)
    {
        if (visited.Any(previous => Algebra.Same(previous, type)))
        {
            return false;
        }

        visited.Add(type);
        var guarantees = _parameter(type);
        return guarantees.IsReference || guarantees.Constraints.Any(constraint => Algebra.IsGenericParameter(constraint)
            && IsReferenceParameter(constraint, visited));
    }

    private bool ParameterAssigns(T type, T declared, List<T> visited)
    {
        if (visited.Any(previous => Algebra.Same(previous, type)))
        {
            return false;
        }

        visited.Add(type);
        var guarantees = _parameter(type);
        return Algebra.Same(declared, Algebra.Primitive("object"))
            || guarantees.IsValueType && Algebra.Same(declared, Algebra.CoreLib("System.ValueType"))
            || guarantees.Constraints.Any(constraint => Algebra.Same(constraint, declared)
                || (Algebra.IsGenericParameter(constraint) ? ParameterAssigns(constraint, declared, visited)
                    : Assignable(constraint, declared)));
    }

    /// <summary>
    /// Renders the values of an established path.
    /// </summary>
    public string Render(FlowState<T>? state) => state is null ? "unreachable"
        : state.Invalid ? "invalid" : state.HasUnknownPath || state.Values is null ? "?"
        : "[" + string.Join(", ", state.Values.Select(value => Name(value.Type))) + "]";
}
