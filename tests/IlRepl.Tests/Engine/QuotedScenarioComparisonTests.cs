using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Quoted scenario names retain their CIL spelling through command parsing and real process comparisons.
/// </summary>
[TestClass]
public sealed class QuotedScenarioComparisonTests
{
    /// <summary>
    /// Supplies cancellation for the comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Quoted scenarios remain callable by name and retain that name in standalone exports.
    /// </summary>
    /// <param name="identifier">The quoted CIL identifier.</param>
    [TestMethod]
    [DataRow("'My Scenario'")]
    [DataRow("'Scenario'")]
    [DataRow("'Owner\\'s Scenario'")]
    [DataRow("'Folder\\\\Scenario'")]
    [DataRow("'Tabbed\tScenario'")]
    [DataRow("'--assert scenario'")]
    public void Run_QuotedScenarioNamesSurviveExport(string identifier)
    {
        var session = IlLines.Load(".method int32 " + identifier + "() {", "ldc.i4.s 43", "ret", "}", "call " + identifier);
        Assert.AreEqual(43, session.Run().Value);
        session.AddLine("call " + identifier);
        foreach (var image in new[] { AssemblyExporter.Write(session, "quoted-scenarios"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("quoted-scenarios", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(43, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// Spaces, escaped characters, and quoted plain identifiers select the declared method before parsing options.
    /// </summary>
    /// <param name="identifier">The quoted CIL identifier.</param>
    /// <param name="name">The decoded metadata name.</param>
    /// <param name="options">Whether to supply execution options after the name.</param>
    [TestMethod]
    [DataRow("'My Scenario'", "My Scenario", false)]
    [DataRow("'My Scenario'", "My Scenario", true)]
    [DataRow("'Scenario'", "Scenario", true)]
    [DataRow("'Owner\\'s Scenario'", "Owner's Scenario", true)]
    [DataRow("'Folder\\\\Scenario'", "Folder\\Scenario", true)]
    [DataRow("'Tabbed\tScenario'", "Tabbed\tScenario", true)]
    [DataRow("'--assert scenario'", "--assert scenario", true)]
    public async Task Compare_QuotedScenarioRunsWithItsOptions(string identifier, string name, bool options)
    {
        var session = IlLines.Load(".method int32 Read(int32 value) { ldarg.0; ret }");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
        foreach (var line in IlLines.Expand(".method int32 " + identifier + "() {",
            options ? "call string Console::ReadLine()" : "ldstr \"42\"", "call int32 Int32::Parse(string)", "call Copy", "ret", "}"))
        {
            session.AddLine(line);
        }

        var package = ComparisonCapture.Create(session, "Copy using " + identifier
            + (options ? " --stdin \"42\\n\" --timeout 10s --assert" : ""));
        Assert.AreEqual(name, package.Original.EntryMethod);
        Assert.AreEqual(name, package.Edited.EntryMethod);
        Assert.AreEqual(options, package.Assert);
        Assert.AreEqual(options ? "42\n" : "", package.StandardInput);
        Assert.AreEqual(options ? 10000 : 30000, package.TimeoutMilliseconds);
        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("42", result.Original.Result!.Value);
        Assert.AreEqual("43", result.Edited.Result!.Value);
        Assert.HasCount(1, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);
    }

    /// <summary>
    /// Missing closing quotes are rejected before an option-like suffix can be interpreted as a separate token.
    /// </summary>
    /// <param name="identifier">The incomplete quoted identifier.</param>
    [TestMethod]
    [DataRow("'")]
    [DataRow("'Scenario")]
    [DataRow("'Scenario\\'")]
    [DataRow("'Scenario --assert")]
    public void Parse_UnterminatedScenarioQuoteIsRejected(string identifier)
    {
        var error = Assert.ThrowsExactly<ReplException>(() => ComparisonCommand.Parse("Copy using " + identifier));
        Assert.Contains("unterminated quoted", error.Message);
    }
}
