using System.Globalization;
using System.Reflection;

namespace IlRepl.Engine.Binding;

public sealed partial class EditingSession
{
    private void OpenType(string spec, string line)
    {
        var before = _state.Clone();
        var enclosing = _state.OpenTypes.LastOrDefault();
        var header = TypeHeaderParser.Parse(spec, nested: enclosing is not null);
        var inherited = enclosing is null ? 0 : _state.Types.DeclarationOf(enclosing.Type)!.GenericParameters.Count;
        var total = header.GenericParameters.Count;
        if (total > 0 && total < inherited)
        {
            throw new ReplException($"a nested type redeclares its enclosing type's {inherited} generic parameters first");
        }

        var name = header.Name;
        if (total > 0 && enclosing is not null)
        {
            var tick = name.LastIndexOf('`');
            var introduced = total - inherited;
            if (header.ArityWritten && tick >= 0 && int.Parse(name[(tick + 1)..], CultureInfo.InvariantCulture) != introduced)
            {
                throw new ReplException($"{name}'s arity must count only the {introduced} parameters it introduces");
            }

            name = (tick < 0 ? name : name[..tick]) + (introduced == 0 ? "" : "`" + SymbolRenderer.Number(introduced));
        }

        var path = enclosing is null
            ? header.Namespace.Length == 0 ? name : header.Namespace + "." + name
            : enclosing.Path + "/" + name;
        var prepared = _predeclared?.GetValueOrDefault(path);
        var existing = _state.Types.Entries.Where(entry => entry.FullName == path)
            .Select(entry => _state.Types.DeclarationOf(entry.Type)).FirstOrDefault();
        if (enclosing is not null && existing is { IsPlaceholder: false } && prepared is null)
        {
            throw new ReplException($"{enclosing.Path} already declares a nested type {name}");
        }

        var identity = prepared?.Type.Definition
            ?? (existing is { IsPlaceholder: true } ? existing.Type.Definition : NextDefinition());
        if (enclosing is null && prepared is null)
        {
            _state.Types.RemoveFamily(path);
        }

        var type = TypeSymbol.Named(
            identity, name, header.Namespace, enclosing?.Type, "ilrepl", header.Attributes,
            header.Kind is TypeKind.Struct or TypeKind.Enum, [.. header.GenericParameters.Select(parameter => parameter.Name)]);
        GenericParameterSymbol[] parameters = [.. header.GenericParameters.Select((parameter, index) =>
            new GenericParameterSymbol(identity, false, index, parameter.Name, parameter.Attributes, []))];
        _state.Types.Add(path, type, new DeclarationSymbol(
            type, prepared?.BaseType, prepared?.Interfaces ?? [], parameters, prepared?.Fields ?? [], prepared?.Methods ?? [], true)
        {
            Properties = prepared?.Properties ?? [],
        });
        _state.Types = _state.Types.Clone([.. _state.OpenTypes.Select(block => block.Path), path]);
        var scope = Scope().WithGenerics(new SymbolGenericContext([.. parameters.Select(parameter => parameter.AsType)], []));
        TypeSymbol Bind(string text) => SymbolBinder.BindType(CilSyntaxParser.ParseType(text), scope).Type;
        var baseType = header.BaseTypeText is { } baseText ? Bind(baseText) : header.Kind switch
        {
            TypeKind.Interface => null,
            TypeKind.Struct => Bind("System.ValueType"),
            TypeKind.Enum => Bind("System.Enum"),
            _ => TypeSymbol.Object,
        };
        var kind = baseType is null ? header.Kind : SymbolRenderer.IlPath(baseType) switch
        {
            "System.ValueType" => TypeKind.Struct,
            "System.Enum" => TypeKind.Enum,
            _ => header.Kind,
        };
        if (baseType is not null && kind == TypeKind.Class
            && (baseType.Attributes.HasFlag(TypeAttributes.Sealed) || baseType.IsInterface || baseType.IsValueTypeShape
                || baseType.Kind == TypeSymbolKind.Primitive && !SymbolIdentity.Equal(baseType, TypeSymbol.Object)))
        {
            throw new ReplException($"cannot extend {SymbolRenderer.Pretty(baseType)}");
        }

        var attributes = header.Attributes;
        if (kind is TypeKind.Struct or TypeKind.Enum)
        {
            attributes |= TypeAttributes.Sealed;
            if (attributes.HasFlag(TypeAttributes.Abstract))
            {
                throw new ReplException("a struct cannot be abstract");
            }

            type = TypeSymbol.Named(identity, name, header.Namespace, enclosing?.Type, "ilrepl", attributes, true,
                [.. header.GenericParameters.Select(parameter => parameter.Name)]);
        }

        TypeSymbol[] interfaces = [.. header.InterfaceTexts.Select(Bind)];
        foreach (var implemented in interfaces)
        {
            if (!implemented.IsInterface)
            {
                throw new ReplException($"{SymbolRenderer.Pretty(implemented)} is not an interface");
            }
        }

        if (kind == TypeKind.Enum && interfaces.Length > 0)
        {
            throw new ReplException("an enum cannot implement interfaces");
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            parameters[i] = parameters[i] with { Constraints = [.. header.GenericParameters[i].ConstraintTexts.Select(Bind)] };
        }

        var declaration = new DeclarationSymbol(
            type, baseType, interfaces, parameters, prepared?.Fields ?? [], prepared?.Methods ?? existing?.Methods ?? [], true)
        {
            Properties = prepared?.Properties ?? [],
        };
        _state.Types.Add(path, type, declaration);
        var block = new EditingTypeBlock
        {
            Before = before,
            Header = header with { Attributes = attributes, Kind = kind }, HeaderLine = line, Type = type, Path = path,
            Kind = kind, BraceSeen = header.OpensBlock,
        };
        _state.OpenTypes.Add(block);
        if (header.ClosesBlock)
        {
            CloseType();
        }
    }

