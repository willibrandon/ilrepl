using System.Collections;
using System.Globalization;
using System.Reflection;
using IlRepl.Protocol;
using Mono.Cecil;

namespace IlRepl.Engine;

/// <summary>
/// Freezes both executable versions, scenario declarations, dependency images, and comparison inputs.
/// </summary>
public static partial class ComparisonCapture
{
    /// <summary>
    /// Captures one comparison without executing declarations, scenarios, or previously run cells.
    /// </summary>
    /// <param name="session">The session containing the committed edit and optional scenario.</param>
    /// <param name="command">The argument text following .compare.</param>
    /// <returns>The immutable package supplied to two fresh runtimes.</returns>
    public static ComparisonPackage Create(Session session, string command)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        var options = ComparisonCommand.Parse(command);
        var edit = session.Edits.FirstOrDefault(edit => edit.Name == options.Name)
            ?? throw new ReplException($"no edit '{options.Name}' in the session");
        var method = edit.Method ?? throw new ReplException($"edit '{edit.Name}' has no committed version");
        if (session.OpenMethod is not null || session.OpenType is not null)
        {
            throw new ReplException("close or abandon the open declaration before comparing methods");
        }

        if (options.Scenario is { } scenarioName)
        {
            var scenario = session.Methods.FirstOrDefault(method => method.Signature.Name == scenarioName)
                ?? throw new ReplException($"no session scenario '{scenarioName}'");
            if (scenario.Signature.Parameters.Count != 0)
            {
                throw new ReplException("a comparison scenario must be a parameterless session method");
            }

            edit.RequireScenarioSignature();
        }
        else
        {
            if (!method.IsStatic || method.ContainsGenericParameters)
            {
                throw new ReplException(
                    "use a parameterless CIL scenario to construct the receiver or supply generic arguments for this method");
            }

            var parameters = method.GetParameters();
            if (parameters.Length != options.Arguments.Count)
            {
                throw new ReplException($"{edit.Name} requires {parameters.Length} literal arguments; received {options.Arguments.Count}");
            }

            for (var index = 0; index < parameters.Length; index++)
            {
                _ = ValueLiteralParser.Parse(options.Arguments[index], parameters[index].ParameterType.IsByRef
                    ? parameters[index].ParameterType.GetElementType()! : parameters[index].ParameterType);
            }
        }

