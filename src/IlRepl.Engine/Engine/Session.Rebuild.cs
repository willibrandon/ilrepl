using System.Reflection.Emit;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Replacing a family other definitions depend on. Every member of the closure, the new family
/// included, is declared ahead of its lines as a fresh prototype, so the replays bind to the new
/// identities whatever order they run in and however the members refer to each other. The
/// group is then written as assemblies that name each other, loaded together, and published
/// as one, or not at all.
/// </summary>
public sealed partial class Session
{
    private LineResult ReplaceWithDependents(OpenTypeBlock block, TypeDeclaration declaration, SessionType previous, (List<SessionType> Types, List<SessionMethod> Methods) closure)
    {
        var savedTypes = _types.ToList();
        var savedMethods = _methods.ToList();
        var savedTable = _typeTable;
        var savedCell = _cell;
        var savedSubmissions = Submissions;
        var created = new List<DefinitionAssembly>();
        var rebuiltNames = new List<string>();
        _rebuilding = true;
        _pendingFamilies.Clear();
        _pendingMethods.Clear();
        _predeclared.Clear();
        // The block being closed is replayed like the rest; nothing stays open meanwhile.
        _openType = null;
        _openMember = null;
        _openAccessor = null;
        try
        {
            // 1. Declare every family of the group ahead of its lines, mapping the identities the
            //    session holds onto the new prototypes.
            var map = new EmitMap(_ => throw new InvalidOperationException("no session methods are mapped here"));
            var members = new List<(SessionType? Old, TypeDeclaration Declaration, string HeaderLine, IReadOnlyList<string> Lines, int Order)>
            {
                (previous, declaration, block.HeaderLine, [.. block.Lines], int.MaxValue),
            };
            members.AddRange(closure.Types.Select(t => (Old: (SessionType?)t, t.Declaration, t.Declaration.HeaderLine, t.Declaration.Lines, t.Order)));
            var predeclared = new Dictionary<string, Dictionary<string, (TypeBuilder Prototype, OwnMembers Members)>>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                var module = NewPrototypeModule();
                var family = new Dictionary<string, (TypeBuilder Prototype, OwnMembers Members)>(StringComparer.Ordinal);
                var generations = new List<IReadOnlyDictionary<string, (TypeBuilder Prototype, OwnMembers Members)>>();
                if (member.Old is not null)
                {
                    generations.Add(member.Old.Prototypes);
                }

                if (ReferenceEquals(member.Declaration, declaration))
                {
                    generations.Add(block.FamilyTypes);
                }

                DeclareAhead(member.Declaration, null, module, family, map, member.Old?.Types, generations);
                predeclared[member.Declaration.FullName] = family;
                foreach (var (path, entry) in family)
                {
                    _predeclared[path] = (entry.Prototype, entry.Members, module);
                }
            }

            foreach (var member in members)
            {
                ShapeAhead(member.Declaration, predeclared[member.Declaration.FullName], map);
            }

            // 2. The scratch table names the prototypes; the closure's method signatures follow the map.
            var scratch = _typeTable.Clone();
            foreach (var member in members)
            {
                foreach (var path in member.Old?.Types.Keys ?? member.Declaration.Family.Select(d => d.FullName))
                {
                    scratch.Remove(path);
                }
            }

            foreach (var (path, entry) in _predeclared)
            {
                scratch.Add(path, entry.Prototype);
                scratch.SetMembers(entry.Prototype, entry.Members);
            }

            _typeTable = scratch;
            var rebuiltMethods = closure.Methods.ToHashSet();
            for (var i = 0; i < _methods.Count; i++)
            {
                if (rebuiltMethods.Contains(_methods[i]))
                {
                    _methods[i] = _methods[i] with { Signature = MapSignature(_methods[i].Signature, map) };
                }
            }

            // 3. Replay every member of the group in the order it was accepted.
            var replays = members.Select(m => (m.Order, Name: m.Declaration.KindWord + " " + m.Declaration.FullName, Replay: (Action)(() => ReplayFamilyLines(m.HeaderLine, m.Lines))))
                .Concat(closure.Methods.Select(m => (m.Order, Name: "method " + m.Signature.Name, Replay: (Action)(() => ReplayMethodLines(m)))))
                .OrderBy(r => r.Order)
                .ToList();
            foreach (var (_, name, replay) in replays)
            {
                try
                {
                    replay();
                }
                catch (ReplException ex)
                {
                    throw new ReplException($"cannot redefine {block.KindWord} {block.Path}: {name}: {ex.Message}  (redefine {name} first without it, or .reset)", ex);
                }

                if (name != block.KindWord + " " + block.Path)
                {
                    rebuiltNames.Add(name);
                }
            }

            // 4. Write the group: each family under a name taken in advance, the others referenced by it.
            var names = _pendingFamilies.ToDictionary(f => f.Declaration.FullName, _ => SessionAssemblies.NextName(SessionAssemblyKind.Types), StringComparer.Ordinal);
            var externals = new Dictionary<Type, CecilWriter.ExternalPrototype>(ReferenceEqualityComparer.Instance);
            foreach (var pending in _pendingFamilies)
            {
                foreach (var nested in pending.Declaration.Family)
                {
                    var (prototype, own) = pending.Prototypes[nested.FullName];
                    var enclosingPath = nested.FullName.Contains('/') ? nested.FullName[..nested.FullName.LastIndexOf('/')] : null;
                    var enclosing = enclosingPath is null ? null : pending.Prototypes[enclosingPath].Prototype;
                    externals[prototype] = new CecilWriter.ExternalPrototype(names[pending.Declaration.FullName], nested.IsNested ? "" : nested.Namespace, nested.Name, enclosing, own);
                }
            }

            var trampolines = _methods.ToDictionary(m => m.Signature.Name, m => m.Trampoline, StringComparer.Ordinal);
            var newTrampolines = new Dictionary<string, MethodTrampoline>(StringComparer.Ordinal);
            foreach (var pending in _pendingMethods)
            {
                var trampoline = MethodTrampoline.Create(pending.Signature, externals);
                created.Add(trampoline.Definition);
                newTrampolines[pending.Signature.Name] = trampoline;
                trampolines[pending.Signature.Name] = trampoline;
            }

            var images = new List<(PendingFamily Pending, byte[] Image, IReadOnlyList<DefinitionAssembly> Dependencies)>();
            foreach (var pending in _pendingFamilies)
            {
                var own = pending.Prototypes.Values.Select(p => (Type)p.Prototype).ToHashSet(ReferenceEqualityComparer.Instance);
                var foreign = new Dictionary<Type, CecilWriter.ExternalPrototype>(ReferenceEqualityComparer.Instance);
                foreach (var (prototype, external) in externals)
                {
                    if (!own.Contains(prototype))
                    {
                        foreign[prototype] = external;
                    }
                }

                var (image, dependencies) = TypeEmitter.Write(pending.Declaration, pending.Prototypes, trampolines, names[pending.Declaration.FullName], foreign);
                images.Add((pending, image, dependencies));
            }

            // 5. Load every image before any type is resolved, since they name each other.
            var compiled = new List<(PendingFamily Pending, CompiledFamily Family)>();
            foreach (var (pending, image, dependencies) in images)
            {
                var definition = SessionAssemblies.Load(image, names[pending.Declaration.FullName], SessionAssemblyKind.Types, dependencies);
                created.Add(definition);
                compiled.Add((pending, new CompiledFamily(definition, new Dictionary<string, Type>(StringComparer.Ordinal))));
            }

            foreach (var (a, _) in compiled)
            {
                foreach (var (b, _) in compiled)
                {
                    compiled.First(c => ReferenceEquals(c.Pending, a)).Family.Definition.AddDependency(compiled.First(c => ReferenceEquals(c.Pending, b)).Family.Definition);
                }

                foreach (var trampoline in newTrampolines.Values)
                {
                    compiled.First(c => ReferenceEquals(c.Pending, a)).Family.Definition.AddDependency(trampoline.Definition);
                }
            }

            var loaded = new List<(PendingFamily Pending, CompiledFamily Family)>();
            foreach (var (pending, family) in compiled)
            {
                var types = new Dictionary<string, Type>(StringComparer.Ordinal);
                foreach (var nested in pending.Declaration.Family)
                {
                    types[nested.FullName] = TypeEmitter.LoadType(family.Definition.Assembly, nested);
                }

                loaded.Add((pending, new CompiledFamily(family.Definition, types)));
            }

            // 6. Versions, then the JIT over everything, then the trampolines.
            var versions = new List<(PendingMethod Pending, MethodTrampoline Trampoline, CompiledMethodVersion Version)>();
            foreach (var pending in _pendingMethods)
            {
                var trampoline = newTrampolines[pending.Signature.Name];
                var version = DefinitionCompiler.CompileMethod(pending.Signature, pending.State, trampoline, trampolines, MethodPreparation.IsSupported, externals);
                created.Add(version.Definition);
                foreach (var (_, family) in loaded)
                {
                    version.Definition.AddDependency(family.Definition);
                }

                versions.Add((pending, trampoline, version));
            }

            if (MethodPreparation.IsSupported)
            {
                foreach (var (pending, family) in loaded)
                {
                    TypeEmitter.Prepare(family, pending.Declaration);
                }
            }

            foreach (var (_, trampoline, version) in versions)
            {
                trampoline.Bind(version.Implementation);
            }

            // 7. Publish: the records, the table, the cell.
            var table = savedTable.Clone();
            foreach (var member in members)
            {
                foreach (var path in member.Old?.Types.Keys ?? [])
                {
                    table.Remove(path);
                }
            }

            foreach (var (_, family) in loaded)
            {
                foreach (var (path, type) in family.Types)
                {
                    table.Add(path, type);
                }
            }

            _typeTable = table;
            _types.Clear();
            _types.AddRange(savedTypes);
            _methods.Clear();
            _methods.AddRange(savedMethods);
            Submissions = savedSubmissions;
            var released = new List<DefinitionAssembly>();
            foreach (var (pending, family) in loaded)
            {
                Submissions++;
                var accepted = new SessionType(pending.Declaration, family.Types, family.Types[pending.Declaration.FullName], family.Definition, pending.Prototypes) { Order = Submissions };
                var index = pending.Previous is null ? -1 : _types.IndexOf(pending.Previous);
                if (index < 0)
                {
                    _types.Add(accepted);
                }
                else
                {
                    _types[index] = accepted;
                }

                if (pending.Previous?.Definition is { } old)
                {
                    released.Add(old);
                }
            }

            // Signatures name the loaded types from here on, as cells and later bodies expect.
            var runtimeMap = new EmitMap(_ => throw new InvalidOperationException("no session methods are mapped here"));
            foreach (var (pending, family) in loaded)
            {
                foreach (var (path, entry) in pending.Prototypes)
                {
                    var runtime = family.Types[path];
                    runtimeMap.Add(entry.Prototype, runtime);
                    var prototypeParameters = entry.Prototype.IsGenericTypeDefinition ? entry.Prototype.GetGenericArguments() : [];
                    var runtimeParameters = runtime.IsGenericTypeDefinition ? runtime.GetGenericArguments() : [];
                    for (var i = 0; i < prototypeParameters.Length && i < runtimeParameters.Length; i++)
                    {
                        runtimeMap.Add(prototypeParameters[i], runtimeParameters[i]);
                    }
                }
            }

            foreach (var (pending, trampoline, version) in versions)
            {
                // The record replayed against was a mapped copy; the original is found by name.
                var index = _methods.FindIndex(m => m.Signature.Name == pending.Signature.Name);
                var committed = new SessionMethod(MapSignature(pending.Signature, runtimeMap), pending.HeaderLine, pending.BodyLines, pending.State, trampoline, version) { Order = index < 0 ? Submissions : _methods[index].Order };
                if (index < 0)
                {
                    _methods.Add(committed);
                }
                else
                {
                    released.Add(_methods[index].Version.Definition);
                    released.Add(_methods[index].Trampoline.Definition);
                    _methods[index] = committed;
                }
            }

            CellState cell;
            try
            {
                cell = BuildCell(Signatures(), table);
            }
            catch (ReplException ex)
            {
                throw new ReplException($"cannot redefine {block.KindWord} {block.Path}: the cell body would no longer compile: {ex.Message}  (.clear the cell first)", ex);
            }

            _cell = cell;
            _openType = null;

            // Only now has anything changed that a rollback could not undo: a redefinition the
            // cell refuses leaves the session, its generation included, as it was.
            Generation++;
            CompletionRevision++;
            foreach (var definition in released.Distinct())
            {
                SessionAssemblies.Release(definition);
            }
        }
        catch
        {
            _types.Clear();
            _types.AddRange(savedTypes);
            _methods.Clear();
            _methods.AddRange(savedMethods);
            _typeTable = savedTable;
            _cell = savedCell;
            Submissions = savedSubmissions;
            _openType = null;
            _open = null;
            _openMember = null;
            _openAccessor = null;
            foreach (var definition in created)
            {
                SessionAssemblies.Release(definition);
            }

            throw;
        }
        finally
        {
            _rebuilding = false;
            _predeclared.Clear();
            _pendingFamilies.Clear();
            _pendingMethods.Clear();
        }

