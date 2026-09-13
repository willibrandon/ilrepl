using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ILVerify;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Resolves framework metadata for the independent .NET verifier without executing fixture methods.
/// </summary>
internal sealed class IlVerificationOracle : IResolver, IDisposable
{
    private readonly Dictionary<string, PEReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _framework = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .DistinctBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Verifies a fixture's methods and returns the verifier's original diagnostic codes.
    /// </summary>
    public IReadOnlyList<VerifierError> Verify(byte[] image)
    {
        var reader = new PEReader(new MemoryStream(image, writable: false));
        var metadata = reader.GetMetadataReader();
        var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        _readers.Add(name, reader);
        var verifier = new Verifier(this);
        verifier.SetSystemModuleName(new AssemblyNameInfo(typeof(object).Assembly.GetName().Name!));
        var results = Run(verifier, reader);
        if (results.FirstOrDefault(result => result.Code == VerifierError.None) is { } failure)
        {
            throw new InvalidOperationException("ILVerification could not finish the fixture: " + failure.Message);
        }

        return results.Select(result => result.Code).ToArray();
    }

    private static VerificationResult[] Run(Verifier verifier, PEReader reader)
    {
        try
        {
            return verifier.Verify(reader).ToArray();
        }
        catch (NullReferenceException exception)
        {
            throw new InvalidOperationException("ILVerification could not finish the fixture: " + exception.Message, exception);
        }
    }

    /// <inheritdoc/>
    public PEReader ResolveAssembly(AssemblyNameInfo assemblyName)
    {
        if (_readers.TryGetValue(assemblyName.Name, out var reader))
        {
            return reader;
        }

        if (!_framework.TryGetValue(assemblyName.Name, out var path))
        {
            throw new FileNotFoundException("The verifier could not resolve " + assemblyName.FullName);
        }

        reader = new PEReader(File.OpenRead(path));
        _readers.Add(assemblyName.Name, reader);
        return reader;
    }

    /// <inheritdoc/>
    public PEReader ResolveModule(AssemblyNameInfo referencingAssembly, string fileName) =>
        throw new NotSupportedException("The verification fixtures have one module.");

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var reader in _readers.Values)
        {
            reader.Dispose();
        }
    }
}
