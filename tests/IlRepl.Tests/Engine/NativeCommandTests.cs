using System.Globalization;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Verifies native selectors and explicit workloads follow the established comparison grammar.
/// </summary>
[TestClass]
public sealed class NativeCommandTests
{
    /// <summary>
    /// Bare inspection requests leave target selection to the session and never authorize execution implicitly.
    /// </summary>
    /// <param name="text">The optional whitespace around an empty selector.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow(" \t ")]
    public void Parse_BareInspectionUsesNonExecutingFullOptsDefaults(string text)
    {
        var options = NativeCommand.Parse(text);

        Assert.AreEqual("", options.Selector);
        Assert.IsNull(options.Against);
        Assert.AreEqual("fullopts", options.Tier);
        Assert.IsFalse(options.Run);
        Assert.IsFalse(options.AllowInitializers);
        Assert.IsFalse(options.Collectible);
        Assert.IsFalse(options.Raw);
        Assert.IsFalse(options.Assert);
        Assert.IsNull(options.Arguments);
        Assert.IsNull(options.Scenario);
        Assert.AreEqual(1, options.Iterations);
        Assert.AreEqual(30000, options.TimeoutMilliseconds);
        Assert.IsEmpty(options.Environment);
    }

    /// <summary>
    /// Typed generic signatures and quoted names remain selectors while the separate literal group authorizes execution.
    /// </summary>
    [TestMethod]
    public void Parse_ClosedGenericSignatureKeepsLiteralWorkloadSeparate()
    {
        const string left = "int32 [Fixture]Box`1<int32>::'Map (value)'<string>(int32, !!0)";
        const string right = "int32 [Other]Box`1<int32>::'Map (value)'<string>(int32, !!0)";

        var options = NativeCommand.Parse(left + " (42, \"hello, λ\") --against " + right + " --tier tier1 --assert");

        Assert.AreEqual(left, options.Selector);
        Assert.AreEqual(right, options.Against);
        Assert.AreSequenceEqual(["42", "\"hello, λ\""], options.Arguments!);
        Assert.IsNull(options.Scenario);
        Assert.IsTrue(options.Run);
        Assert.IsTrue(options.Assert);
        Assert.AreEqual("tier1", options.Tier);
        Assert.AreEqual(1000, options.Iterations);
        Assert.IsTrue(options.Pgo);
    }

    /// <summary>
    /// Method signature parentheses alone never authorize an invocation.
    /// </summary>
    [TestMethod]
    public void Parse_ParameterlessMethodSignatureDoesNotAuthorizeExecution()
    {
        var inspected = NativeCommand.Parse("int32 Owner::Value()");
        var executed = NativeCommand.Parse("int32 Owner::Value() ()");

        Assert.AreEqual("int32 Owner::Value()", inspected.Selector);
        Assert.IsFalse(inspected.Run);
        Assert.IsNull(inspected.Arguments);
        Assert.AreEqual(inspected.Selector, executed.Selector);
        Assert.IsTrue(executed.Run);
        Assert.IsNotNull(executed.Arguments);
        Assert.IsEmpty(executed.Arguments);
    }

    /// <summary>
    /// Scenarios, fixture paths, standard input, caps, and ISA overrides survive the same command without changing each other.
    /// </summary>
    [TestMethod]
    public void Parse_ScenarioPreservesWorkloadAndRuntimeOptions()
    {
        var options = NativeCommand.Parse("Copy using 'Scenario with spaces' --native --tier tier1 --pgo off "
            + "--iterations 321 --timeout 1.5s --stdin \"hello\\nλ\" --files \"fixture files\" "
            + "--env DOTNET_EnableAVX512F=0 --env \"APP_VALUE=two = three\" --raw --allow-initializers");

        Assert.AreEqual("Copy", options.Selector);
        Assert.AreEqual("Scenario with spaces", options.Scenario);
        Assert.IsNull(options.Arguments);
        Assert.IsTrue(options.Run);
        Assert.IsFalse(options.Pgo);
        Assert.AreEqual(321, options.Iterations);
        Assert.AreEqual(1500, options.TimeoutMilliseconds);
        Assert.AreEqual("hello\nλ", options.StandardInput);
        Assert.AreEqual("fixture files", options.FixtureDirectory);
        Assert.AreEqual("0", options.Environment["DOTNET_EnableAVX512F"]);
        Assert.AreEqual("two = three", options.Environment["APP_VALUE"]);
        Assert.IsTrue(options.Raw);
        Assert.IsTrue(options.AllowInitializers);
    }

    /// <summary>
    /// Explicit tier requests normalize the optimized alias while preserving their workload caps.
    /// </summary>
    /// <param name="tier">The user spelling.</param>
    /// <param name="expected">The canonical mode.</param>
    /// <param name="iterations">The default invocation cap.</param>
    [TestMethod]
    [DataRow("optimized", "fullopts", 1)]
    [DataRow("fullopts", "fullopts", 1)]
    [DataRow("Tier0", "tier0", 1)]
    [DataRow("Tier1", "tier1", 1000)]
    public void Parse_TierUsesCanonicalNameAndDefaultCap(string tier, string expected, int iterations)
    {
        var options = NativeCommand.Parse("Value --run --tier " + tier);

        Assert.AreEqual("Value", options.Selector);
        Assert.AreEqual(expected, options.Tier);
        Assert.AreEqual(iterations, options.Iterations);
        Assert.IsTrue(options.Run);
        Assert.IsFalse(options.Collectible);
    }

