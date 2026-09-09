namespace IlRepl.Engine.Binding;

/// <summary>
/// Represents parsed type syntax and source positions before name binding.
/// </summary>
/// <remarks>
/// A type as written in IL, with the position of every part in the text it was read from. Nothing
/// is looked up: a named type keeps its hint, its path, and its arguments as syntax, and the
/// binder decides what they mean.
/// </remarks>
public sealed record TypeSyntax
{
    /// <summary>
    /// The shape.
    /// </summary>
    public required TypeSyntaxKind Kind { get; init; }

    /// <summary>
    /// The index of the first character of the type, its <c>class</c> or <c>valuetype</c> word included.
    /// </summary>
    public int Start { get; init; }

    /// <summary>
    /// The index after the last character of the type.
    /// </summary>
    public int End { get; init; }

    /// <summary>
    /// The canonical primitive keyword, or <c>decimal</c> for the accepted C# alias that is not a CIL primitive.
    /// </summary>
    public string? Keyword { get; init; }

    /// <summary>
    /// The assembly named in square brackets before a named type, or null.
    /// </summary>
    public string? AssemblyHint { get; init; }

    /// <summary>
    /// The index of the <c>[</c> of the assembly hint, or -1.
    /// </summary>
    public int HintStart { get; init; } = -1;

    /// <summary>
    /// The index after the <c>]</c> of the assembly hint, or -1.
    /// </summary>
    public int HintEnd { get; init; } = -1;

    /// <summary>
    /// Stores the decoded ILAsm type path while retaining any written generic arity.
    /// </summary>
    /// <remarks>
    /// The decoded path of a named type as ILAsm writes it: <c>System.String</c>, <c>Outer/Inner</c>,
    /// <c>List`1</c>. A quoted segment is decoded; the arity suffix is kept when written.
    /// </remarks>
    public string? Name { get; init; }

    /// <summary>
    /// The index of the first character of the name, after the hint.
    /// </summary>
    public int NameStart { get; init; } = -1;

    /// <summary>
    /// The index after the last character of the name, before any argument list.
    /// </summary>
    public int NameEnd { get; init; } = -1;

    /// <summary>
    /// True when the type was written with the <c>valuetype</c> word.
    /// </summary>
    public bool ValueTypeKeyword { get; init; }

    /// <summary>
    /// True when the type was written with the <c>class</c> word.
    /// </summary>
    public bool ClassKeyword { get; init; }

    /// <summary>
    /// The generic arguments of a named type, empty when none were written.
    /// </summary>
    public IReadOnlyList<TypeSyntax> Arguments { get; init; } = [];

    /// <summary>
    /// True when an argument list <c>&lt;...&gt;</c> follows the name.
    /// </summary>
    public bool HasArguments => Arguments.Count > 0;

    /// <summary>
    /// The text after <c>!</c> or <c>!!</c>: an index or a parameter name.
    /// </summary>
    public string? Reference { get; init; }

    /// <summary>
    /// The element of an array, byref, pointer, modified, or pinned type.
    /// </summary>
    public TypeSyntax? Element { get; init; }

    /// <summary>
    /// The rank of an array; 1 for a vector.
    /// </summary>
    public int Rank { get; init; }

    /// <summary>
    /// True for a vector, <c>T[]</c>, which is a different type from the rank-1 array <c>T[0...]</c>.
    /// </summary>
    public bool IsVector { get; init; }

    /// <summary>
    /// The text between the brackets of an array suffix, spaces removed.
    /// </summary>
    public string? Shape { get; init; }

    /// <summary>
    /// The signature of a function pointer.
    /// </summary>
    public SignatureSyntax? FunctionPointer { get; init; }

    /// <summary>
    /// The modifier type of a modified type.
    /// </summary>
    public TypeSyntax? Modifier { get; init; }

    /// <summary>
    /// True for <c>modreq</c>, false for <c>modopt</c>.
    /// </summary>
    public bool IsRequired { get; init; }

    /// <summary>
    /// Returns the type beneath custom modifiers and pinned annotations.
    /// </summary>
    /// <remarks>
    /// The core type under every modifier and pinned wrapper: what the type is once the
    /// annotations the CLI keeps beside it are set apart.
    /// </remarks>
    public TypeSyntax Unwrapped => Kind is TypeSyntaxKind.Modified or TypeSyntaxKind.Pinned ? Element!.Unwrapped : this;

    /// <summary>
    /// True when the type or a wrapper around it was written <c>pinned</c>.
    /// </summary>
    public bool IsPinned => Kind == TypeSyntaxKind.Pinned || (Kind == TypeSyntaxKind.Modified && Element!.IsPinned);

    /// <summary>
    /// The custom modifiers written after the type, outermost last: the order the CLI reads them.
    /// </summary>
    /// <param name="required">True for the <c>modreq</c> modifiers, false for <c>modopt</c>.</param>
    /// <returns>The modifier types in the order written.</returns>
    public IReadOnlyList<TypeSyntax> Modifiers(bool required)
    {
        var found = new List<TypeSyntax>();
        for (var current = this; current.Kind is TypeSyntaxKind.Modified or TypeSyntaxKind.Pinned; current = current.Element!)
        {
            if (current.Kind == TypeSyntaxKind.Modified && current.IsRequired == required)
            {
                found.Add(current.Modifier!);
            }
        }

        found.Reverse();
        return found;
    }
}
