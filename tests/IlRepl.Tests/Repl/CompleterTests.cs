using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Tests for <see cref="Completer"/> and the catalog it publishes.
/// </summary>
[TestClass]
public sealed class CompleterTests
{
    /// <summary>
    /// Opcode prefixes complete to opcode names with their stack transition.
    /// </summary>
    [TestMethod]
    public void Complete_OpcodePrefix_ReturnsOpcodes()
    {
        var items = Completer.Complete("ldc.i4.");
        Assert.IsNotEmpty(items);
        Assert.IsTrue(items.All(i => i.Name.StartsWith("ldc.i4.", StringComparison.Ordinal)));
        var s = items.Single(i => i.Name == "ldc.i4.s");
        Assert.IsTrue(s.TakesOperand);
        Assert.Contains("i", s.Detail);
    }

    /// <summary>
    /// Dot prefixes complete to commands.
    /// </summary>
    [TestMethod]
    public void Complete_DotPrefix_ReturnsCommands()
    {
        var items = Completer.Complete(".lo");
        Assert.Contains(i => i.Name == ".locals", items);
        Assert.Contains(i => i.Name == ".load", items);
    }

    /// <summary>
    /// The catalog holds every opcode except the reserved prefixes, and every command.
    /// </summary>
    [TestMethod]
    public void Catalog_CoversOpcodesAndCommands()
    {
        var catalog = Completer.Catalog;
        Assert.Contains(i => i.Name == "add", catalog);
        Assert.Contains(i => i.Name == "constrained.", catalog);
        Assert.DoesNotContain(i => i.Name.StartsWith("prefix", StringComparison.Ordinal), catalog);
        Assert.Contains(i => i.Name == ".help", catalog);
        Assert.Contains(i => i.Name == ".method" && i.TakesOperand, catalog);
        Assert.Contains(i => i.Name == ".methods" && !i.TakesOperand, catalog);
        Assert.IsGreaterThan(220, catalog.Count);
    }

    /// <summary>
    /// The shared prefix filter agrees with the engine completer.
    /// </summary>
    [TestMethod]
    public void CatalogCompleter_AgreesWithCompleter()
    {
        var fromCatalog = CatalogCompleter.Complete(Completer.Catalog, "conv.ovf").Select(i => i.Name).ToList();
        var fromEngine = Completer.Complete("conv.ovf").Select(i => i.Name).ToList();
        Assert.AreSequenceEqual(fromEngine, fromCatalog);
    }

    /// <summary>
    /// .me offers the directive first, then the listing command.
    /// </summary>
    [TestMethod]
    public void Complete_MethodPrefix_ReturnsDirectiveThenCommand()
    {
        var items = Completer.Complete(".me");
        Assert.AreSequenceEqual([".method", ".methods"], items.Select(i => i.Name));
        Assert.AreEqual("T Name(T arg, ...) {", items[0].Detail);
        Assert.IsTrue(items[0].TakesOperand);
    }
}
