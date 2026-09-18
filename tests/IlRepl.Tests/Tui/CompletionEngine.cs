using System.Collections.Concurrent;
using System.Runtime.Loader;
using IlRepl.Protocol;
using IlRepl.Repl;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Gates delivery of real editing results without synthesizing candidate pages or assembly observations.
/// </summary>
internal sealed class CompletionEngine : IReplEngine
{
    private readonly InProcessEngine _inner = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private readonly Lock _callsLock = new();
    private readonly List<HeldCompletion> _calls = [];

    /// <summary>
    /// Every real completion call in arrival order.
    /// </summary>
    public IReadOnlyList<HeldCompletion> Calls
    {
        get { lock (_callsLock) { return _calls.ToArray(); } }
    }

    /// <summary>
    /// Whether prepared completion replies remain held until the test releases them.
    /// </summary>
    public bool HoldCompletion { get; set; } = true;

    /// <summary>
    /// Whether prepared analysis replies remain held until the test releases them.
    /// </summary>
    public bool HoldAnalysis { get; set; }

    /// <summary>
    /// Every real analysis call in arrival order.
    /// </summary>
    public ConcurrentQueue<HeldAnalysis> Analyses { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<CompletionItem> Catalog => _inner.Catalog;

    /// <inheritdoc />
    public CilVocabulary Vocabulary => _inner.Vocabulary;

    /// <inheritdoc />
    public SessionStatus Status => _inner.Status;

    /// <inheritdoc />
    public long AssemblyVersion => _inner.AssemblyVersion;

    /// <inheritdoc />
    public Task<long> WaitForAssembliesAsync(long version, CancellationToken cancellationToken) =>
        _inner.WaitForAssembliesAsync(version, cancellationToken);

    /// <summary>
    /// Establishes real catalog and analysis metadata before a test begins observing request delivery.
    /// </summary>
    public async Task PrimeAsync(CancellationToken cancellationToken)
    {
        long before;
        do
        {
            before = AssemblyVersion;
            await _inner.CompleteAsync(new CompletionRequest(["call Console::Wr"], 0, 16, null, []), cancellationToken);
            await _inner.AnalyzeAsync(new AnalysisRequest(["ldc.i4.1", "ret"], 1, 0, 0), cancellationToken);
        }
        while (before != AssemblyVersion);
    }

    /// <summary>
    /// Loads a generated assembly without changing accepted session source or its semantic revision.
    /// </summary>
    public static void ChangeAssemblies()
    {
        var name = "EditingLoad" + Guid.NewGuid().ToString("N");
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll);
        assembly.MainModule.Types.Add(new TypeDefinition(name, "LoadedType", TypeAttributes.Public, assembly.MainModule.TypeSystem.Object));
        using var image = new MemoryStream();
        assembly.Write(image);
        image.Position = 0;
        var context = new AssemblyLoadContext(name);
        context.LoadFromStream(image);
    }

    /// <summary>
    /// Loads a real method whose distinct parameter types span more detail rows than the terminal can display at once.
    /// </summary>
    public static void LoadLongSignature()
    {
        const string name = "LongSignatureFixture";
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll);
        var module = assembly.MainModule;
        var owner = new TypeDefinition(name, "Methods", TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(owner);
        var method = new MethodDefinition("Use", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        owner.Methods.Add(method);
        for (var index = 0; index < 80; index++)
        {
            var type = new TypeDefinition(name, "SignaturePart" + index.ToString("D2"), TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(type);
            method.Parameters.Add(new ParameterDefinition("argument" + index, ParameterAttributes.None, type));
        }
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        using var image = new MemoryStream();
        assembly.Write(image);
        image.Position = 0;
        var context = new AssemblyLoadContext(name);
        context.LoadFromStream(image);
    }

    /// <inheritdoc />
    public Task<AnalysisReply> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        var prepared = _inner.AnalyzeAsync(request, _lifetime.Token);
        var call = new HeldAnalysis(request, prepared, cancellationToken);
        Analyses.Enqueue(call);
        if (!HoldAnalysis) call.Release.TrySetResult();
        return DeliverAsync(prepared, call.Release.Task);
    }

    /// <inheritdoc />
    public Task<CompletionReply> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        var prepared = _inner.CompleteAsync(request, _lifetime.Token);
        var call = new HeldCompletion(request, prepared, cancellationToken);
        lock (_callsLock) { _calls.Add(call); }
        if (!HoldCompletion) call.Release.TrySetResult();
        return DeliverAsync(prepared, call.Release.Task);
    }

    private async Task<T> DeliverAsync<T>(Task<T> prepared, Task release)
    {
        var cancellationToken = _lifetime.Token;
        var reply = await prepared.ConfigureAwait(false);
        await release.WaitAsync(cancellationToken).ConfigureAwait(false);
        return reply;
    }

    /// <inheritdoc />
    public Task<HandleReply> HandleAsync(string line, CancellationToken cancellationToken) => _inner.HandleAsync(line, cancellationToken);

    /// <inheritdoc />
    public Task<HandleReply> RollbackAsync(SessionMark mark, CancellationToken cancellationToken) =>
        _inner.RollbackAsync(mark, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync();
        foreach (var call in Calls) call.Release.TrySetCanceled();
        foreach (var call in Analyses) call.Release.TrySetCanceled();
        await _inner.DisposeAsync();
        _lifetime.Dispose();
    }
}
