using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// The <c>.class</c> half of the session: type blocks, their members, and the families they
/// commit. While a family is open, every line goes to it; a member method inside it takes its
/// body lines the way a session method does, and closing the outermost brace commits the whole
/// family at once.
/// </summary>
public sealed partial class Session
{
    private static readonly string[] AccessorWords = [".get", ".set", ".other", ".addon", ".removeon", ".fire"];
    private static int s_prototypeCounter;

    private readonly List<SessionType> _types = [];
    private TypeTable _typeTable = new();
    private OpenTypeBlock? _openType;
    private OpenMemberBlock? _openMember;
    private PendingAccessorBlock? _openAccessor;

    /// <summary>
    /// The type families defined with <c>.class</c>, in definition order.
    /// </summary>
    public IReadOnlyList<SessionType> Types => _types;

    /// <summary>
    /// The ILAsm path of the innermost type block being typed, such as <c>Outer/Inner</c>, or null.
    /// </summary>
    public string? OpenType => _openType?.Path;

    /// <summary>
    /// The number of types defined, nested ones included.
    /// </summary>
    public int TypeCount => _types.Sum(t => t.Declaration.Family.Count());

    /// <summary>
    /// The session types a line can name.
    /// </summary>
    public TypeTable TypeTable => _typeTable;

    /// <summary>
    /// Lists the members of the innermost open type block so far, for <c>.show</c>.
    /// </summary>
    /// <returns>The header text and one line per member, or an empty list when no type is open.</returns>
    public IReadOnlyList<string> DescribeOpenType()
    {
        var lines = new List<string>();
        for (var block = _openType; block is not null; block = block.Enclosing)
        {
            lines.Insert(0, DescribeHeader(block));
        }

        if (_openType is null)
        {
            return lines;
        }

        foreach (var field in _openType.Fields)
        {
            lines.Add("    .field " + field.Describe());
        }

        foreach (var method in _openType.Methods)
        {
            lines.Add("    .method " + method.Describe() + " { " + (method.Body is null ? "abstract" : method.Body.InstructionCount.ToString(CultureInfo.InvariantCulture) + " instructions") + " }");
        }

        foreach (var accessor in _openType.Accessors)
        {
            lines.Add("    ." + accessor.Word + " " + accessor.Name + " { " + string.Join(", ", accessor.Accessors.Select(a => a.Kind)) + " }");
        }

        foreach (var nested in _openType.NestedTypes)
        {
            lines.Add("    .class " + nested.KindWord + " " + nested.DisplayName + " { ... }");
        }

        return lines;
    }

    private static string DescribeHeader(OpenTypeBlock block)
    {
        var words = new List<string> { ".class", MemberAccess.VisibilityWord(block.Header.Attributes) };
        if (block.Kind == TypeKind.Interface)
        {
            words.Add("interface");
        }

        if (block.Header.Layout != TypeLayoutKind.Auto)
        {
            words.Add(block.Header.Layout.ToString().ToLowerInvariant());
        }

        if (block.Header.Attributes.HasFlag(TypeAttributes.Sealed) && block.Kind == TypeKind.Class)
        {
            words.Add("sealed");
        }

        if (block.Header.Attributes.HasFlag(TypeAttributes.Abstract) && block.Kind == TypeKind.Class)
        {
            words.Add("abstract");
        }

        var name = block.Path + (block.TypeParameters.Count == 0 ? "" : "<" + string.Join(", ", block.TypeParameters.Select(p => p.Describe())) + ">");
        words.Add(name);
        if (block.BaseType is not null && block.BaseType != typeof(object))
        {
            words.Add("extends " + TypeNameFormatter.Pretty(block.BaseType));
        }

        if (block.Interfaces.Count > 0)
        {
            words.Add("implements " + string.Join(", ", block.Interfaces.Select(TypeNameFormatter.Pretty)));
        }

        words.Add("{");
        return string.Join(" ", words);
    }