    private void AddTypeLine(string text)
    {
        var block = _state.OpenTypes[^1];
        if (IsDirective(text, ".custom"))
        {
            block.MetadataTypes.AddRange(CustomAttributeBinding.Parse(text[7..].Trim(), Scope()).ReferencedTypes());
            return;
        }

        if (IsDirective(text, ".property") || IsDirective(text, ".event"))
        {
            OpenAccessor(text, block);
            return;
        }

        if (IsDirective(text, ".override"))
        {
            var scope = Scope();
            block.Overrides.Add(OverrideBinding.AtClassLevel(text[9..].Trim(), scope));
            scope.CommitDeclarations();
            return;
        }

        if (text == "}")
        {
            CloseType();
            return;
        }

        if (text == "{" && !block.BraceSeen && block.Lines.Count == 0)
        {
            block.BraceSeen = true;
            return;
        }

        if (IsDirective(text, ".field"))
        {
            var field = FieldDeclarationBinding.Parse(text[6..].Trim(), Scope(), text);
            var declaration = _state.Types.DeclarationOf(block.Type)!;
            if (block.Fields.Any(existing => existing.Name == field.Name))
            {
                throw new ReplException($"field {field.Name} is already declared in {block.Path}");
            }

            if (block.Kind == TypeKind.Interface && !field.Attributes.HasFlag(FieldAttributes.Static))
            {
                throw new ReplException("an interface cannot declare instance fields");
            }

            if (field.Offset is not null && block.Header.Layout != TypeLayoutKind.Explicit)
            {
                throw new ReplException("field offsets require explicit layout");
            }

            if (field.ConstantText is { } constant)
            {
                LiteralBindingRules.Constant(constant, field.Type.Type, Scope(), "field " + field.Name);
            }

            var symbol = new FieldSymbol
            {
                Definition = declaration.Fields.FirstOrDefault(existing => existing.Name == field.Name)?.Definition ?? NextDefinition(),
                Source = MethodSymbolSource.Declared, DeclaringType = block.Type,
                Name = field.Name, FieldType = field.Type.Type, Attributes = field.Attributes,
                RequiredModifiers = field.Type.RequiredModifiers, OptionalModifiers = field.Type.OptionalModifiers,
            };
            ReplaceMembers(declaration,
                declaration.Fields.Where(existing => existing.Name != field.Name).Append(symbol), declaration.Methods);
            block.Fields.Add(field);
            return;
        }

        if (IsDirective(text, ".pack") || IsDirective(text, ".size"))
        {
            if (block.Header.Layout == TypeLayoutKind.Auto)
            {
                throw new ReplException(".pack and .size require sequential or explicit layout");
            }

            var pack = text.StartsWith(".pack", StringComparison.Ordinal);
            var value = LayoutDirectiveParser.Parse(text[5..].Trim(), pack);
            if (pack)
            {
                block.PackingSize = value;
            }
            else
            {
                block.ClassSize = value;
            }

            return;
        }

        throw new ReplException($"instructions belong in a method body; class {block.Path} is open");
    }

