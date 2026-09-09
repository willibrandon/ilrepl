using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace IlRepl.Engine;

/// <summary>
/// Writes a type family exactly as declared into a session assembly, loads it, and prepares
/// its bodies on the JIT. The live type carries the declared metadata and nothing else: no
/// synthesized constructor, the declared layout, the declared attributes and modifiers. The
/// same writer serves an export, which is why live and saved metadata agree.
/// </summary>
public static class TypeEmitter
{
    /// <summary>
    /// Writes and loads a family.
    /// </summary>
    /// <param name="family">The outermost declaration, with its nested types.</param>
    /// <param name="prototypes">The prototype and members of every declaration, by path.</param>
    /// <param name="trampolines">The trampolines of the session methods bodies may call, by name.</param>
    /// <param name="prepare">True to ask the JIT to compile every body.</param>
    /// <returns>The loaded family.</returns>
    /// <exception cref="ReplException">The writer, the loader, or the JIT rejected the family.</exception>
    public static CompiledFamily Compile(TypeDeclaration family, IReadOnlyDictionary<string, (TypeBuilder Prototype, OwnMembers Members)> prototypes, IReadOnlyDictionary<string, MethodTrampoline> trampolines, bool prepare)
    {
        ArgumentNullException.ThrowIfNull(family);
        var name = SessionAssemblies.NextName(SessionAssemblyKind.Types);
        var (image, dependencies) = Write(family, prototypes, trampolines, name, null);
        var compiled = Load(image, name, dependencies, family);
        if (prepare)
        {
            try
            {
                Prepare(compiled, family);
            }
            catch
            {
                SessionAssemblies.Release(compiled.Definition);
                throw;
            }
        }

        return compiled;
    }

    /// <summary>
    /// Writes a family into an image under a name taken in advance. Prototypes of other families
    /// written in the same group are referenced by their assembly names.
    /// </summary>
    /// <param name="family">The outermost declaration.</param>
    /// <param name="prototypes">The prototype and members of every declaration, by path.</param>
    /// <param name="trampolines">The trampolines of the session methods, by name.</param>
    /// <param name="name">The assembly's simple name.</param>
    /// <param name="externals">Prototypes of the other families of the group, or null.</param>
    /// <returns>The image and the loaded session assemblies it references.</returns>
    /// <exception cref="ReplException">The writer rejected the family.</exception>
    public static (byte[] Image, IReadOnlyList<DefinitionAssembly> Dependencies) Write(TypeDeclaration family, IReadOnlyDictionary<string, (TypeBuilder Prototype, OwnMembers Members)> prototypes, IReadOnlyDictionary<string, MethodTrampoline> trampolines, string name, IReadOnlyDictionary<Type, CecilWriter.ExternalPrototype>? externals)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(prototypes);
        ArgumentNullException.ThrowIfNull(trampolines);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var writer = new CecilWriter(SessionAssemblyKind.Types, name);
        foreach (var (prototype, external) in externals ?? new Dictionary<Type, CecilWriter.ExternalPrototype>())
        {
            writer.DefineExternal(prototype, external);
        }

        try
        {
            WriteAll(writer, [(family, prototypes, null)], trampolines);
            return (writer.Write(), writer.Dependencies);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or NullReferenceException)
        {
            throw new ReplException($"the writer rejected {family.KindWord} {family.FullName}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Loads a written family and resolves its types.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="name">The assembly's simple name.</param>
    /// <param name="dependencies">The loaded session assemblies it references.</param>
    /// <param name="family">The outermost declaration.</param>
    /// <returns>The loaded family.</returns>
    /// <exception cref="ReplException">The runtime rejected the family.</exception>
    public static CompiledFamily Load(byte[] image, string name, IReadOnlyList<DefinitionAssembly> dependencies, TypeDeclaration family)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(family);
        DefinitionAssembly definition;
        try
        {
            definition = SessionAssemblies.Load(image, name, SessionAssemblyKind.Types, dependencies);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ArgumentException or InvalidOperationException)
        {
            throw new ReplException($"the runtime rejected {family.KindWord} {family.FullName}: {ex.Message}", ex);
        }

        try
        {
            var types = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var declaration in family.Family)
            {
                types[declaration.FullName] = LoadType(definition.Assembly, declaration);
            }

            return new CompiledFamily(definition, types);
        }
        catch
        {
            SessionAssemblies.Release(definition);
            throw;
        }
    }