    private LineResult OpenTypeBlock(string spec, string line)
    {
        var enclosing = _openType;
        var header = TypeHeaderParser.Parse(spec, nested: enclosing is not null);
        if (enclosing is null && _types.Any(t => t.FullName == (header.Namespace.Length == 0 ? header.Name : header.Namespace + "." + header.Name)))
        {
            // A redefinition of an accepted family: allowed, validated at close (stage 5).
        }

        var module = enclosing?.Module ?? NewPrototypeModule();
        var enclosingTotal = enclosing?.GenericParameters.Length ?? 0;
        var names = header.GenericParameters.Select(p => p.Name).ToArray();
        if (enclosing is not null && names.Length < enclosingTotal && names.Length > 0)
        {
            throw new ReplException($"a nested type redeclares its enclosing type's {enclosingTotal} generic parameter(s) first, then introduces its own (ECMA I.10.7.1)");
        }

        // The arity suffix counts the parameters this type introduces; a nested type redeclares
        // the enclosing ones by position first.
        var name = header.Name;
        var introduced = Math.Max(0, names.Length - enclosingTotal);
        if (names.Length > 0 && !header.Name.Contains('`', StringComparison.Ordinal))
        {
            name = introduced == 0 ? header.Name : header.Name + "`" + introduced.ToString(CultureInfo.InvariantCulture);
        }
        else if (names.Length > 0 && enclosing is not null)
        {
            var tick = header.Name.LastIndexOf('`');
            name = introduced == 0 ? header.Name[..tick] : header.Name[..tick] + "`" + introduced.ToString(CultureInfo.InvariantCulture);
        }

        if (enclosing is not null && (enclosing.NestedTypes.Any(n => n.Name == name) || enclosing.Placeholders.TryGetValue(name, out var placeholder) && placeholder.Builder.IsCreated()))
        {
            throw new ReplException($"{enclosing.Path} already declares a nested type {name}");
        }

        TypeBuilder builder;
        if (enclosing is not null && enclosing.Placeholders.Remove(name, out var forward))
        {
            var declaredValue = header.Kind is TypeKind.Struct or TypeKind.Enum || header.BaseTypeText is not null && IsValueBase(header.BaseTypeText);
            if (forward.IsValueType != declaredValue)
            {
                throw new ReplException($"{enclosing.Path}/{name} was referenced as a {(forward.IsValueType ? "valuetype" : "class")} before its declaration but is declared as a {(declaredValue ? "struct" : "class")}; declare it first, or match the reference");
            }

            builder = forward.Builder;
        }
        else
        {
            builder = enclosing is null
                ? module.DefineType(header.Namespace.Length == 0 ? name : header.Namespace + "." + name, header.Attributes)
                : enclosing.Prototype.DefineNestedType(name, header.Attributes);
        }

        Type[] generics = names.Length > 0 ? builder.DefineGenericParameters(names) : [];
        var table = _typeTable.Clone();
        for (var outer = enclosing; outer is not null; outer = outer.Enclosing)
        {
            table.Add(outer.Path, outer.Prototype);
            table.SetMembers(outer.Prototype, outer.Members);
        }

        var path = enclosing is null ? (header.Namespace.Length == 0 ? name : header.Namespace + "." + name) : enclosing.Path + "/" + name;
        table.Add(path, builder);
        var context = new ParseContext([], [], new GenericContext(generics, []), Resolver, Signatures(), table);

        Type? baseType = null;
        var kind = header.Kind;
        if (header.BaseTypeText is not null)
        {
            baseType = TypeParser.Parse(header.BaseTypeText, context);
            if (baseType == typeof(ValueType))
            {
                kind = TypeKind.Struct;
            }
            else if (baseType == typeof(Enum))
            {
                kind = TypeKind.Enum;
            }
            else if (header.KindFromWord && kind is TypeKind.Struct or TypeKind.Enum)
            {
                throw new ReplException($"a {(kind == TypeKind.Struct ? "value" : "enum")} type extends System.{(kind == TypeKind.Struct ? "ValueType" : "Enum")}, not {TypeNameFormatter.Pretty(baseType)}");
            }
            else if (baseType.IsSealed && baseType is not TypeBuilder)
            {
                throw new ReplException($"cannot extend sealed type {TypeNameFormatter.Pretty(baseType)}");
            }
            else if (baseType.IsInterface)
            {
                throw new ReplException($"{name} cannot extend interface {TypeNameFormatter.Pretty(baseType)}; use implements");
            }
            else if (baseType.IsValueType && baseType is not TypeBuilder)
            {
                throw new ReplException($"cannot extend {TypeNameFormatter.Pretty(baseType)}; a struct extends System.ValueType and an enum extends System.Enum");
            }
        }
        else if (kind != TypeKind.Interface)
        {
            baseType = kind switch
            {
                TypeKind.Struct => typeof(ValueType),
                TypeKind.Enum => typeof(Enum),
                _ => typeof(object),
            };
        }

        var attributes = header.Attributes;
        if (kind is TypeKind.Struct or TypeKind.Enum)
        {
            attributes |= TypeAttributes.Sealed;
            if (attributes.HasFlag(TypeAttributes.Abstract))
            {
                throw new ReplException("a struct cannot be abstract");
            }
        }

        var interfaces = header.InterfaceTexts.Select(t => TypeParser.Parse(t, context)).ToList();
        foreach (var i in interfaces)
        {
            if (!i.IsInterface)
            {
                throw new ReplException($"{TypeNameFormatter.Pretty(i)} is not an interface");
            }
        }

        if (kind == TypeKind.Enum && interfaces.Count > 0)
        {
            throw new ReplException("an enum cannot implement interfaces");
        }

        var scope = new AccessScope(builder, (kind == TypeKind.Struct ? "struct " : kind == TypeKind.Interface ? "interface " : kind == TypeKind.Enum ? "enum " : "class ") + path);
        if (baseType is not null)
        {
            MemberAccess.CheckType(baseType, scope, table);
            builder.SetParent(baseType);
        }

        foreach (var i in interfaces)
        {
            MemberAccess.CheckType(i, scope, table);
            builder.AddInterfaceImplementation(i);
        }

        var parameters = new List<GenericParameterDeclaration>();
        for (var i = 0; i < names.Length; i++)
        {
            var parameterSpec = header.GenericParameters[i];
            var gp = (GenericTypeParameterBuilder)generics[i];
            var constraints = parameterSpec.ConstraintTexts.Select(t => TypeParser.Parse(t, context)).ToList();
            gp.SetGenericParameterAttributes(parameterSpec.Attributes);
            var baseConstraint = constraints.FirstOrDefault(c => !c.IsInterface && !c.IsGenericParameter);
            if (baseConstraint is not null)
            {
                gp.SetBaseTypeConstraint(baseConstraint);
            }

            var interfaceConstraints = constraints.Where(c => c.IsInterface || c.IsGenericParameter).ToArray();
            if (interfaceConstraints.Length > 0)
            {
                gp.SetInterfaceConstraints(interfaceConstraints);
            }

            parameters.Add(new GenericParameterDeclaration(parameterSpec.Name, parameterSpec.Attributes, constraints));
        }

        var block = new OpenTypeBlock
        {
            Header = header with { Attributes = attributes },
            HeaderLine = line,
            Enclosing = enclosing,
            Prototype = builder,
            Module = module,
            Kind = kind,
            BaseType = baseType,
            Interfaces = interfaces,
            GenericParameters = generics,
            TypeParameters = parameters,
            Name = name,
            BraceSeen = header.OpensBlock,
        };
        block.Members.DefineForward = signature => DefineForwardMethod(block, signature);
        block.Members.BaseType = baseType;
        block.Members.Interfaces = interfaces;
        _openType = block;
        if (enclosing is not null)
        {
            enclosing.Outermost.Lines.Add(line);
        }

        var result = new LineResult(LineOutcome.TypeStart, null, block.KindWord + " " + DisplayName(block));
        if (header.ClosesBlock)
        {
            return CloseTypeBlock();
        }

        return result;
    }

