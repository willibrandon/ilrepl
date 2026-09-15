using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Executes one captured side in a process or browser worker dedicated to that single execution.
/// </summary>
public static partial class ComparisonWorker
{
    /// <summary>
    /// Initializes the captured conditions and executes the selected call or scenario in this fresh runtime.
    /// </summary>
    /// <param name="package">The immutable executable and input package.</param>
    /// <param name="original">Whether to execute the captured original side.</param>
    /// <param name="ready">Signals that runtime startup completed and the execution timeout must begin.</param>
    /// <param name="outputLimit">Requests immediate termination when console output exceeds its limit.</param>
    /// <param name="captureOutput">Whether to capture console writers in memory or let the host capture its standard streams.</param>
    /// <param name="useStandardInput">Whether the host supplies captured input through the actual standard-input stream.</param>
    /// <param name="restoreFileTimes">An optional host implementation for restoring timestamps on its filesystem.</param>
    /// <returns>The completed observations or setup failure.</returns>
    public static async Task<ComparisonSide> ExecuteAsync(ComparisonPackage package, bool original, Action ready, Action outputLimit,
        bool captureOutput = true, bool useStandardInput = false, Action<ComparisonFile>? restoreFileTimes = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(ready);
        ArgumentNullException.ThrowIfNull(outputLimit);
        var image = original ? package.Original : package.Edited;
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var previousInput = OperatingSystem.IsBrowser() ? TextReader.Null : Console.In;
        var exceeded = false;
        void Limit()
        {
            exceeded = true;
            outputLimit();
        }

        using var stdout = captureOutput ? new ComparisonOutputWriter(package.OutputLimit, Limit) : null;
        using var stderr = captureOutput ? new ComparisonOutputWriter(package.OutputLimit, Limit) : null;
        using var stdin = useStandardInput ? null : new StringReader(package.StandardInput);
        var loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        var preserveContext = package.Original.OriginalAssembly is not null;
        var executionContext = preserveContext && package.Dependencies.Any(dependency => dependency.IsCollectible)
            ? new AssemblyLoadContext("ilrepl-comparison", isCollectible: true) : AssemblyLoadContext.Default;
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            if (loaded.TryGetValue(name.FullName, out var assembly))
            {
                return assembly;
            }

            var dependency = package.Dependencies.FirstOrDefault(dependency =>
                string.Equals(new AssemblyName(dependency.Name).FullName, name.FullName, StringComparison.OrdinalIgnoreCase));
            if (dependency is null)
            {
                return null;
            }

            assembly = LoadDependency(preserveContext ? executionContext : context, dependency, preserveContext);
            loaded.Add(name.FullName, assembly);
            return assembly;
        }

        using var reflection = executionContext.EnterContextualReflection();
        AssemblyLoadContext.Default.Resolving += Resolve;
        if (executionContext != AssemblyLoadContext.Default) executionContext.Resolving += Resolve;
        try
        {
            if (captureOutput)
            {
                Console.SetOut(stdout!);
                Console.SetError(stderr!);
            }
            if (stdin is not null)
            {
                Console.SetIn(stdin);
            }
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(package.Culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(package.UICulture);
            foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().ToArray())
            {
                if (!package.Environment.ContainsKey(key))
                {
                    Environment.SetEnvironmentVariable(key, null);
                }
            }

            foreach (var pair in package.Environment)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }

