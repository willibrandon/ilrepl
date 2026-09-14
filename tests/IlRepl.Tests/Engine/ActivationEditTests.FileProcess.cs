using System.Diagnostics;
using System.Globalization;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// File activation runs in a separate process so Windows can release loaded assembly files before cleanup.
/// </summary>
public sealed partial class ActivationEditTests
{
    private const string FileProbePath = "ILREPL_ACTIVATION_PROBE_PATH";
    private const string FileProbeOverload = "ILREPL_ACTIVATION_PROBE_OVERLOAD";
    private const string FileProbeNested = "ILREPL_ACTIVATION_PROBE_NESTED";

    private async Task RunFileActivationChild(string path, int overload, bool nested)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add("FullyQualifiedName~ActivationEditTests.RunFileActivationProbe");
        start.Environment[FileProbePath] = path;
        start.Environment[FileProbeOverload] = overload.ToString(CultureInfo.InvariantCulture);
        start.Environment[FileProbeNested] = nested.ToString();
        using var child = Process.Start(start) ?? throw new InvalidOperationException("the activation probe did not start");
        var output = child.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        var error = child.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        try
        {
            await child.WaitForExitAsync(TestContext.CancellationToken);
            Assert.AreEqual(0, child.ExitCode, await output + Environment.NewLine + await error);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Exercises file-based activation, comparison workers, and both export formats inside the fixture-owning process.
    /// </summary>
    [TestMethod]
    public async Task RunFileActivationProbe()
    {
        var fixturePath = Environment.GetEnvironmentVariable(FileProbePath);
        TestSkip.Unless(fixturePath is not null, "runs as a child of Compare_FileActivationUsesCopiedTypes");
        var path = fixturePath!;
        var overload = int.Parse(Environment.GetEnvironmentVariable(FileProbeOverload)!, CultureInfo.InvariantCulture);
        var nested = bool.Parse(Environment.GetEnvironmentVariable(FileProbeNested)!);
        var session = new Session();
        var assembly = session.Resolver.Load(path);
        var name = ActivationExamples.Name(nested, overload == 8);
        Assert.AreEqual(42, assembly.GetType("Activation.Owner")!.GetMethod("Read")!.Invoke(null, [path, name]));
        var edit = session.PrepareEdit("int32 Activation.Owner::Read(string, string)", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, [path, name]));
        var arguments = "(" + LiteralParser.Escape(path) + ", " + LiteralParser.Escape(name) + ")";
        var unchanged = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy " + arguments),
            TestContext.CancellationToken);
        Assert.AreEqual("match", unchanged.Outcome, unchanged.Original.Detail + "; " + unchanged.Edited.Detail);
        Assert.AreEqual("42", unchanged.Original.Result!.Value);
        Assert.AreEqual("42", unchanged.Edited.Result!.Value);
        session.CommitEdit(edit.Name, ActivationExamples.Method(overload, nested, true, true));
        var changed = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy " + arguments),
            TestContext.CancellationToken);
        Assert.AreEqual("different", changed.Outcome, changed.Original.Detail + "; " + changed.Edited.Detail);
        Assert.AreEqual("42", changed.Original.Result!.Value);
        Assert.AreEqual("43", changed.Edited.Result!.Value);
        session.AddLine("ldstr " + LiteralParser.Escape(path));
        session.AddLine("ldstr " + LiteralParser.Escape(name));
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "file-activation"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("file-activation", isCollectible: true);
            try
            {
                var saved = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(43, saved.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }
}