    private static bool IsValueBase(string text)
    {
        var trimmed = text.Trim();
        return trimmed.EndsWith("System.ValueType", StringComparison.Ordinal) || trimmed.EndsWith("System.Enum", StringComparison.Ordinal) || trimmed is "ValueType" or "Enum";
    }

    private static string DisplayName(OpenTypeBlock block) =>
        block.Path + (block.TypeParameters.Count == 0 ? "" : "<" + string.Join(", ", block.TypeParameters.Select(p => p.Name)) + ">");

    private static ModuleBuilder NewPrototypeModule()
    {
        var id = Interlocked.Increment(ref s_prototypeCounter);
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ilrepl.prototype.types" + id.ToString(CultureInfo.InvariantCulture)), OperatingSystem.IsBrowser() ? AssemblyBuilderAccess.Run : AssemblyBuilderAccess.RunAndCollect);
        return assembly.DefineDynamicModule("prototype");
    }

    private static MethodBase DefineForwardMethod(OpenTypeBlock block, MethodSignature signature)
    {
        var builder = DefineMethodBuilder(block, signature, out _);
        block.Members.Add(signature, builder, declared: false);
        return builder;
    }

    private static MethodBase DefineMethodBuilder(OpenTypeBlock block, MethodSignature signature, out Type[] methodGenerics)
    {
        methodGenerics = [];
        var parameterTypes = signature.ParameterTypes;
        var required = signature.Parameters.Select(p => p.RequiredModifiers.ToArray()).ToArray();
        var optional = signature.Parameters.Select(p => p.OptionalModifiers.ToArray()).ToArray();
        if (signature.Name == ".ctor")
        {
            return block.Prototype.DefineConstructor(signature.Attributes, signature.CallingConvention, parameterTypes, required, optional);
        }

        if (signature.Name == ".cctor")
        {
            return block.Prototype.DefineTypeInitializer();
        }

        var method = block.Prototype.DefineMethod(signature.Name, signature.Attributes, signature.CallingConvention);
        if (signature.TypeParameters.Count > 0)
        {
            methodGenerics = method.DefineGenericParameters([.. signature.TypeParameters.Select(p => p.Name)]);
        }

        method.SetSignature(signature.ReturnType, [.. signature.ReturnRequiredModifiers], [.. signature.ReturnOptionalModifiers], parameterTypes, required, optional);
        method.SetImplementationFlags(signature.ImplAttributes);
        return method;
    }

    private ParseContext TypeContext(OpenTypeBlock block)
    {
        var table = _typeTable.Clone();
        foreach (var (path, entry) in block.Outermost.FamilyTypes)
        {
            // A nested type that already closed is named by its path from anywhere in the family.
            table.Add(path, entry.Prototype);
            table.SetMembers(entry.Prototype, entry.Members);
        }

        for (var outer = block; outer is not null; outer = outer.Enclosing)
        {
            table.Add(outer.Path, outer.Prototype);
            table.SetMembers(outer.Prototype, outer.Members);
        }

        table.Forward = (name, valueType) => ForwardType(block, name, valueType);
        return new ParseContext([], [], new GenericContext(block.GenericParameters, []), Resolver, Signatures(), table);
    }

    private static TypeBuilder? ForwardType(OpenTypeBlock block, string name, bool valueType)
    {
        // Outer/Inner, or a bare Inner inside Outer, names a nested type that may be declared
        // later in the family; a placeholder of the referenced kind stands in until then.
        if (block.Outermost.FamilyTypes.TryGetValue(name, out var closed))
        {
            return closed.Prototype;
        }

        var slash = name.LastIndexOf('/');
        var enclosingPath = slash < 0 ? null : name[..slash];
        var nested = slash < 0 ? name : name[(slash + 1)..];
        var target = enclosingPath is null ? block : FindBlock(block, enclosingPath);
        if (target is null || !InstructionParser.IsIdentifier(nested.Replace("`", "", StringComparison.Ordinal)))
        {
            return null;
        }

        if (target.Placeholders.TryGetValue(nested, out var existing))
        {
            return existing.Builder;
        }

        var builder = target.Prototype.DefineNestedType(nested, TypeAttributes.NestedPublic | (valueType ? TypeAttributes.Sealed : TypeAttributes.Class), valueType ? typeof(ValueType) : typeof(object));
        target.Placeholders[nested] = (builder, valueType);
        return builder;
    }