            foreach (var file in package.Files.OrderBy(file => file.LinkTarget is not null))
            {
                var root = Path.GetFullPath(Environment.CurrentDirectory) + Path.DirectorySeparatorChar;
                var path = Path.GetFullPath(file.Path, root);
                var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var rootEntry = file.Path == "." && file.IsDirectory && file.LinkTarget is null;
                if (!path.StartsWith(root, pathComparison) && !(rootEntry && string.Equals(path, root[..^1], pathComparison)))
                {
                    throw new ReplException("a comparison fixture path escapes its working directory");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (file.LinkTarget is { } target)
                {
                    var destination = Path.GetFullPath(target, Path.GetDirectoryName(path)!);
                    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    if (!destination.StartsWith(root, comparison) && !string.Equals(destination, root[..^1], comparison))
                    {
                        throw new ReplException("a comparison fixture link escapes its working directory");
                    }

                    if (file.IsDirectory)
                    {
                        Directory.CreateSymbolicLink(path, target);
                    }
                    else
                    {
                        File.CreateSymbolicLink(path, target);
                    }
                }
                else if (file.IsDirectory)
                {
                    Directory.CreateDirectory(path);
                }
                else
                {
                    File.WriteAllBytes(path, file.Contents);
                }
            }

            RestoreFixtureMetadata(package.Files, restoreFileTimes);
            ComparisonProbe.Initialize(image.TypeNames);
            ready();
            if (image.OriginalAssembly is { } identity)
            {
                foreach (var satellite in package.Dependencies)
                {
                    var name = new AssemblyName(satellite.Name);
                    if (!string.IsNullOrEmpty(name.CultureName) && name.Name?.EndsWith(".resources", StringComparison.Ordinal) == true)
                        Resolve(executionContext, name);
                }
                foreach (var parent in package.Dependencies)
                {
                    if (parent.OriginalSatelliteFiles is not { } paths) continue;
                    var current = ComparisonSatelliteFiles.Paths(parent);
                    var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                    if (!paths.SequenceEqual(current, comparer))
                        throw new ReplException("the original assembly's satellite files changed after comparison capture: "
                            + parent.OriginalLocation);
                }
                var dependency = package.Dependencies.FirstOrDefault(dependency =>
                    string.Equals(dependency.Name, identity, StringComparison.OrdinalIgnoreCase));
                if (dependency?.OriginalLocation is { } location) VerifyOriginalFile(dependency, location);
                var originalAssembly = executionContext.LoadFromAssemblyName(new AssemblyName(identity));
                if (image.OriginalModule is { } module && originalAssembly.ManifestModule.ModuleVersionId != module)
                {
                    throw new ReplException("the original assembly no longer matches the captured module");
                }
            }

            using var code = new MemoryStream(image.Image, writable: false);
            var assembly = executionContext.LoadFromStream(code);
            var type = assembly.GetType(image.EntryType, throwOnError: true)!;
            Type Argument(string name) => Type.GetType(name, throwOnError: true)!;
            if (image.TypeArguments.Count != 0)
            {
                type = type.MakeGenericType(image.TypeArguments.Select(Argument).ToArray());
            }

            var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Single(method => image.EntryToken == 0 ? method.Name == image.EntryMethod : method.MetadataToken == image.EntryToken);
            if (image.MethodArguments.Count != 0)
            {
                method = method.MakeGenericMethod(image.MethodArguments.Select(Argument).ToArray());
            }

            var parameters = method.GetParameters();
            var arguments = parameters.Select((parameter, index) => ValueLiteralParser.Parse(image.Arguments[index],
                parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()! : parameter.ParameterType)).ToArray();
            object? result = null;
            object? returned = null;
            Exception? failure = null;
            try
            {
                returned = method.Invoke(null, arguments);
                result = returned is null && typeof(Task).IsAssignableFrom(method.ReturnType)
                    ? ComparisonProbe.NullTask() : await AsyncObservation.AwaitAsync(returned).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            }

            ComparisonProbe.CompleteValueTask(returned, result, failure);
            var invocations = await ComparisonProbe.CompleteAsync().ConfigureAwait(false);
            var observation = new StructuralObservation(image.TypeNames);
            return new ComparisonSide(exceeded ? "output-limit" : invocations.Count == 0 ? "setup-failed" : "completed", invocations,
                observation.Capture(result), failure is null ? null : observation.Exception(failure),
                stdout?.Text ?? "", stderr?.Text ?? "",
                invocations.Count == 0 ? "the scenario did not invoke the selected method" : null);
        }
        catch (Exception ex)
        {
            var observation = new StructuralObservation(image.TypeNames);
            return new ComparisonSide(exceeded ? "output-limit" : "setup-failed", [], null, observation.Exception(ex), stdout?.Text ?? "",
                stderr?.Text ?? "", ex.Message);
        }
        finally
        {
            AssemblyLoadContext.Default.Resolving -= Resolve;
            if (executionContext != AssemblyLoadContext.Default)
            {
                executionContext.Resolving -= Resolve;
                executionContext.Unload();
            }
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Console.SetIn(previousInput);
        }
    }
}
