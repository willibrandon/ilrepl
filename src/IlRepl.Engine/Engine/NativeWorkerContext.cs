using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Reconstructs and prepares a native target without executing its body before an explicit workload.
/// </summary>
public sealed class NativeWorkerContext : IDisposable
{
    private readonly AssemblyLifetimeScope _lifetime;
    private readonly NativeLoadContext? _context;
    private readonly NativeOptions _options;
    private readonly NativeTarget _target;
    private readonly CompiledCell? _cell;
    private bool _activated;
    private readonly string _nativeDirectory;

    /// <summary>
    /// Loads original images or re-emits a captured cell using the normal compiler.
    /// </summary>
    /// <param name="target">The immutable implementation and declaration context.</param>
    /// <param name="options">The compilation and execution settings.</param>
    /// <param name="nativeDirectory">The optional directory owned by the supervising worker process.</param>
    public NativeWorkerContext(NativeTarget target, NativeOptions options, string? nativeDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        _nativeDirectory = nativeDirectory ?? Path.Combine(Path.GetTempPath(), "ilrepl-native-assets-" + Guid.NewGuid().ToString("N"));
        _target = target;
        _options = options;
        Session = new Session { DeferActivation = true };
        _lifetime = new AssemblyLifetimeScope(options.Collectible);
        try
        {
            _context = new NativeLoadContext(target, Session.Resolver, options.Collectible, _nativeDirectory);
            foreach (var image in target.Assemblies)
            {
                _context.LoadFromAssemblyName(new AssemblyName(image.Name));
            }

            Session.RestoreNative(target, _context);
            if (target.Method is { } identity)
            {
                Method = _context.ResolveMethod(identity);
            }
            else
            {
                _cell = CellCompiler.CompileForInspection(Session);
                Method = Close(_cell.Implementation!);
            }

            InvocationMethod = target.Scenario is { } scenario ? _context.ResolveMethod(scenario)
                : _cell is null ? Method : Close(_cell.EntryPoint);
            if (Method.ContainsGenericParameters)
            {
                throw new ReplException("native inspection requires closed generic arguments");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// The worker's metadata-only session context.
    /// </summary>
    public Session Session { get; }

    /// <summary>
    /// The actual implementation body selected for disassembly.
    /// </summary>
    public MethodBase Method { get; }

    /// <summary>
    /// The versionable entry point invoked by the explicit workload.
    /// </summary>
    public MethodBase InvocationMethod { get; }

    /// <summary>
    /// The materialized explicit arguments, populated only at the execution boundary.
    /// </summary>
    public object?[] Arguments { get; private set; } = [];

    /// <summary>
    /// Checks transitive module activation before asking CoreCLR to compile the original method.
    /// </summary>
    public void Prepare()
    {
        RequireActivationPermission();
        var handles = Method.IsGenericMethod ? Method.GetGenericArguments().Select(type => type.TypeHandle).ToArray() : [];
        if (Method.DeclaringType is { IsConstructedGenericType: true } owner)
        {
            handles = [.. owner.GetGenericArguments().Select(type => type.TypeHandle), .. handles];
        }

        RuntimeHelpers.PrepareMethod(Method.MethodHandle, handles);
    }

    /// <summary>
    /// Invokes the authorized workload and awaits ordinary Task or ValueTask results.
    /// </summary>
    /// <returns>A task completing when that invocation has finished.</returns>
    public async Task InvokeAsync()
    {
        if (!_options.Run)
        {
            throw new ReplException("native body execution requires literals, using Scenario, or --run");
        }

        if (!_activated)
        {
            Session.Activate();
            Session.ActivateNativeBindings();
            Arguments = _target.Scenario is not null ? [] : _cell is not null
                ? [.. Session.Cell.Arguments.Select(argument => argument.ExecutionValue())]
                : [.. InvocationMethod.GetParameters().Select((parameter, index) => ValueLiteralParser.Parse(
                    _options.Arguments![index], parameter.ParameterType.IsByRef
                        ? parameter.ParameterType.GetElementType()! : parameter.ParameterType))];
            _activated = true;
        }

        var value = InvocationMethod.Invoke(null, Arguments);
        if (value is Task task)
        {
            await task.ConfigureAwait(false);
        }
        else if (value is ValueTask valueTask)
        {
            await valueTask.ConfigureAwait(false);
        }
        else if (value is not null && value.GetType().IsGenericType
            && value.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            await ((Task)value.GetType().GetMethod("AsTask")!.Invoke(value, null)!).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rejects activation of reachable module initializers unless the request explicitly allows it.
    /// </summary>
    public void RequireActivationPermission()
    {
        if (_options.Run || _options.AllowInitializers)
        {
            return;
        }

        var bindings = _target.Bindings.ToDictionary(binding => _context!.ResolveMethod(binding.Trampoline),
            binding => _context!.ResolveMethod(binding.Implementation));
        var reached = NativeActivationGraph.Assemblies(Method, bindings);
        foreach (var image in _target.Assemblies.Where(image => reached.Contains(image.Name)))
        {
            using var pe = new PEReader(new MemoryStream(image.Image, writable: false));
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                if (reader.GetString(type.Name) != "<Module>")
                {
                    continue;
                }

                if (type.GetMethods().Any(method => reader.GetString(reader.GetMethodDefinition(method).Name) == ".cctor"))
                {
                    throw new ReplException($"module '{image.Name}' has an initializer; use --allow-initializers or an explicit workload");
                }
            }
        }
    }

    /// <summary>
    /// Releases collectible metadata and restores the caller's normal emission lifetime.
    /// </summary>
    public void Dispose()
    {
        _cell?.Release();
        Session.Resolver.Dispose();
        if (_context is { IsCollectible: true })
        {
            _context.Unload();
        }

        _lifetime.Dispose();
        try
        {
            if (Directory.Exists(_nativeDirectory))
            {
                Directory.Delete(_nativeDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // The supervisor retries cleanup after native libraries are released by process exit.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve inspection results when user code changed asset permissions.
        }
    }

    private MethodInfo Close(MethodInfo method) => method.IsGenericMethodDefinition
        ? method.MakeGenericMethod([.. Session.TypeArguments ?? []]) : method;
}