    private static OpenTypeBlock? FindBlock(OpenTypeBlock innermost, string path)
    {
        for (var block = innermost; block is not null; block = block.Enclosing)
        {
            if (block.Path == path)
            {
                return block;
            }
        }

        return null;
    }

    private LineResult AddTypeLine(string line, string text)
    {
        // Each path records the line on the outermost block once it is accepted, so an undo can
        // replay the family from its header.
        if (_openMember is not null)
        {
            return AddMemberLine(line);
        }

        return _openAccessor is not null ? AddAccessorLine(line, text) : AddClassLevelLine(line, text);
    }

    private LineResult AddClassLevelLine(string line, string text)
    {
        var block = _openType!;
        if (text == "{")
        {
            if (block.BraceSeen)
            {
                throw new ReplException("unexpected '{'; the class is already open");
            }

            block.BraceSeen = true;
            block.Outermost.Lines.Add(line);
            return new LineResult(LineOutcome.Empty, null, null);
        }

        if (!block.BraceSeen)
        {
            throw new ReplException($"expected '{{' to open {block.KindWord} {block.Path}");
        }

        if (text.StartsWith('}'))
        {
            if (text.Trim().Length > 1)
            {
                throw new ReplException($"unexpected '{text[1..].Trim()}' after '}}'");
            }

            if (block.Enclosing is not null)
            {
                // A nested close is part of the family's lines; the outermost one ends them.
                block.Outermost.Lines.Add(line);
            }

            return CloseTypeBlock();
        }

        var space = text.IndexOfAny([' ', '\t', '(']);
        var directive = space < 0 ? text : text[..space];
        var rest = space < 0 ? "" : text[space..].Trim();
        LineResult result;
        switch (directive)
        {
            case ".class":
                return OpenTypeBlock(rest, line);
            case ".field":
                result = AddField(block, rest, line);
                break;
            case ".method":
                result = OpenMember(block, rest, line);
                break;
            case ".property":
            case ".event":
                result = OpenAccessor(block, directive, rest, line);
                break;
            case ".override":
                result = AddClassOverride(block, rest, line);
                break;
            case ".pack":
            case ".size":
                result = AddLayout(block, directive, rest);
                break;
            case ".custom":
                block.CustomAttributes.Add(CustomAttributeParser.Parse(rest, TypeContext(block), line));
                result = new LineResult(LineOutcome.Custom, null, "custom " + block.CustomAttributes[^1].Describe());
                break;
            case ".locals":
            case ".try":
            case ".args":
            case ".vararg":
            case ".typeparams":
            case ".typeargs":
            case ".param":
                throw new ReplException($"{directive} belongs in a method body; open one with .method inside {block.KindWord} {block.Path}");
            case ".get":
            case ".set":
            case ".other":
            case ".addon":
            case ".removeon":
            case ".fire":
                throw new ReplException($"{directive} belongs inside a .property or .event block");
            case ".maxstack":
                return new LineResult(LineOutcome.Empty, null, null);
            default:
                if (directive.StartsWith('.'))
                {
                    throw new ReplException($"unknown directive '{directive}' inside a .class block; expected .field, .method, .property, .event, .class, .override, .pack, .size, or .custom");
                }

                throw new ReplException($"instructions belong in a method body; {block.KindWord} {block.Path} is open (define a .method, or close it with }})");
        }

        block.Outermost.Lines.Add(line);
        return result;
    }

    private LineResult AddField(OpenTypeBlock block, string rest, string line)
    {
        var field = FieldDeclarationParser.Parse(rest, TypeContext(block), line);
        MemberAccess.CheckType(field.Type, block.Scope, _typeTable);
        if (block.Fields.Any(f => f.Name == field.Name))
        {
            throw new ReplException($"field {field.Name} is already declared on {block.Path}");
        }

        if (block.Kind == TypeKind.Interface && !field.IsStatic)
        {
            throw new ReplException($"interface {block.Path} cannot declare instance fields (make it static, or move it to a class)");
        }

        if (field.Offset is not null && block.Header.Layout != TypeLayoutKind.Explicit)
        {
            throw new ReplException("field offsets need 'explicit' on the .class header");
        }

        if (block.Kind == TypeKind.Enum)
        {
            if (field.Name == "value__")
            {
                if (field.IsStatic || !IsIntegral(field.Type))
                {
                    throw new ReplException("value__ must be an instance field of an integer type: .field public specialname rtspecialname int32 value__");
                }

                field = field with { Attributes = field.Attributes | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName };
            }
            else if (!field.IsLiteral || field.Type != block.Prototype)
            {
                throw new ReplException($"enum field {field.Name} must be 'public static literal valuetype {block.Path} {field.Name} = int32(N)'");
            }
        }

        var builder = block.Prototype.DefineField(field.Name, field.Type, [.. field.RequiredModifiers], [.. field.OptionalModifiers], field.Attributes);
        if (field.Offset is { } offset)
        {
            builder.SetOffset(offset);
        }

        block.Fields.Add(field);
        block.Members.Add(field, builder);
        return new LineResult(LineOutcome.Field, null, "field " + field.Describe());
    }