    private void CloseType()
    {
        var block = _state.OpenTypes[^1];
        var declaration = _state.Types.DeclarationOf(block.Type)!;
        ValidateAccessors(block, declaration);
        declaration.Properties = [.. block.Accessors.Where(accessor => accessor.Property is not null).Select(accessor =>
        {
            var property = accessor.Property!;
            var isPublic = accessor.Accessors.Any(reference => declaration.Methods.Any(method => method.IsPublic
                && method.Name == reference.Name && method.IsStatic == reference.IsStatic
                && method.ParameterTypes.SequenceEqual(reference.ParameterTypes)));
            var existing = declaration.Properties.FirstOrDefault(candidate => candidate.Name == property.Name
                && SymbolIdentity.Equal(candidate.Type, property.Type)
                && candidate.Parameters.SequenceEqual(property.ParameterTypes));
            return new PropertySymbol(existing?.Definition ?? NextDefinition(), block.Type, property.Name,
                property.Type, property.ParameterTypes, isPublic, property.IsStatic);
        })];
        var undeclared = declaration.Methods.FirstOrDefault(method => !method.IsDeclared);
        if (undeclared is not null)
        {
            throw new ReplException($"class {block.Path} closes but {undeclared} was never declared");
        }

        if (block.Header.Layout == TypeLayoutKind.Explicit)
        {
            var missing = block.Fields.FirstOrDefault(
                field => !field.Attributes.HasFlag(FieldAttributes.Static) && field.Offset is null);
            if (missing is not null)
            {
                throw new ReplException($"explicit layout: field {missing.Name} needs an offset");
            }
        }

        var validation = new TypeValidationState(
            block.Type, block.Path, block.Kind, block.Header.Layout, block.PackingSize, block.ClassSize,
            block.Fields.Where(field => !field.Attributes.HasFlag(FieldAttributes.Static))
                .ToDictionary(field => field.Name, field => field.Offset),
            declaration.GenericParameters,
            block.Overrides.Concat(block.Bodies.SelectMany(body => body.Overrides)).Select(mapping => mapping.Target.Method).ToArray());
        if (_state.OpenTypes.Count == 1 && ReplaceFamily(block))
        {
            return;
        }

        if (_state.OpenTypes.Count == 1)
        {
            var missing = _state.Types.Entries.FirstOrDefault(entry => IsFamilyPath(entry.FullName, block.Path)
                && _state.Types.DeclarationOf(entry.Type) is { IsPlaceholder: true });
            if (missing.Type is not null)
            {
                throw new ReplException($"class {block.Path} closes but {missing.FullName} was referenced and never declared");
            }

            foreach (var completed in block.NestedValidations.Append(validation))
            {
                if (_pendingTypeValidations is not null)
                {
                    _pendingTypeValidations.Add(completed);
                }
                else
                {
                    TypeDeclarationBinding.Validate(completed, Scope());
                }
            }

            foreach (var body in block.NestedBodies.Concat(block.Bodies))
            {
                if (_pendingBodyValidations is not null)
                {
                    _pendingBodyValidations.Add(body);
                }
                else
                {
                    RecheckBody(body);
                }
            }
        }

        _state.Types.Add(block.Path, block.Type, new DeclarationSymbol(
            declaration.Type, declaration.BaseType, declaration.Interfaces, declaration.GenericParameters,
            declaration.Fields, declaration.Methods, false) { Properties = declaration.Properties });
        _state.OpenTypes.RemoveAt(_state.OpenTypes.Count - 1);
        _state.Types = _state.Types.Clone([.. _state.OpenTypes.Select(owner => owner.Path)]);
        if (_state.OpenTypes.Count == 0)
        {
            TypeSymbol[] family = [.. _state.Types.Entries.Where(entry => entry.FullName == block.Path
                || entry.FullName.StartsWith(block.Path + "/", StringComparison.Ordinal)).Select(entry => entry.Type)];
            _state.Definitions.RemoveAll(definition => definition.IsFamily && definition.Name == block.Path);
            _state.Definitions.Add(new EditingDefinition(
                block.Path, true, block.HeaderLine, [.. block.Lines], family,
                [.. ReferencedTypes(block, declaration)], ReferencedMethods(block).ToHashSet(StringComparer.Ordinal)));
            _state.CommittedTypes = _state.Types.Clone();
        }
        else
        {
            var parent = _state.OpenTypes[^1];
            parent.MetadataTypes.AddRange(ReferencedTypes(block, declaration));
            parent.NestedMethodReferences.AddRange(ReferencedMethods(block));
            parent.NestedValidations.AddRange(block.NestedValidations);
            parent.NestedValidations.Add(validation);
            parent.NestedBodies.AddRange(block.NestedBodies);
            parent.NestedBodies.AddRange(block.Bodies);
        }
    }
}
