using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// A <c>.class</c> block being typed: its header, the prototype builder the stack model sees,
/// the members so far, and, for the outermost block, every line typed since the header so the
/// family can be replayed after an undo. Nothing here is visible to cells until the family commits.
/// </summary>
internal sealed class OpenTypeBlock
{
    /// <summary>
    /// The parsed header.
    /// </summary>
    public required TypeHeader Header { get; init; }

    /// <summary>
    /// The header line as typed.
    /// </summary>
    public required string HeaderLine { get; init; }

    /// <summary>
    /// The enclosing block, or null for the outermost.
    /// </summary>
    public required OpenTypeBlock? Enclosing { get; init; }

    /// <summary>
    /// The prototype builder, never created.
    /// </summary>
    public required TypeBuilder Prototype { get; init; }

    /// <summary>
    /// The prototype module the family's builders live in.
    /// </summary>
    public required ModuleBuilder Module { get; init; }

    /// <summary>
    /// The kind, decided by the header words or the base type.
    /// </summary>
    public required TypeKind Kind { get; init; }

    /// <summary>
    /// The base type, or null for an interface.
    /// </summary>
    public required Type? BaseType { get; init; }

    /// <summary>
    /// The interfaces implemented.
    /// </summary>
    public required IReadOnlyList<Type> Interfaces { get; init; }

    /// <summary>
    /// The generic parameter builders.
    /// </summary>
    public required Type[] GenericParameters { get; init; }

    /// <summary>
    /// The generic parameter declarations with their resolved constraints.
    /// </summary>
    public required IReadOnlyList<GenericParameterDeclaration> TypeParameters { get; init; }

    /// <summary>
    /// The metadata name, with the arity suffix for the parameters this type introduces.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The members declared so far, for resolution.
    /// </summary>
    public OwnMembers Members { get; } = new();

    /// <summary>
    /// The <c>.pack</c> value.
    /// </summary>
    public int? PackingSize { get; set; }

    /// <summary>
    /// The <c>.size</c> value.
    /// </summary>
    public int? ClassSize { get; set; }

    /// <summary>
    /// True once the opening brace was seen.
    /// </summary>
    public bool BraceSeen { get; set; }

    /// <summary>
    /// The fields so far.
    /// </summary>
    public List<FieldDeclaration> Fields { get; } = [];

    /// <summary>
    /// The methods so far, added when their blocks close.
    /// </summary>
    public List<MethodDeclaration> Methods { get; } = [];

    /// <summary>
    /// The property and event blocks so far, resolved to their accessors when the type closes.
    /// </summary>
    public List<PendingAccessorBlock> Accessors { get; } = [];

    /// <summary>
    /// The nested types so far.
    /// </summary>
    public List<TypeDeclaration> NestedTypes { get; } = [];

    /// <summary>
    /// The class-level overrides so far.
    /// </summary>
    public List<ClassOverrideDeclaration> Overrides { get; } = [];

    /// <summary>
    /// The custom attributes so far.
    /// </summary>
    public List<CustomAttributeDeclaration> CustomAttributes { get; } = [];

    /// <summary>
    /// Placeholders for nested types referenced before their declaration, by name.
    /// </summary>
    public Dictionary<string, (TypeBuilder Builder, bool IsValueType)> Placeholders { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The lines typed since the header, on the outermost block only.
    /// </summary>
    public List<string> Lines { get; } = [];

    /// <summary>
    /// The outermost block of the family.
    /// </summary>
    public OpenTypeBlock Outermost => Enclosing?.Outermost ?? this;

    /// <summary>
    /// The ILAsm path: <c>Geometry.Point</c>, <c>Outer/Inner</c>.
    /// </summary>
    public string Path => Enclosing is null
        ? (Header.Namespace.Length == 0 ? Name : Header.Namespace + "." + Name)
        : Enclosing.Path + "/" + Name;

    /// <summary>
    /// The word a note uses for the kind.
    /// </summary>
    public string KindWord => Kind switch
    {
        TypeKind.Struct => "struct",
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        _ => "class",
    };

    /// <summary>
    /// The type of <c>this</c> for an instance member: a reference for a struct.
    /// </summary>
    public Type ThisType => Kind is TypeKind.Struct or TypeKind.Enum ? Prototype.MakeByRefType() : Prototype;

    /// <summary>
    /// On the outermost block, every type of the family that has closed, by path, with its
    /// prototype and members, so the family can be validated and published as one.
    /// </summary>
    public Dictionary<string, (System.Reflection.Emit.TypeBuilder Prototype, OwnMembers Members)> FamilyTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The index of the field a class-level <c>.custom</c> attaches to, as in ILAsm where an
    /// attribute follows the field it describes, or -1 when the next one attaches to the type.
    /// </summary>
    public int AttributeField { get; set; } = -1;

    /// <summary>
    /// The access scope of declarations in this type.
    /// </summary>
    public AccessScope Scope => new(Prototype, KindWord + " " + Path);
}

/// <summary>
/// A <c>.property</c> or <c>.event</c> block, kept until the type closes so its accessors can
/// name methods declared later.
/// </summary>
internal sealed class PendingAccessorBlock
{
    /// <summary>
    /// The property header, or null for an event.
    /// </summary>
    public PropertyHeader? Property { get; init; }

    /// <summary>
    /// The event header, or null for a property.
    /// </summary>
    public EventHeader? Event { get; init; }

    /// <summary>
    /// The header line as typed.
    /// </summary>
    public required string HeaderLine { get; init; }

    /// <summary>
    /// The accessor lines so far.
    /// </summary>
    public List<AccessorReference> Accessors { get; } = [];

    /// <summary>
    /// The custom attributes so far.
    /// </summary>
    public List<CustomAttributeDeclaration> CustomAttributes { get; } = [];

    /// <summary>
    /// The lines of the block as typed.
    /// </summary>
    public List<string> Lines { get; } = [];

    /// <summary>
    /// True once the opening brace was seen.
    /// </summary>
    public bool BraceSeen { get; set; }

    /// <summary>
    /// True once the block closed.
    /// </summary>
    public bool Closed { get; set; }

    /// <summary>
    /// The name, for messages.
    /// </summary>
    public string Name => Property?.Name ?? Event!.Name;

    /// <summary>
    /// The directive, for messages.
    /// </summary>
    public string Word => Property is not null ? "property" : "event";
}

/// <summary>
/// A member <c>.method</c> block being typed inside a type block.
/// </summary>
internal sealed class OpenMemberBlock
{
    /// <summary>
    /// The signature with its attributes.
    /// </summary>
    public required MethodSignature Signature { get; init; }

    /// <summary>
    /// The prototype builder.
    /// </summary>
    public required MethodBase Builder { get; init; }

    /// <summary>
    /// The header line as typed.
    /// </summary>
    public required string HeaderLine { get; init; }

    /// <summary>
    /// The validated body so far.
    /// </summary>
    public required CellState State { get; init; }

    /// <summary>
    /// The body lines so far.
    /// </summary>
    public List<string> BodyLines { get; } = [];
}