    private static bool IsIntegral(Type type) =>
        type == typeof(sbyte) || type == typeof(byte) || type == typeof(short) || type == typeof(ushort)
        || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
        || type == typeof(char) || type == typeof(bool) || type == typeof(nint) || type == typeof(nuint);

    private static LineResult AddLayout(OpenTypeBlock block, string directive, string rest)
    {
        if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw new ReplException($"{directive} needs a non-negative integer");
        }

        if (block.Header.Layout == TypeLayoutKind.Auto)
        {
            throw new ReplException(".pack and .size need sequential or explicit layout on the .class header");
        }

        if (directive == ".pack")
        {
            if (value is not (0 or 1 or 2 or 4 or 8 or 16 or 32 or 64 or 128))
            {
                throw new ReplException(".pack must be 0 or a power of two up to 128");
            }

            var previous = block.PackingSize;
            block.PackingSize = value;
            return new LineResult(LineOutcome.Layout, null, previous is { } p ? $"pack {value} (replaces {p})" : $"pack {value}");
        }

        var before = block.ClassSize;
        block.ClassSize = value;
        return new LineResult(LineOutcome.Layout, null, before is { } b ? $"size {value} (replaces {b})" : $"size {value}");
    }

    private LineResult AddClassOverride(OpenTypeBlock block, string rest, string line)
    {
        if (!rest.Contains(" with ", StringComparison.Ordinal))
        {
            throw new ReplException(".override T::M belongs inside the method that implements it (or use .override T::M with method ... at class level)");
        }

        var declaration = OverrideParser.ParseAtClassLevel(rest, TypeContext(block), line);
        block.Overrides.Add(declaration);
        return new LineResult(LineOutcome.Override, null, $"overrides {declaration.TargetDescription} with {declaration.BodyName}");
    }

    private LineResult OpenMember(OpenTypeBlock block, string rest, string line)
    {
        var context = TypeContext(block);
        var owner = block.Header with { Kind = block.Kind, Attributes = block.Header.Attributes };
        var signature = MethodHeaderParser.ParseMember(rest, context, owner, out var braceOpen, out var closes, out var throwaway);
        foreach (var mentioned in signature.ParameterTypes.Append(signature.ReturnType))
        {
            MemberAccess.CheckType(mentioned, block.Scope, context.Types);
        }

        var duplicate = block.Members.FindMethods(signature.Name).FirstOrDefault(m => m.Declared && SameSignature(m.Signature, signature));
        if (duplicate.Builder is not null)
        {
            throw new ReplException($"method {signature.DescribeMember()} is already declared on {block.Path}");
        }

        if (signature.Name == ".cctor" && block.Members.FindMethods(".cctor").Any(m => m.Declared))
        {
            throw new ReplException($"{block.Path} already has a .cctor");
        }

        // A forward reference with this signature already has a builder; the header claims it.
        var forward = block.Members.FindMethods(signature.Name).FirstOrDefault(m => !m.Declared && SameSignature(m.Signature, signature) && m.Signature.IsStatic == signature.IsStatic);
        MethodBase builder;
        Type[] methodGenerics = [];
        if (forward.Builder is not null)
        {
            builder = forward.Builder;
        }
        else if (throwaway.Length > 0)
        {
            // A generic method's signature must name the builder's own parameters, so the builder
            // is defined first and the header is read again with those parameters in scope.
            var generic = block.Prototype.DefineMethod(signature.Name, signature.Attributes, signature.CallingConvention);
            methodGenerics = generic.DefineGenericParameters([.. signature.TypeParameters.Select(p => p.Name)]);
            signature = MethodHeaderParser.ParseMember(rest, context, owner, out _, out _, out _, _ => methodGenerics);
            generic.SetSignature(signature.ReturnType, [.. signature.ReturnRequiredModifiers], [.. signature.ReturnOptionalModifiers], signature.ParameterTypes, [.. signature.Parameters.Select(p => p.RequiredModifiers.ToArray())], [.. signature.Parameters.Select(p => p.OptionalModifiers.ToArray())]);
            generic.SetImplementationFlags(signature.ImplAttributes);
            for (var i = 0; i < methodGenerics.Length; i++)
            {
                var gp = (GenericTypeParameterBuilder)methodGenerics[i];
                var declared = signature.TypeParameters[i];
                gp.SetGenericParameterAttributes(declared.Attributes);
                var baseConstraint = declared.Constraints.FirstOrDefault(c => !c.IsInterface && !c.IsGenericParameter);
                if (baseConstraint is not null)
                {
                    gp.SetBaseTypeConstraint(baseConstraint);
                }

                var interfaceConstraints = declared.Constraints.Where(c => c.IsInterface || c.IsGenericParameter).ToArray();
                if (interfaceConstraints.Length > 0)
                {
                    gp.SetInterfaceConstraints(interfaceConstraints);
                }
            }

            builder = generic;
        }
        else
        {
            builder = DefineMethodBuilder(block, signature, out _);
        }

        block.Members.Add(signature, builder, declared: true);
        var isAbstract = signature.Attributes.HasFlag(MethodAttributes.Abstract);
        var member = new MemberContext(block.Prototype, block.Header, signature.IsStatic ? null : block.ThisType, isAbstract, block.Path, block.KindWord);
        var state = new CellState(Resolver, new GenericContext(block.GenericParameters, methodGenerics), Signatures(), signature, braceOpen, context.Types, member);
        _openMember = new OpenMemberBlock { Signature = signature, Builder = builder, HeaderLine = line, State = state };
        var result = new LineResult(LineOutcome.MethodStart, null, "method " + signature.DescribeMember());
        if (closes)
        {
            block.Outermost.Lines.Add(line);
            var end = CloseMember(fromHeader: true);
            return new LineResult(LineOutcome.MethodEnd, null, result.Message + "; " + end.Message);
        }

        return result;
    }

    private LineResult AddMemberLine(string line)
    {
        var member = _openMember!;
        var result = member.State.Apply(line);
        if (result.Outcome == LineOutcome.MethodEnd)
        {
            _openType!.Outermost.Lines.Add(line);
            return CloseMember(fromHeader: false);
        }

        if (result.Outcome != LineOutcome.Empty)
        {
            member.BodyLines.Add(line);
            _openType!.Outermost.Lines.Add(line);
        }

        return result;
    }

    private LineResult CloseMember(bool fromHeader)
    {
        var block = _openType!;
        var member = _openMember!;
        var state = member.State;
        var signature = member.Signature;
        if (state.Member is { IsAbstract: true })
        {
            if (state.Entries.Any(e => e.Kind is EntryKind.Instruction or EntryKind.Labels or EntryKind.Block))
            {
                throw new ReplException($"abstract method {signature.Name} has no body; close it with }}");
            }
        }
        else if (!fromHeader || state.Entries.Count > 0 || signature.ReturnType == typeof(void))
        {
            state.ValidateMethodEnd();
        }
        else
        {
            throw new ReplException($"method {signature.Name} needs a body: it returns {TypeNameFormatter.Pretty(signature.ReturnType)}");
        }

        // .param and .custom lines shape the signature that is kept.
        var parameters = signature.Parameters.ToList();
        var methodAttributes = new List<CustomAttributeDeclaration>(signature.CustomAttributes);
        foreach (var entry in state.Entries)
        {
            if (entry.Kind == EntryKind.Param && entry.ParamIndex is > 0 and var index && entry.ParamHasDefault)
            {
                parameters[index - 1] = parameters[index - 1] with { DefaultValue = entry.ParamDefault, HasDefault = true, Attributes = parameters[index - 1].Attributes | ParameterAttributes.HasDefault };
            }
            else if (entry.Kind == EntryKind.Custom && entry.Custom is { } custom)
            {
                if (entry.ParamIndex is > 0 and var target)
                {
                    parameters[target - 1] = parameters[target - 1] with { CustomAttributes = [.. parameters[target - 1].CustomAttributes, custom] };
                }
                else
                {
                    methodAttributes.Add(custom);
                }
            }
        }

        var kept = signature with { Parameters = parameters, CustomAttributes = methodAttributes };
        var declaration = new MethodDeclaration(kept, [.. state.Overrides], member.HeaderLine, [.. member.BodyLines], state.Member is { IsAbstract: true } ? null : state);
        block.Methods.Add(declaration);
        block.Members.Add(kept, member.Builder, declared: true);
        _openMember = null;
        return new LineResult(LineOutcome.MethodEnd, null, $"end of method {signature.Name}");
    }

    private LineResult OpenAccessor(OpenTypeBlock block, string directive, string rest, string line)
    {
        var context = TypeContext(block);
        PendingAccessorBlock pending;
        if (directive == ".property")
        {
            var header = PropertyEventParser.ParseProperty(rest, context);
            if (block.Accessors.Any(a => a.Property?.Name == header.Name))
            {
                throw new ReplException($"property {header.Name} is already declared on {block.Path}");
            }

            pending = new PendingAccessorBlock { Property = header, HeaderLine = line, BraceSeen = header.OpensBlock };
        }
        else
        {
            var header = PropertyEventParser.ParseEvent(rest, context);
            if (block.Accessors.Any(a => a.Event?.Name == header.Name))
            {
                throw new ReplException($"event {header.Name} is already declared on {block.Path}");
            }

            pending = new PendingAccessorBlock { Event = header, HeaderLine = line, BraceSeen = header.OpensBlock };
        }

        _openAccessor = pending;
        return new LineResult(LineOutcome.Accessor, null, pending.Word + " " + pending.Name);
    }

    private LineResult AddAccessorLine(string line, string text)
    {
        var pending = _openAccessor!;
        var block = _openType!;
        if (text == "{")
        {
            if (pending.BraceSeen)
            {
                throw new ReplException($"unexpected '{{'; the {pending.Word} is already open");
            }

            pending.BraceSeen = true;
            block.Outermost.Lines.Add(line);
            return new LineResult(LineOutcome.Empty, null, null);
        }

        if (text == "}")
        {
            pending.Closed = true;
            block.Accessors.Add(pending);
            _openAccessor = null;
            block.Outermost.Lines.Add(line);
            return new LineResult(LineOutcome.Accessor, null, $"end of {pending.Word} {pending.Name}");
        }

        var space = text.IndexOfAny([' ', '\t']);
        var directive = space < 0 ? text : text[..space];
        var rest = space < 0 ? "" : text[space..].Trim();
        if (directive == ".custom")
        {
            pending.CustomAttributes.Add(CustomAttributeParser.Parse(rest, TypeContext(block), line));
            pending.Lines.Add(line);
            block.Outermost.Lines.Add(line);
            return new LineResult(LineOutcome.Custom, null, "custom " + pending.CustomAttributes[^1].Describe());
        }

        if (!AccessorWords.Contains(directive))
        {
            throw new ReplException($"only .get, .set, .other, .addon, .removeon, .fire, and .custom belong inside a {pending.Word} block; close it with }} first");
        }

        var isProperty = pending.Property is not null;
        if (isProperty && directive is ".addon" or ".removeon" or ".fire")
        {
            throw new ReplException($"{directive} belongs in an .event block");
        }

        if (!isProperty && directive is ".get" or ".set" or ".other")
        {
            throw new ReplException($"{directive} belongs in a .property block");
        }

        var accessor = PropertyEventParser.ParseAccessor(directive[1..], rest, TypeContext(block));
        if (accessor.Kind is "get" or "set" or "addon" or "removeon" or "fire" && pending.Accessors.Any(a => a.Kind == accessor.Kind))
        {
            throw new ReplException($"{pending.Word} {pending.Name} already has a {directive}");
        }

        pending.Accessors.Add(accessor);
        pending.Lines.Add(line);
        block.Outermost.Lines.Add(line);
        return new LineResult(LineOutcome.Accessor, null, $"{accessor.Kind} {accessor.Name}");
    }

    private LineResult CloseTypeBlock()
    {
        var block = _openType!;
        if (block.Placeholders.Count > 0)
        {
            var missing = block.Placeholders.Keys.First();
            throw new ReplException($"{block.KindWord} {block.Path} closes but {block.Path}/{missing} was referenced and never declared (declare it before the closing brace)");
        }

        var undeclared = block.Members.Undeclared.FirstOrDefault();
        if (undeclared is not null)
        {
            throw new ReplException($"{block.KindWord} {block.Path} closes but {undeclared.DescribeMember()} was referenced and never declared");
        }

        var declaration = BuildDeclaration(block);
        block.Outermost.FamilyTypes[block.Path] = (block.Prototype, block.Members);
        if (block.Enclosing is { } enclosing)
        {
            enclosing.NestedTypes.Add(declaration);
            _openType = enclosing;
            return new LineResult(LineOutcome.TypeEnd, null, $"end of {block.KindWord} {block.Path}");
        }

        declaration = ValidateFamily(block, declaration);
        return CommitFamily(block, declaration);
    }

    private TypeDeclaration BuildDeclaration(OpenTypeBlock block)
    {
        var properties = new List<PropertyDeclaration>();
        var events = new List<EventDeclaration>();
        foreach (var pending in block.Accessors)
        {
            MethodDeclaration Find(AccessorReference reference)
            {
                var match = block.Methods.FirstOrDefault(m => m.Name == reference.Name && m.IsStatic == reference.IsStatic
                    && TypeIdentity.Equal(m.Signature.ReturnType, reference.ReturnType)
                    && m.Signature.Parameters.Count == reference.ParameterTypes.Count
                    && m.Signature.ParameterTypes.Zip(reference.ParameterTypes).All(p => TypeIdentity.Equal(p.First, p.Second)));
                return match ?? throw new ReplException($"{pending.Word} {pending.Name} names .{reference.Kind} {reference.Name}({string.Join(", ", reference.ParameterTypes.Select(TypeNameFormatter.Pretty))}), which {block.Path} does not declare");
            }

            if (pending.Property is { } property)
            {
                var getter = pending.Accessors.FirstOrDefault(a => a.Kind == "get");
                var setter = pending.Accessors.FirstOrDefault(a => a.Kind == "set");
                var others = pending.Accessors.Where(a => a.Kind == "other").Select(Find).ToList();
                properties.Add(new PropertyDeclaration(property.Name, property.Type, property.ParameterTypes, property.IsStatic, property.Attributes,
                    getter is null ? null : Find(getter), setter is null ? null : Find(setter), others, null, false, [.. pending.CustomAttributes], pending.HeaderLine, [.. pending.Lines]));
            }
            else
            {
                var evt = pending.Event!;
                var add = pending.Accessors.FirstOrDefault(a => a.Kind == "addon") ?? throw new ReplException($"event {evt.Name} needs .addon and .removeon");
                var remove = pending.Accessors.FirstOrDefault(a => a.Kind == "removeon") ?? throw new ReplException($"event {evt.Name} needs .addon and .removeon");
                var fire = pending.Accessors.FirstOrDefault(a => a.Kind == "fire");
                events.Add(new EventDeclaration(evt.Name, evt.HandlerType, evt.Attributes, Find(add), Find(remove), fire is null ? null : Find(fire), [.. pending.CustomAttributes], pending.HeaderLine, [.. pending.Lines]));
            }
        }

        foreach (var over in block.Overrides)
        {
            var found = block.Methods.Any(m => m.Name == over.BodyName && m.IsStatic == over.BodyIsStatic
                && TypeIdentity.Equal(m.Signature.ReturnType, over.BodyReturnType)
                && m.Signature.Parameters.Count == over.BodyParameterTypes.Count
                && m.Signature.ParameterTypes.Zip(over.BodyParameterTypes).All(p => TypeIdentity.Equal(p.First, p.Second)));
            if (!found)
            {
                throw new ReplException($".override {over.TargetDescription} names {(over.BodyIsStatic ? "static" : "instance")} {TypeNameFormatter.Pretty(over.BodyReturnType)} {over.BodyName}({string.Join(", ", over.BodyParameterTypes.Select(TypeNameFormatter.Pretty))}), which {block.Path} does not declare");
            }
        }

        var declaration = new TypeDeclaration(
            block.Name,
            block.Header.Namespace,
            block.Path,
            block.Kind,
            block.Header.Attributes,
            block.Header.Layout,
            block.TypeParameters,
            block.BaseType,
            block.Interfaces,
            block.PackingSize,
            block.ClassSize,
            [.. block.Fields],
            [.. block.Methods],
            properties,
            events,
            [.. block.NestedTypes],
            [.. block.Overrides],
            [.. block.CustomAttributes],
            block.HeaderLine,
            block.Enclosing is null ? [.. block.Lines] : []);
        return declaration;
    }

    /// <summary>
    /// Finds the declaration of a session type: one of the family being closed, or an accepted one.
    /// </summary>
    private TypeDeclaration? DeclarationOf(Type type, TypeDeclaration? family, OpenTypeBlock? block)
    {
        var definition = TypeRelations.Definition(type);
        if (family is not null && block is not null)
        {
            foreach (var (path, entry) in block.FamilyTypes)
            {
                if (ReferenceEquals(entry.Prototype, definition))
                {
                    return family.Family.FirstOrDefault(d => d.FullName == path);
                }
            }
        }

        foreach (var accepted in _types)
        {
            if (accepted.DeclarationOf(definition) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private TypeDeclaration ValidateFamily(OpenTypeBlock block, TypeDeclaration declaration)
    {
        var table = _typeTable.Clone();
        foreach (var (path, entry) in block.FamilyTypes)
        {
            table.Add(path, entry.Prototype);
            table.SetMembers(entry.Prototype, entry.Members);
        }

        TypeDeclaration? Lookup(Type type) => DeclarationOf(type, declaration, block);
        TypeDeclaration Validated(TypeDeclaration member)
        {
            var prototype = block.FamilyTypes[member.FullName].Prototype;
            var implied = TypeDeclarationValidator.Validate(member, prototype, table, Lookup);
            foreach (var method in member.Methods)
            {
                method.Body?.RecheckAccess(table);
            }

            var nested = member.NestedTypes.Select(Validated).ToList();
            return member with { Overrides = [.. member.Overrides, .. implied], NestedTypes = nested };
        }

        return Validated(declaration);
    }

    private LineResult CommitFamily(OpenTypeBlock block, TypeDeclaration declaration)
    {
        // Compilation and publication arrive with the type emitter; for now the family is kept
        // as its declaration and its prototypes answer name lookups.
        var replacing = _types.FindIndex(t => t.FullName == declaration.FullName);
        var accepted = new SessionType(declaration, block.FamilyTypes.ToDictionary(p => p.Key, p => (Type)p.Value.Prototype, StringComparer.Ordinal), null);
        if (replacing < 0)
        {
            _types.Add(accepted);
        }
        else
        {
            _types[replacing] = accepted;
        }

        var table = _typeTable.Clone();
        foreach (var (path, entry) in block.FamilyTypes)
        {
            table.Add(path, entry.Prototype);
            table.SetMembers(entry.Prototype, entry.Members);
        }

        _typeTable = table;
        _openType = null;
        Rebuild();
        Submissions++;
        return new LineResult(LineOutcome.TypeEnd, null, replacing < 0 ? $"end of {block.KindWord} {block.Path}" : $"replaced {block.KindWord} {block.Path}");
    }

    private bool UndoTypeLine()
    {
        var outermost = _openType!.Outermost;
        if (outermost.Lines.Count == 0)
        {
            _openType = null;
            _openMember = null;
            _openAccessor = null;
            return true;
        }

        var header = outermost.HeaderLine;
        var lines = outermost.Lines.Take(outermost.Lines.Count - 1).ToList();
        _openType = null;
        _openMember = null;
        _openAccessor = null;
        ReplayFamily(header, lines);
        return true;
    }

    private void ReplayFamily(string headerLine, IReadOnlyList<string> lines)
    {
        var text = InstructionParser.StripComments(headerLine).Trim();
        OpenTypeBlock(text[".class".Length..], headerLine);
        foreach (var line in lines)
        {
            AddTypeLine(line, InstructionParser.StripComments(line).Trim());
        }
    }

    private void AbandonTypeFamily()
    {
        _openType = null;
        _openMember = null;
        _openAccessor = null;
    }

    private static bool IsClassDirective(string text) =>
        text.StartsWith(".class", StringComparison.Ordinal) && (text.Length == ".class".Length || !char.IsLetter(text[".class".Length]));
}
