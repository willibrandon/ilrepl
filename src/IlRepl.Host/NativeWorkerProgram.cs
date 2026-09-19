using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// Executes one native inspection in a disposable CoreCLR process with explicit execution boundaries.
/// </summary>
internal static class NativeWorkerProgram
{
    /// <summary>
    /// Runs the private file-based worker protocol after the diagnostics startup handshake.
    /// </summary>
    /// <param name="arguments">The package, side, and isolated control directory.</param>
    /// <returns>The worker exit code for malformed input.</returns>
    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length != 4)
        {
            return 64;
        }

        var root = arguments[3];
        OwnedProcessGroup.PrepareWorker();
        WorkerOwnerWatchdog.Start();
        await File.WriteAllTextAsync(Path.Combine(root, "group-ready"), "ready").ConfigureAwait(false);
        await WaitForAsync(Path.Combine(root, "start")).ConfigureAwait(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        Console.Write(NativeOutputBuffer.StartMarker(root));
        Console.Out.Flush();
        var package = JsonSerializer.Deserialize(await File.ReadAllTextAsync(arguments[1]).ConfigureAwait(false),
            ProtocolJsonContext.Default.NativePackage)!;
        var target = arguments[2] == "left" ? package.Left : package.Right!;
        var options = package.Options;
        var report = new NativeReport { Name = target.Name, Fingerprint = target.Fingerprint };
        var state = new NativeWorkerState { Report = report };
        try
        {
            report = Describe(target, options);
            state = state with { Report = report };
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(package.Culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(package.UICulture);
            ComparisonWorker.RestoreFixtures(package.Files);
            await File.WriteAllTextAsync(Path.Combine(root, "ready"), "ready").ConfigureAwait(false);
            if (options.Info)
            {
                var probe = CapabilityProbe();
                state = state with { MethodId = (ulong)probe.MethodHandle.Value, Method = NativeCapture.Identify(probe) };
                await NativeStateFile.WriteAsync(root, state).ConfigureAwait(false);
                RuntimeHelpers.PrepareMethod(probe.MethodHandle);
            }
            else
            {
                using var context = new NativeWorkerContext(target, options, Path.Combine(root, "native"));
                state = state with
                {
                    MethodId = (ulong)context.Method.MethodHandle.Value,
                    Method = NativeCapture.Identify(context.Method),
                    Report = report with
                    {
                        Implementation = NativeCapture.Identify(context.Method),
                        ModuleVersionId = context.Method.Module.ModuleVersionId,
                        Collectible = context.Method.Module.Assembly.IsCollectible,
                        Roles = ["implementation: " + MemberResolver.Describe(context.Method),
                            "invocation: " + MemberResolver.Describe(context.InvocationMethod)],
                    },
                };

                await NativeStateFile.WriteAsync(root, state).ConfigureAwait(false);
                context.Prepare();
                var elapsed = Stopwatch.StartNew();
                while (options.Run && state.Report.Invocations < options.Iterations && !File.Exists(Path.Combine(root, "stop")))
                {
                    state = state with { Report = state.Report with { Invocations = state.Report.Invocations + 1 } };
                    await NativeStateFile.WriteAsync(root, state).ConfigureAwait(false);
                    await context.InvokeAsync().ConfigureAwait(false);
                    if (options.Tier != "tier1")
                    {
                        continue;
                    }

                    // Maintain at most 100 driver calls per second while allowing runtime events to stop the workload.
                    var next = TimeSpan.FromMilliseconds(state.Report.Invocations * 10L) - elapsed.Elapsed;
                    if (next > TimeSpan.Zero)
                    {
                        await Task.Delay(next).ConfigureAwait(false);
                    }
                }

                using var evidence = new NativeAddressEvidence();
                var listings = await AvailableListingsAsync(root, state.Method.JitNames).ConfigureAwait(false);
                evidence.Collect(context, listings, report.Architecture);
                state = state with
                {
                    Probes = evidence.Probes,
                    Report = state.Report with
                    {
                        Addresses = evidence.Facts,
                        Constants = evidence.Constants,
                    },
                };

                await NativeStateFile.WriteAsync(root, state).ConfigureAwait(false);
            }

            state = state with { Report = state.Report with { Outcome = "complete" } };
        }
        catch (Exception exception)
        {
            var cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            state = state with { Report = state.Report with { Outcome = "failed", Detail = cause.GetType().Name + ": " + cause.Message } };
        }

        await NativeStateFile.WriteAsync(root, state).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(root, "work-done"), "done").ConfigureAwait(false);
        await WaitForAsync(Path.Combine(root, "release")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<NativeCompilation[]> AvailableListingsAsync(string root, string[] methodNames)
    {
        var path = Path.Combine(root, "native.txt");
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[(int)Math.Min(stream.Length, 16 * 1024 * 1024)];
        var count = await stream.ReadAtLeastAsync(bytes.AsMemory(), bytes.Length, throwOnEndOfStream: false).ConfigureAwait(false);
        return [.. NativeDisassembly.Parse(Encoding.UTF8.GetString(bytes, 0, count))
            .Where(listing => methodNames.Contains(listing.Method, StringComparer.Ordinal))];
    }

    private static async Task WaitForAsync(string path)
    {
        while (!File.Exists(path))
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static MethodInfo CapabilityProbe()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ilrepl.native.capability"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("capability");
        var type = module.DefineType("IlRepl.NativeCapability", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var method = type.DefineMethod("Probe", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);
        return type.CreateType()!.GetMethod("Probe")!;
    }

    private static NativeReport Describe(NativeTarget target, NativeOptions options)
    {
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(pair => (string)pair.Key, pair => (string)pair.Value!, StringComparer.Ordinal);
        var jit = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!,
            OperatingSystem.IsWindows() ? "clrjit.dll" : OperatingSystem.IsMacOS() ? "libclrjit.dylib" : "libclrjit.so");
        var intrinsics = typeof(Vector128).Assembly.GetTypes().Where(type => type.IsPublic && !type.ContainsGenericParameters &&
            type.Namespace is { } name
            && name.StartsWith("System.Runtime.Intrinsics", StringComparison.Ordinal)
            && type.GetProperty("IsSupported", BindingFlags.Static | BindingFlags.Public) is not null)
            .Where(type => (bool)type.GetProperty("IsSupported")!.GetValue(null)!).Select(type => type.FullName!).Order().ToArray();
        return new NativeReport
        {
            IsCapability = options.Info, Raw = options.Raw,
            Name = target.Name, Fingerprint = target.Fingerprint, Runtime = RuntimeInformation.FrameworkDescription
                + "; CoreCLR " + typeof(object).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Jit = Path.GetFileName(jit) + " SHA256 " + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(jit))),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), OperatingSystem = RuntimeInformation.OSDescription,
            RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            Authorization = options.Info ? "" : options.Run ? "explicit workload and required module initialization authorized"
                : options.AllowInitializers ? "module initialization authorized; target body not invoked"
                : "target body and user module initializers not authorized",
            InstructionSets = intrinsics, Settings = NativeRuntimeSettings.Describe(environment), Collectible = options.Collectible,
        };
    }
}
