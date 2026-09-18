using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Executes one exported method on an isolated eight-mebibyte execution stack in a fresh test process.
/// </summary>
internal static class ExportProbe
{
    /// <summary>
    /// Handles the private export probe mode before the test platform initializes.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>Whether this process was an export probe.</returns>
    internal static async Task<bool> TryRunAsync(string[] args)
    {
        if (args is ["--export-tool-output"])
        {
            await Console.Error.WriteAsync(new string('e', 128 * 1024));
            await Console.Out.WriteAsync(new string('o', 128 * 1024));
            return true;
        }
        if (args is ["--export-tool-wait", var signal])
        {
            await File.WriteAllTextAsync(signal + ".pending", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            File.Move(signal + ".pending", signal);
            await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            return true;
        }
        if (args.Length != 3 || args[0] != "--export-probe") return false;
        var request = JsonSerializer.Deserialize(await File.ReadAllTextAsync(args[1]), ExportJsonContext.Default.ExportRequest)
            ?? throw new InvalidDataException("The export request is missing.");
        if (!request.EndOfInput) throw new InvalidDataException("Export comparison requires an explicit end of input.");
        var completion = new TaskCompletionSource<ExportObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Invoke(request));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }, request.StackSize);
        thread.Start();
        var result = await completion.Task;
        await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(result, ExportJsonContext.Default.ExportObservation));
        return true;
    }

    private static ExportObservation Invoke(ExportRequest request)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(request.Culture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(request.UICulture);
        using var input = new StringReader(Encoding.GetEncoding(request.InputEncoding).GetString(request.StandardInput));
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var originalInput = Console.In;
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        Console.SetIn(input);
        Console.SetOut(TextWriter.Synchronized(output));
        Console.SetError(TextWriter.Synchronized(error));
        try
        {
            var context = new AssemblyLoadContext("export probe", isCollectible: false);
            context.Resolving += (_, name) =>
            {
                var path = request.Dependencies.SingleOrDefault(candidate =>
                    AssemblyName.GetAssemblyName(candidate).Name == name.Name);
                return path is null ? null : context.LoadFromAssemblyPath(path);
            };
            var assembly = context.LoadFromAssemblyPath(request.ImagePath);
            var method = assembly.GetType(request.Type, throwOnError: true)!
                .GetMethod(request.Method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(request.Type, request.Method);
            if (method.IsGenericMethodDefinition)
            {
                method = method.MakeGenericMethod(request.GenericArguments
                    .Select(name => Type.GetType(name, throwOnError: true)!).ToArray());
            }

            object? result = null;
            string? exceptionType = null;
            try
            {
                result = method.Invoke(null, request.Arguments.Select(argument => argument.Materialize()).ToArray());
            }
            catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
            {
                exceptionType = inner.GetType().FullName;
            }

            return new ExportObservation(ExportValue.From(result), exceptionType, output.ToString(), error.ToString(),
                Environment.Version.ToString(), request.Profile);
        }
        finally
        {
            Console.SetIn(originalInput);
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }
}