    /// <summary>
    /// Asks the JIT to compile every body of a loaded family.
    /// </summary>
    /// <param name="compiled">The loaded family.</param>
    /// <param name="family">Its outermost declaration.</param>
    /// <exception cref="ReplException">The JIT or the runtime rejected a body.</exception>
    public static void Prepare(CompiledFamily compiled, TypeDeclaration family)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(family);
        foreach (var declaration in family.Family)
        {
            Prepare(declaration, compiled.Types[declaration.FullName]);
        }
    }

    /// <summary>
    /// Writes a family into an existing writer, for an export. The prototypes map onto the
    /// definitions written; nothing is loaded.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="family">The outermost declaration.</param>
    /// <param name="prototypes">The prototype and members of every declaration, by path.</param>
    /// <param name="trampolines">The trampolines of the session methods, by name.</param>
    /// <param name="runtimeTypes">The loaded types of the family by path, whose members other bodies are bound to, or null.</param>
    public static void Write(CecilWriter writer, TypeDeclaration family, IReadOnlyDictionary<string, (TypeBuilder Prototype, OwnMembers Members)> prototypes, IReadOnlyDictionary<string, MethodTrampoline> trampolines, IReadOnlyDictionary<string, Type>? runtimeTypes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(family);
        WriteAll(writer, [(family, prototypes, runtimeTypes)], trampolines);
    }

    /// <summary>
    /// Writes several families into one writer: every type of every family is declared before
    /// any shape or body is imported, so the families may mention each other in any order.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="families">The families with their prototypes and, for an export, their loaded types.</param>
    /// <param name="trampolines">The trampolines of the session methods, by name.</param>
    public static void WriteAll(CecilWriter writer, IReadOnlyList<(TypeDeclaration Family, IReadOnlyDictionary<string, (TypeBuilder Prototype, OwnMembers Members)> Prototypes, IReadOnlyDictionary<string, Type>? RuntimeTypes)> families, IReadOnlyDictionary<string, MethodTrampoline> trampolines)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(families);
        ArgumentNullException.ThrowIfNull(trampolines);
        var emitters = families.Select(f => (
            Emitter: new TypeFamilyEmitter(writer, f.Prototypes, trampolines) { RuntimeTypes = f.RuntimeTypes }, f.Family)).ToList();
        foreach (var (emitter, family) in emitters)
        {
            emitter.Declare(family);
        }

        foreach (var (emitter, family) in emitters)
        {
            emitter.Shape(family);
        }

        foreach (var (emitter, family) in emitters)
        {
            emitter.Members(family);
        }

        foreach (var (emitter, family) in emitters)
        {
            emitter.Details(family);
        }

        foreach (var (emitter, family) in emitters)
        {
            emitter.Bodies(family);
        }
    }

    internal static Type LoadType(Assembly assembly, TypeDeclaration declaration)
    {
        var name = ReflectionName(declaration);
        try
        {
            return assembly.GetType(name, throwOnError: true)!;
        }
        catch (TypeLoadException ex)
        {
            throw new ReplException($"the runtime rejected {declaration.KindWord} {declaration.FullName}: {ex.Message}", ex);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new ReplException($"the runtime rejected {declaration.KindWord} {declaration.FullName}: {ex.InnerException.Message}", ex);
        }
    }

    /// <summary>
    /// The reflection name of a declaration: the namespace, then the nesting path with '+'.
    /// </summary>
    /// <param name="declaration">The declaration.</param>
    /// <returns>The name <c>Assembly.GetType</c> accepts.</returns>
    public static string ReflectionName(TypeDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        return TypeResolver.ReflectionName(declaration.FullName);
    }

    private static void Prepare(TypeDeclaration declaration, Type type)
    {
        Type[]? instantiation = null;
        if (type.IsGenericTypeDefinition)
        {
            instantiation = ClosedInstantiation(type.GetGenericArguments());
            if (instantiation is null)
            {
                return;
            }
        }

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            if (method.IsAbstract || method.ContainsGenericParameters && method is MethodInfo { IsGenericMethodDefinition: true })
            {
                continue;
            }

            try
            {
                if (instantiation is null)
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                }
                else
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle, [.. instantiation.Select(t => t.TypeHandle)]);
                }
            }
            catch (InvalidProgramException ex) when (ex.Message.Contains("Vararg", StringComparison.OrdinalIgnoreCase))
            {
                throw new ReplException($"the runtime only supports the vararg calling convention on Windows; {declaration.KindWord} {declaration.FullName}::{method.Name} cannot be prepared here", ex);
            }
            catch (InvalidProgramException ex)
            {
                throw new ReplException($"the JIT rejected {declaration.FullName}::{method.Name}: {ex.Message} (check .show for a stack mismatch between branches)", ex);
            }
            catch (Exception ex) when (ex is TypeLoadException or MissingMemberException or BadImageFormatException or TypeInitializationException or ArgumentException)
            {
                throw new ReplException($"the runtime rejected {declaration.FullName}::{method.Name}: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Picks type arguments the constraints admit, or null when a constraint needs a specific type.
    /// </summary>
    private static Type[]? ClosedInstantiation(Type[] parameters)
    {
        var arguments = new Type[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.GetGenericParameterConstraints().Length > 0)
            {
                return null;
            }

            var special = parameter.GenericParameterAttributes & System.Reflection.GenericParameterAttributes.SpecialConstraintMask;
            arguments[i] = special.HasFlag(System.Reflection.GenericParameterAttributes.NotNullableValueTypeConstraint) ? typeof(int) : typeof(object);
        }

        return arguments;
    }
}
