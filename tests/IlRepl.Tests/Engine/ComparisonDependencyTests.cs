using IlRepl.Engine;
using IlRepl.Host;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Comparison packages retain available libraries without requiring unused optional references to be installed.
/// </summary>
[TestClass]
public sealed class ComparisonDependencyTests
{
    private static readonly string[] EntryNames = ["Read", "Optional"];

    /// <summary>
    /// Supplies cancellation for actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// An absent optional library does not block capture, while an available transitive library reaches both workers.
    /// </summary>
    /// <param name="useMissing">Whether the chosen path actually calls the missing optional library.</param>
    /// <returns>The completed package and worker assertions.</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Create_MissingTransitiveReference_IsResolvedOnlyWhenNeeded(bool useMissing)
    {
        var session = new Session();
        var (leaf, leafImage, leafType) = CecilFixture.Build((module, owner) =>
        {
            var read = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            owner.Methods.Add(read);
            read.Body.GetILProcessor().Emit(OpCodes.Ldc_I4, 41);
            read.Body.GetILProcessor().Emit(OpCodes.Ret);
        }, session.Resolver);

        var missingName = "IlReplMissingOptional" + Guid.NewGuid().ToString("N");
        var (library, libraryImage, libraryType) = CecilFixture.Build((module, owner) =>
        {
            var missing = new AssemblyNameReference(missingName, new Version(1, 0, 0, 0));
            module.AssemblyReferences.Add(missing);
            var missingType = new TypeReference("N", "Optional", module, missing);
            var missingMethod = new MethodReference("Read", module.TypeSystem.Int32, missingType);
            foreach (var name in EntryNames)
            {
                var read = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
                owner.Methods.Add(read);
                read.Body.GetILProcessor().Emit(OpCodes.Call,
                    name == "Read" ? module.ImportReference(leafType.GetMethod("Read")!) : missingMethod);
                read.Body.GetILProcessor().Emit(OpCodes.Ret);
            }
        }, session.Resolver);

        Assert.AreEqual(41, libraryType.GetMethod("Read")!.Invoke(null, null));
        var target = useMissing ? "Optional" : "Read";
        foreach (var line in IlLines.Expand(".method int32 Work() {",
            "call int32 [" + library.GetName().Name + "]N.Fixture::" + target + "()", "ret", "}"))
        {
            session.AddLine(line);
        }

        var edit = session.PrepareEdit("Work", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));

        var package = ComparisonCapture.Create(session, "Copy ()");

        Assert.AreSequenceEqual(leafImage, package.Dependencies.Single(dependency => dependency.Name == leaf.FullName).Image);
        Assert.AreSequenceEqual(libraryImage, package.Dependencies.Single(dependency => dependency.Name == library.FullName).Image);
        Assert.DoesNotContain(dependency => dependency.Name.StartsWith(missingName, StringComparison.Ordinal), package.Dependencies);
        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
        Assert.AreEqual(useMissing ? "match" : "different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.HasCount(1, side.Invocations);
            if (useMissing)
            {
                Assert.EndsWith("System.IO.FileNotFoundException", side.Exception!.Type);
                Assert.Contains(missingName, side.Exception.Message!);
            }
            else
            {
                Assert.IsNull(side.Exception);
                Assert.AreEqual(side == result.Original ? "41" : "42", side.Result!.Value);
            }
        }
    }
}