    /// <summary>
    /// Duration units and boundary values produce millisecond deadlines without changing iteration caps.
    /// </summary>
    /// <param name="duration">The accepted user duration.</param>
    /// <param name="milliseconds">The exact deadline.</param>
    [TestMethod]
    [DataRow("1ms", 1)]
    [DataRow("500ms", 500)]
    [DataRow("30s", 30000)]
    [DataRow("2m", 120000)]
    [DataRow("0.001s", 1)]
    [DataRow("1.5", 1500)]
    [DataRow("1.9ms", 1)]
    [DataRow("2147483647ms", int.MaxValue)]
    public void Parse_DurationUsesExistingUnits(string duration, int milliseconds)
    {
        var options = NativeCommand.Parse("Value --timeout " + duration);

        Assert.AreEqual(milliseconds, options.TimeoutMilliseconds);
        Assert.AreEqual(milliseconds, ComparisonCommand.Parse("Copy () --timeout " + duration).TimeoutMilliseconds);
        Assert.AreEqual(1, options.Iterations);
        Assert.IsFalse(options.Run);
    }

    /// <summary>
    /// Comparison and native timeout parsing reject the same malformed or overflowing values with the same explanation.
    /// </summary>
    /// <param name="duration">The invalid shared duration syntax.</param>
    [TestMethod]
    [DataRow("0ms")]
    [DataRow("0.0009s")]
    [DataRow("2147483648ms")]
    [DataRow("NaN")]
    [DataRow("Infinity")]
    [DataRow("1,5s")]
    [DataRow("+1s")]
    [DataRow("1e3s")]
    public void Parse_InvalidDurationMatchesComparisonDiagnostic(string duration)
    {
        var native = Assert.ThrowsExactly<ReplException>(() => NativeCommand.Parse("Value --timeout " + duration));
        var comparison = Assert.ThrowsExactly<ReplException>(() => ComparisonCommand.Parse("Copy () --timeout " + duration));

        Assert.AreEqual(comparison.Message, native.Message);
        Assert.Contains("--timeout requires a positive duration", native.Message);
    }

    /// <summary>
    /// Explicit positive iteration caps override the tier default at both integer boundaries.
    /// </summary>
    /// <param name="iterations">The caller's accepted invocation cap.</param>
    [TestMethod]
    [DataRow(1)]
    [DataRow(int.MaxValue)]
    public void Parse_ExplicitIterationCapOverridesTierDefault(int iterations)
    {
        var text = iterations.ToString(CultureInfo.InvariantCulture);
        var options = NativeCommand.Parse("Value --run --tier tier1 --iterations " + text);

        Assert.AreEqual(iterations, options.Iterations);
        Assert.AreEqual("tier1", options.Tier);
        Assert.IsTrue(options.Run);
    }

    /// <summary>
    /// Invalid options and incompatible execution modes fail during parsing before a target can be prepared.
    /// </summary>
    /// <param name="text">The malformed or contradictory request.</param>
    /// <param name="diagnostic">The actionable part of the error.</param>
    [TestMethod]
    [DataRow("Value --tier tier1", "requires explicit execution")]
    [DataRow("Value --tier tier0 --collectible", "collectible methods cannot be tiered")]
    [DataRow("Value --run --tier tier1 --collectible", "collectible methods cannot be tiered")]
    [DataRow("Value --iterations 1", "requires an explicit workload")]
    [DataRow("Value --run --iterations 0", "positive integer")]
    [DataRow("Value --run --iterations -1", "positive integer")]
    [DataRow("Value --run --iterations 2147483648", "positive integer")]
    [DataRow("Value --timeout 0ms", "positive duration")]
    [DataRow("Value --timeout -1ms", "positive duration")]
    [DataRow("Value --timeout 0.0009s", "positive duration")]
    [DataRow("Value --timeout 2147483648ms", "positive duration")]
    [DataRow("Value --timeout NaN", "positive duration")]
    [DataRow("Value --pgo off", "applies to tier0 and tier1")]
    [DataRow("Value --tier tier0 --pgo maybe", "requires on or off")]
    [DataRow("Value --tier osr", "requires fullopts, tier0, or tier1")]
    [DataRow("Value --tier", "requires a value")]
    [DataRow("Value --against", "requires a selector")]
    [DataRow("Value --against Other --against Third", "only once")]
    [DataRow("Value () using Scenario", "one literal workload or one scenario")]
    [DataRow("Value using Scenario ()", "one literal workload or one scenario")]
    [DataRow("Value --env =0", "requires NAME=VALUE")]
    [DataRow("Value --env DOTNET_EnableAVX=0 --env DOTNET_EnableAVX=1", "more than once")]
    [DataRow("Value --args (1)", "unknown native option")]
    [DataRow("Value --using Scenario", "unknown native option")]
    [DataRow("Value --info", "cannot be combined")]
    [DataRow("--info --run", "cannot be combined")]
    [DataRow("Value (\"unfinished)", "unterminated")]
    [DataRow("Value<int32", "unterminated")]
    [DataRow("Value)", "unbalanced")]
    public void Parse_InvalidRequestFailsBeforeExecution(string text, string diagnostic)
    {
        var error = Assert.ThrowsExactly<ReplException>(() => NativeCommand.Parse(text));

        Assert.Contains(diagnostic, error.Message);
    }
}
