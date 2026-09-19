using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Missing dependency diagnostics identify the recovery workflow available in the current execution environment.
/// </summary>
[TestClass]
public sealed class SessionDependencyDiagnosticTests
{
    /// <summary>
    /// Supplies cancellation for real in-process engine requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Loaded name-only framework references remain available with case differences or partial assembly display identities.
    /// </summary>
    /// <param name="request">The valid assembly request supplied to the runtime binder.</param>
    [TestMethod]
    [DataRow("system.runtime")]
    [DataRow("System.Runtime, Culture=neutral")]
    public void ReferenceStatus_RecognizesLoadedAssemblyIdentity(string request)
    {
        using var core = new ReplCore();
        Assert.IsTrue(core.Handle(".load \"" + request + "\"").Succeeded);
        var document = core.CaptureSession(new SessionEditor());
        var reference = Assert.ContainsSingle(document.References);

        Assert.AreEqual("available", core.ReferenceStatus(reference));

        Assert.AreEqual(request, reference.Request);
        Assert.IsEmpty(reference.Assets);
        using var reopened = new ReplCore();
        Assert.IsEmpty(reopened.ReopenSession(document));
        Assert.AreEqual("available", reopened.ReferenceStatus(reference));
    }

    /// <summary>
    /// Opening, explicit execution, and staged adoption preserve environment-specific guidance for each missing asset path.
    /// </summary>
    /// <param name="kind">The unavailable dependency asset or manifest shape.</param>
    /// <param name="canRestore">Whether the host provides desktop dependency restoration.</param>
    [TestMethod]
    [DataRow("managed", false)]
    [DataRow("managed", true)]
    [DataRow("satellite", false)]
    [DataRow("satellite", true)]
    [DataRow("native", false)]
    [DataRow("native", true)]
    [DataRow("empty-project", false)]
    [DataRow("empty-project", true)]
    [DataRow("assembly-name", false)]
    [DataRow("assembly-name", true)]
    [DataRow("changed", false)]
    [DataRow("changed", true)]
    public async Task MissingReference_PreservesRecoveryGuidanceAcrossSessionPaths(string kind, bool canRestore)
    {
        using var files = new SessionDependencyFixture();
        var changedPath = Path.Combine(files.DirectoryPath, "changed.dll");
        File.WriteAllBytes(changedPath, [1]);
        var reference = new SessionReference
        {
            Origin = kind == "assembly-name" ? "assembly" : "project",
            Request = kind == "assembly-name" ? files.AssemblyName : "Unavailable.csproj",
            Assets = kind is "empty-project" or "assembly-name" ? [] :
            [
                new SessionReferenceAsset
                {
                    Name = kind == "native" ? "unavailable-native" : files.AssemblyName,
                    Hash = SessionCodec.Hash([2]),
                    Kind = kind == "changed" ? "managed" : kind,
                    Path = kind == "changed" ? changedPath : null,
                },
            ],
        };
        var document = new SessionDocument
        {
            References = [reference],
            Entries = [new SessionEntry { Kind = SessionEntryKind.Reference, Reference = reference.Identity,
                Source = [".load " + reference.Request] }],
        };
        var options = new ReplOptions { SupportsDependencyRestore = canRestore };
        using var core = new ReplCore(new Session(), options);
        await using var engine = new InProcessEngine(core);

        var opened = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Hydrate }, Document = document,
        }, TestContext.CancellationToken);

        var diagnostic = Assert.ContainsSingle(opened.Diagnostics);
        var detail = kind switch
        {
            "empty-project" => "has no verified assets",
            "assembly-name" => "could not load",
            "native" => "is unavailable on this runtime",
            "changed" => "changed",
            _ => "is missing",
        };
        Assert.Contains(detail, diagnostic);
        if (canRestore)
        {
            Assert.Contains(kind == "changed" ? "use .load to adopt its new contents" : "use .session restore", diagnostic);
            Assert.DoesNotContain("this demo", diagnostic);
        }
        else
        {
            Assert.Contains("terminal ilrepl", diagnostic);
            Assert.DoesNotContain("use .session restore", diagnostic);
            Assert.DoesNotContain("use .load", diagnostic);
            if (kind == "native")
            {
                Assert.Contains("run this experiment in terminal ilrepl", diagnostic);
            }
            else
            {
                Assert.Contains("save with .session save --embed, then open that file in this demo", diagnostic);
            }
        }

        Assert.Contains(line => line.Kind == LineKind.Error && line.PlainText == "  " + diagnostic, opened.Reply.Lines);
        Assert.AreSequenceEqual(SessionCodec.Write(document), SessionCodec.Write(opened.Document));
        var status = core.ReferenceStatus(reference);
        Assert.Contains(!canRestore ? "terminal ilrepl" : "use .session restore", status);
        Assert.DoesNotContain("available", status);
        using var replay = new ReplCore(new Session(), options);
        var runError = Assert.ThrowsExactly<ReplException>(() => replay.RunSession(document, [], TestContext.CancellationToken));
        Assert.Contains(diagnostic, runError.Message);
        Assert.IsEmpty(replay.Transcript.Lines);
        using var candidate = new ReplCore(new Session(), options);
        var adoptionError = Assert.ThrowsExactly<ReplException>(() => candidate.AdoptReferences(document));
        Assert.AreEqual(diagnostic, adoptionError.Message);
        Assert.IsEmpty(candidate.CaptureSession(new SessionEditor()).References);
    }
}