        var dependencies = new Dictionary<string, ComparisonAssembly>(StringComparer.OrdinalIgnoreCase);
        var original = CaptureImage(session, edit, options, original: true, dependencies);
        var edited = CaptureImage(session, edit, options, original: false, dependencies);
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(pair => (string)pair.Key, pair => (string)pair.Value!, StringComparer.Ordinal);
        return new ComparisonPackage(edit.Name, edit.Fingerprint, edit.Revision, original, edited, dependencies.Values.ToArray(),
            environment,
            CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name, options.StandardInput, Files(options.FixtureDirectory),
            options.TimeoutMilliseconds, 65536, options.Assert);
    }

    private static ComparisonImage CaptureImage(Session session, MethodEdit edit, ComparisonOptions options, bool original,
        Dictionary<string, ComparisonAssembly> dependencies)
    {
        if (original && edit.Baseline.Problems.Count != 0 && options.Scenario is null
            && !SessionAssemblies.TryGetDefinition(edit.Original.Method.Module.Assembly, out _))
        {
            return CaptureExternalOriginal(session, edit, options, dependencies);
        }

        MethodDefinition? entry = null;
        string[] typeArguments = [];
        string[] methodArguments = [];
        var image = AssemblyExporter.WriteComparison(session, edit, original, (writer, selected) =>
        {
            entry = ComparisonInstrumentation.Wrap(writer, selected);
            writer.Define(IlAsmRenderer.DefinitionOf(edit.Method!), entry);
            if (edit.Current!.CallableEntryPoint != edit.Method)
            {
                var forwarding = (MethodDefinition)writer.Import(IlAsmRenderer.DefinitionOf(edit.Current.CallableEntryPoint!));
                CecilForwardingMethod.Redirect(selected, forwarding.Name, entry);
            }

            if (options.Scenario is null)
            {
                var method = edit.Method!;
                if (method.DeclaringType is { IsConstructedGenericType: true } declaring)
                {
                    typeArguments = declaring.GetGenericArguments().Select(type => ArgumentName(writer.Import(type))).ToArray();
                }

                if (method is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } generic)
                {
                    methodArguments = generic.GetGenericArguments().Select(type => ArgumentName(writer.Import(type))).ToArray();
                }
            }
        });
        using (var module = ModuleDefinition.ReadModule(new MemoryStream(image, writable: false)))
        {
            foreach (var reference in module.AssemblyReferences)
            {
                CaptureDependency(reference.FullName, session, dependencies);
            }
        }

        var family = original && edit.Baseline.Problems.Count == 0 ? edit.Baseline : edit.Current!;
        var names = family.RuntimeTypes.ToDictionary(type => type.FullName!, type =>
        {
            var source = (Type)family.OriginalMember(type);
            return "[" + source.Assembly.GetName().Name + "]" + source.FullName;
        }, StringComparer.Ordinal);
        return new ComparisonImage(image, options.Scenario is null ? entry!.DeclaringType.FullName.Replace('/', '+') : "IlRepl.Cell",
            options.Scenario ?? entry!.Name, options.Scenario is null ? entry!.MetadataToken.ToInt32() : 0,
            typeArguments, methodArguments, options.Arguments, names);
    }

    private static ComparisonImage CaptureExternalOriginal(Session session, MethodEdit edit, ComparisonOptions options,
        Dictionary<string, ComparisonAssembly> dependencies)
    {
        var method = edit.Original.Requested;
        var owner = method.DeclaringType!;
        CaptureDependency(method.Module.Assembly.FullName!, session, dependencies);
        foreach (var argument in owner.GetGenericArguments()
            .Concat(method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes))
        {
            CaptureOriginalArgument(argument, session, dependencies);
        }

        var writer = new CecilWriter(SessionAssemblyKind.Cell);
        var entry = CecilOriginalCall.Wrap(method, writer);
        var image = writer.Write();
        foreach (var reference in writer.Module.AssemblyReferences)
        {
            CaptureDependency(reference.FullName, session, dependencies);
        }

        return new ComparisonImage(image, entry.DeclaringType.FullName, entry.Name, entry.MetadataToken.ToInt32(),
            [], [], options.Arguments, new Dictionary<string, string>())
        {
            OriginalAssembly = method.Module.Assembly.FullName,
            OriginalModule = method.Module.ModuleVersionId,
        };
    }

    private static void CaptureDependency(string identity, Session session, Dictionary<string, ComparisonAssembly> captured)
    {
        if (captured.ContainsKey(identity))
        {
            return;
        }

        var name = new AssemblyName(identity);
        var assembly = session.Resolver.Assemblies.FirstOrDefault(assembly =>
            string.Equals(assembly.FullName, name.FullName, StringComparison.OrdinalIgnoreCase));
        if (assembly is null)
        {
            assembly = SessionAssemblies.Resolve(name) ?? Assembly.Load(name);
        }

        // Runtime and ilrepl assemblies are supplied by the fresh host or browser bundle.
        var coreDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var location = assembly.IsDynamic ? "" : assembly.Location;
        if (assembly == typeof(ComparisonProbe).Assembly || assembly == typeof(ComparisonPackage).Assembly
            || (!string.IsNullOrEmpty(coreDirectory) && Path.GetDirectoryName(location) == coreDirectory)
            || (OperatingSystem.IsBrowser() && (name.Name?.StartsWith("System.", StringComparison.Ordinal) == true
                || name.Name == "System" || name.Name == "netstandard")))
        {
            return;
        }

        byte[]? image = null;
        if (SessionAssemblies.TryGetDefinition(assembly, out var definition))
        {
            image = definition.Image;
        }
        else if (session.Resolver.TryGetImage(assembly, out var retained))
        {
            image = retained;
        }
        else if (!string.IsNullOrEmpty(location))
        {
            image = File.ReadAllBytes(location);
        }

        if (image is null)
        {
            throw new ReplException($"comparison cannot capture dependency image {identity}");
        }

        captured.Add(identity, new ComparisonAssembly(identity, image));
        using var module = ModuleDefinition.ReadModule(new MemoryStream(image, writable: false));
        if (module.Mvid != assembly.ManifestModule.ModuleVersionId)
        {
            throw new ReplException($"dependency image {identity} no longer matches the loaded module");
        }

        foreach (var reference in module.AssemblyReferences)
        {
            CaptureDependency(reference.FullName, session, captured);
        }
    }

    private static List<ComparisonFile> Files(string? directory)
    {
        if (directory is null)
        {
            return [];
        }

        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            throw new ReplException($"comparison fixture directory '{directory}' does not exist");
        }

        var result = new List<ComparisonFile>();
        foreach (var path in FixtureEntries(root).Order(StringComparer.Ordinal))
        {
            var attributes = File.GetAttributes(path);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            string? linkTarget = null;
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                FileSystemInfo entry = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
                if (entry.LinkTarget is not { } target)
                {
                    throw new ReplException($"comparison fixture '{path}' is an unsupported reparse point");
                }

                var destination = Path.GetFullPath(target, Path.GetDirectoryName(path)!);
                var relative = Path.GetRelativePath(root, destination);
                if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || Path.IsPathRooted(relative))
                {
                    throw new ReplException($"comparison fixture link '{path}' points outside the captured directory");
                }

                linkTarget = Path.GetRelativePath(Path.GetDirectoryName(path)!, destination);
            }

            result.Add(new ComparisonFile(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                isDirectory || linkTarget is not null ? [] : File.ReadAllBytes(path))
            {
                IsDirectory = isDirectory,
                LinkTarget = linkTarget,
            });
        }

        return result;
    }

    private static IEnumerable<string> FixtureEntries(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", options))
            {
                yield return path;
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Push(path);
                }
            }
        }
    }
}