        var list = rebuiltNames.Count == 1 ? rebuiltNames[0] : string.Join(", ", rebuiltNames.Take(rebuiltNames.Count - 1)) + " and " + rebuiltNames[^1];
        return new LineResult(LineOutcome.TypeEnd, null, $"replaced {block.KindWord} {block.Path}; rebuilt {list} (existing instances and delegates keep the previous definitions)");
    }

    /// <summary>
    /// Defines the prototype of a declaration and its nested types ahead of their lines, and
    /// maps the identities the session holds for them onto the new builders.
    /// </summary>
    private static void DeclareAhead(TypeDeclaration declaration, TypeBuilder? enclosing, ModuleBuilder module, Dictionary<string, (TypeBuilder Prototype, OwnMembers Members)> family, EmitMap map, IReadOnlyDictionary<string, Type>? oldTypes, IReadOnlyList<IReadOnlyDictionary<string, (TypeBuilder Prototype, OwnMembers Members)>> oldPrototypes)
    {
        var builder = enclosing is null
            ? module.DefineType(declaration.Namespace.Length == 0 ? declaration.Name : declaration.Namespace + "." + declaration.Name, declaration.Attributes)
            : enclosing.DefineNestedType(declaration.Name, declaration.Attributes);
        Type[] generics = declaration.TypeParameters.Count > 0 ? builder.DefineGenericParameters([.. declaration.TypeParameters.Select(p => p.Name)]) : [];
        family[declaration.FullName] = (builder, new OwnMembers());
        void MapOld(Type old)
        {
            map.Add(old, builder);
            var oldGenerics = old.IsGenericTypeDefinition ? old.GetGenericArguments() : [];
            for (var i = 0; i < oldGenerics.Length && i < generics.Length; i++)
            {
                map.Add(oldGenerics[i], generics[i]);
            }
        }

        if (oldTypes is not null && oldTypes.TryGetValue(declaration.FullName, out var oldType))
        {
            MapOld(oldType);
        }

        foreach (var generation in oldPrototypes)
        {
            // The accepted family's prototypes, and the ones the edited block just built, both
            // appear in bodies and declarations that are replayed or shaped.
            if (generation.TryGetValue(declaration.FullName, out var oldPrototype))
            {
                MapOld(oldPrototype.Prototype);
            }
        }

        foreach (var nested in declaration.NestedTypes)
        {
            DeclareAhead(nested, builder, module, family, map, oldTypes, oldPrototypes);
        }
    }

    /// <summary>
    /// Gives a prototype declared ahead its base, interfaces, constraints, fields, and method
    /// builders, every type mapped onto the new identities, so lines can claim them.
    /// </summary>
    private static void ShapeAhead(TypeDeclaration declaration, Dictionary<string, (TypeBuilder Prototype, OwnMembers Members)> family, EmitMap map)
    {
        var (builder, own) = family[declaration.FullName];
        var baseType = declaration.BaseType is null ? null : map.Map(declaration.BaseType);
        var interfaces = declaration.Interfaces.Select(map.Map).ToList();
        if (baseType is not null)
        {
            builder.SetParent(baseType);
        }

        foreach (var i in interfaces)
        {
            builder.AddInterfaceImplementation(i);
        }

        own.BaseType = baseType;
        own.Interfaces = interfaces;
        var generics = builder.IsGenericTypeDefinition ? builder.GetGenericArguments() : [];
        for (var i = 0; i < declaration.TypeParameters.Count && i < generics.Length; i++)
        {
            var parameter = declaration.TypeParameters[i];
            var gp = (GenericTypeParameterBuilder)generics[i];
            gp.SetGenericParameterAttributes(parameter.Attributes);
            var constraints = parameter.Constraints.Select(map.Map).ToList();
            RuntimeGenericConstraints.Register(gp, parameter with { Constraints = constraints });
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
        }

        foreach (var field in declaration.Fields)
        {
            var mapped = field with
            {
                Type = map.Map(field.Type),
                ExactType = field.ExactType is null ? null : map.Map(field.ExactType),
                RequiredModifiers = [.. field.RequiredModifiers.Select(map.Map)],
                OptionalModifiers = [.. field.OptionalModifiers.Select(map.Map)],
            };
            var fieldBuilder = builder.DefineField(mapped.Name, mapped.Type, [.. mapped.RequiredModifiers], [.. mapped.OptionalModifiers], mapped.Attributes);
            if (mapped.Offset is { } offset)
            {
                fieldBuilder.SetOffset(offset);
            }

            own.AddForward(mapped, fieldBuilder);
        }

        foreach (var method in declaration.Methods)
        {
            var signature = MapSignature(method.Signature, map);
            var methodBuilder = DefineMethodBuilder(builder, signature, out var methodGenerics);
            if (methodGenerics.Length > 0)
            {
                // The signature's own parameters are the old method's; the builder's replace them by position.
                var methodMap = new EmitMap(_ => throw new InvalidOperationException("no session methods are mapped here"));
                foreach (var old in SignatureIdentity.MethodParametersOf(signature))
                {
                    if (old is not null && old.GenericParameterPosition < methodGenerics.Length)
                    {
                        methodMap.Add(old, methodGenerics[old.GenericParameterPosition]);
                    }
                }

                signature = MapSignature(signature, methodMap);
                if (methodBuilder is MethodBuilder generic)
                {
                    generic.SetSignature(signature.ReturnType, [.. signature.ReturnRequiredModifiers], [.. signature.ReturnOptionalModifiers], signature.ParameterTypes, [.. signature.Parameters.Select(p => p.RequiredModifiers.ToArray())], [.. signature.Parameters.Select(p => p.OptionalModifiers.ToArray())]);
                }
            }

            own.Add(signature, methodBuilder, declared: false);
        }

        foreach (var nested in declaration.NestedTypes)
        {
            ShapeAhead(nested, family, map);
        }
    }

    /// <summary>
    /// A signature with every type mapped onto the new identities.
    /// </summary>
    private static MethodSignature MapSignature(MethodSignature signature, EmitMap map) => signature with
    {
        ReturnType = map.Map(signature.ReturnType),
        ReturnRequiredModifiers = [.. signature.ReturnRequiredModifiers.Select(map.Map)],
        ReturnOptionalModifiers = [.. signature.ReturnOptionalModifiers.Select(map.Map)],
        Parameters = [.. signature.Parameters.Select(p => p with
        {
            Type = map.Map(p.Type),
            ExactType = p.ExactType is null ? null : map.Map(p.ExactType),
            RequiredModifiers = [.. p.RequiredModifiers.Select(map.Map)],
            OptionalModifiers = [.. p.OptionalModifiers.Select(map.Map)],
        })],
        TypeParameters = [.. signature.TypeParameters.Select(p => p with { Constraints = [.. p.Constraints.Select(map.Map)] })],
        ExactSymbol = signature.ExactSymbol is null ? null : SymbolRemapper.Method(
            signature.ExactSymbol, signature.ExactSymbol.Definition, map.Map, signature.ExactSymbol.IsDeclared),
    };

    private void ReplayFamilyLines(string headerLine, IReadOnlyList<string> lines)
    {
        // Stored lines hold no comments; they are replayed as they are.
        _openType = null;
        _openMember = null;
        _openAccessor = null;
        AddLine(NormalizedLine.FromText(headerLine));
        foreach (var line in lines)
        {
            AddLine(NormalizedLine.FromText(line));
        }

        if (_openType is not null)
        {
            AddLine(NormalizedLine.FromText("}"));
        }
    }

    private void ReplayMethodLines(SessionMethod method)
    {
        _open = null;
        _openType = null;
        _openMember = null;
        _openAccessor = null;
        AddLine(NormalizedLine.FromText(method.HeaderLine));
        foreach (var line in method.BodyLines)
        {
            AddLine(NormalizedLine.FromText(line));
        }

        if (_open is not null)
        {
            AddLine(NormalizedLine.FromText("}"));
        }
    }
}
