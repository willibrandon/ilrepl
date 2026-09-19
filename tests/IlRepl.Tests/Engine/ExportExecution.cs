using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using IlRepl.Processes;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Runs independently produced images with matching process inputs and restores each fixture's filesystem between executions.
/// </summary>
internal sealed class ExportExecution : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(AppContext.BaseDirectory, "artifacts", "export-conformance", Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _workingDirectory;
    private readonly Dictionary<string, string> _files;
    private bool _failed;

    /// <summary>
    /// Creates an isolated execution workspace with an optional initial filesystem fixture.
    /// </summary>
    /// <param name="files">Relative paths and their initial text contents.</param>
    internal ExportExecution(IReadOnlyDictionary<string, string>? files = null)
    {
        _workingDirectory = Path.Combine(_directory, "work");
        _files = files is null ? [] : new Dictionary<string, string>(files);
        var tools = typeof(IlasmLocator).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Key is "IlasmPackagePath" or "IldasmPackagePath")
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        Record("tools.json", JsonSerializer.SerializeToUtf8Bytes(tools));
    }

    /// <summary>
    /// Retains source and images from preparation so a metadata or verifier failure remains reproducible.
    /// </summary>
    /// <param name="name">The fixture-local artifact basename.</param>
    /// <param name="contents">The exact generated artifact contents.</param>
    internal void Record(string name, byte[] contents) => File.WriteAllBytes(Path.Combine(_directory, name), contents);

    /// <summary>
    /// Runs one artifact in a fresh runtime with all input and compilation settings fixed before startup.
    /// </summary>
    /// <param name="image">The independently produced assembly image.</param>
    /// <param name="type">The entry method's declaring type.</param>
    /// <param name="method">The static entry method.</param>
    /// <param name="profile">Either deterministic or tiered runtime compilation.</param>
    /// <param name="arguments">The scalar method arguments.</param>
    /// <param name="genericArguments">The closed generic method arguments.</param>
    /// <param name="standardInput">The exact UTF-8 input text, followed by EOF.</param>
    /// <param name="environment">Fixture-specific environment values.</param>
    /// <param name="culture">The execution culture.</param>
    /// <param name="uiCulture">The resource lookup culture.</param>
    /// <param name="dependencies">Paths of dependency images.</param>
    /// <param name="cancellationToken">Cancels execution and cleans up the child process.</param>
    /// <returns>The actual typed result, exception, and console output.</returns>
    internal async Task<ExportObservation> RunAsync(
        byte[] image,
        string type,
        string method,
        string profile,
        object?[]? arguments = null,
        Type[]? genericArguments = null,
        string standardInput = "",
        IReadOnlyDictionary<string, string>? environment = null,
        string culture = "",
        string uiCulture = "",
        string[]? dependencies = null,
        CancellationToken cancellationToken = default)
    {
        ResetFiles();
        var identity = Guid.NewGuid().ToString("N");
        var imagePath = Path.Combine(_directory, identity + ".dll");
        var requestPath = Path.Combine(_directory, identity + ".request.json");
        var resultPath = Path.Combine(_directory, identity + ".result.json");
        var variables = CreateEnvironment(profile, environment);
        var request = new ExportRequest(imagePath, type, method, (arguments ?? []).Select(ExportValue.From).ToArray(),
            (genericArguments ?? []).Select(argument => argument.AssemblyQualifiedName!).ToArray(), culture, uiCulture,
            Encoding.UTF8.GetBytes(standardInput), "utf-8", variables, profile, _workingDirectory, dependencies ?? [],
            _files, 8 * 1024 * 1024, true);
        await File.WriteAllBytesAsync(imagePath, image, cancellationToken);
        await File.WriteAllTextAsync(requestPath,
            JsonSerializer.Serialize(request, ExportJsonContext.Default.ExportRequest), cancellationToken);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { WorkingDirectory = _workingDirectory };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(ExportProbe).Assembly.Location);
        }

        start.ArgumentList.Add("--export-probe");
        start.ArgumentList.Add(requestPath);
        start.ArgumentList.Add(resultPath);
        start.Environment.Clear();
        foreach (var (name, value) in variables)
        {
            start.Environment.Add(name, value);
        }

        try
        {
            var result = await ToolProcess.RunAsync(start, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(_directory, identity + ".tool.json"),
                JsonSerializer.Serialize(result), cancellationToken);
            Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError + "\nArtifacts: " + _directory);
            return JsonSerializer.Deserialize(await File.ReadAllTextAsync(resultPath, cancellationToken),
                ExportJsonContext.Default.ExportObservation) ?? throw new InvalidDataException("The probe result is missing.");
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    /// <summary>
    /// Constructs a fresh child environment with explicit runtime settings and no inherited tuning overrides.
    /// </summary>
    /// <param name="profile">The deterministic or tiered compilation profile.</param>
    /// <param name="overrides">Fixture variables applied before the declared runtime profile.</param>
    /// <returns>The complete process environment.</returns>
    internal Dictionary<string, string> CreateEnvironment(string profile, IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (profile is not ("deterministic" or "tiered"))
        {
            throw new ArgumentException("Unknown runtime profile.", nameof(profile));
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var variables = new Dictionary<string, string>(comparer);
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                variables[name] = value;
            }
        }

        var home = Directory.CreateDirectory(Path.Combine(_directory, "home")).FullName;
        var temporary = Directory.CreateDirectory(Path.Combine(_directory, "temporary")).FullName;
        variables["HOME"] = home;
        variables["USERPROFILE"] = home;
        variables["TMPDIR"] = temporary;
        variables["TMP"] = temporary;
        variables["TEMP"] = temporary;
        variables["DOTNET_ROOT"] = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));
        variables["DOTNET_NOLOGO"] = "1";
        if (overrides is not null)
        {
            foreach (var (name, value) in overrides)
            {
                if (name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Fixture variables cannot override the runtime compilation profile.", nameof(overrides));
                }

                variables[name] = value;
            }
        }

        var enabled = profile == "tiered" ? "1" : "0";
        foreach (var name in new[] { "TieredCompilation", "TieredPGO", "TC_QuickJit", "TC_QuickJitForLoops", "ReadyToRun" })
        {
            variables["DOTNET_" + name] = enabled;
        }

        return variables;
    }

    /// <summary>
    /// Checks the fixture through the real execution host with the same environment and runtime profile as exported images.
    /// </summary>
    /// <param name="example">The shared source and expected scalar result.</param>
    /// <param name="profile">The compilation profile applied before host startup.</param>
    /// <param name="cancellationToken">Cancels host startup, submission, and execution.</param>
    internal async Task RunLiveAsync(ControlFlowExample example, string profile, CancellationToken cancellationToken)
    {
        ResetFiles();
        var environment = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .ToDictionary(name => name, _ => (string?)null,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (name, value) in CreateEnvironment(profile))
        {
            environment[name] = value;
        }

        await using var host = await HostProcessEngine.StartAsync(null, _workingDirectory, environment, cancellationToken);
        var culture = "[System.Runtime]System.Globalization.CultureInfo";
        foreach (var line in new[]
        {
            ".quiet on",
            "call class " + culture + " " + culture + "::get_InvariantCulture()",
            "call void " + culture + "::set_CurrentCulture(class " + culture + ")",
            "call class " + culture + " " + culture + "::get_InvariantCulture()",
            "call void " + culture + "::set_CurrentUICulture(class " + culture + ")", ".run",
        }
            .Concat(example.Source.Split('\n')).Concat(["ldc.i4 " + example.Input, example.Call]))
        {
            var reply = await host.HandleAsync(line, cancellationToken);
            Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(output => output.PlainText)));
        }

        var result = await host.HandleAsync(".run", cancellationToken);
        Assert.IsTrue(result.Succeeded, string.Join('\n', result.Lines.Select(output => output.PlainText)));
        Assert.AreEqual("  = " + example.Expected + " : int32",
            Assert.ContainsSingle(result.Lines.Where(line => line.Kind == LineKind.Result)).PlainText);
        Assert.IsEmpty(result.Lines.Where(line => line.Kind == LineKind.Output));
    }

    /// <summary>
    /// Preserves the reproduction directory when an observable conformance assertion fails.
    /// </summary>
    internal void RetainArtifacts() => _failed = true;

    /// <summary>
    /// Removes successful fixtures after all their child processes have exited.
    /// </summary>
    public void Dispose()
    {
        if (_failed)
        {
            Console.Error.WriteLine("Export conformance artifacts: " + _directory);
        }
        else
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private void ResetFiles()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }

        Directory.CreateDirectory(_workingDirectory);
        foreach (var (path, content) in _files)
        {
            var destination = Path.GetFullPath(path, _workingDirectory);
            if (!destination.StartsWith(_workingDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new ArgumentException("Fixture paths must remain inside the working directory.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, content);
        }
    }
}
