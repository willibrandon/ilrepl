using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Runtime-only sibling names receive complete source context when concrete target discovery cannot determine their values.
/// </summary>
public sealed partial class SiblingTypeLookupTests
{
    /// <summary>
    /// Unknown parameters resolve each separate sibling and compound type without introducing target tokens into the selected body.
    /// </summary>
    /// <returns>The completed actual runtime, worker, and standalone export assertions.</returns>
    [TestMethod]
    public async Task Edit_RuntimeSiblingNamesPreserveCompleteSourceContext()
    {
        var session = new Session();
        var assembly = session.Resolver.LoadImage(SiblingTypeLookupFixture.Create("type", "plain", 1, flow: "runtime"));
        AssertNoSiblingTokens(assembly.GetType("Lookup.Owner")!, session);
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]Lookup.Owner::Read(string)", "Copy");
        Assert.DoesNotContain("Lookup.Sibling", edit.Source);
        Assert.DoesNotContain("Lookup.GenericSibling", edit.Source);
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        foreach (var name in new[] { "Lookup.Sibling", "Lookup.Sibling+Nested",
            "Lookup.GenericSibling`1[[" + typeof(int).AssemblyQualifiedName + "]]" })
        {
            Assert.AreEqual(42, edit.Original.Requested.Invoke(null, [name]));
            Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, [name]));
            Assert.AreEqual(42, edit.Method!.Invoke(null, [name]));
            var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session,
                "Copy (" + LiteralParser.Escape(name) + ")"), TestContext.CancellationToken);
            Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
            Assert.AreEqual("completed", result.Original.Outcome);
            Assert.AreEqual("completed", result.Edited.Outcome);
            Assert.AreEqual("42", result.Original.Result!.Value);
            Assert.AreEqual("42", result.Edited.Result!.Value);
        }

        var source = edit.Source;
        session.CommitEdit(edit.Name, source.Insert(source.LastIndexOf("ret", StringComparison.Ordinal), "ldc.i4.1\nadd\n"));
        const string sibling = "Lookup.Sibling";
        var changed = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy (\"" + sibling + "\")"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", changed.Outcome, changed.Original.Detail + "; " + changed.Edited.Detail);
        Assert.AreEqual("completed", changed.Original.Outcome);
        Assert.AreEqual("completed", changed.Edited.Outcome);
        Assert.AreEqual("42", changed.Original.Result!.Value);
        Assert.AreEqual("43", changed.Edited.Result!.Value);
        session.AddLine("ldstr \"" + sibling + "\"");
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "sibling-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            AssertExport(image);
        }
    }
}
