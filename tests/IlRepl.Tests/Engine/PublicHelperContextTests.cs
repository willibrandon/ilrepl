using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Public source helpers share copied state when required and remain external when their original context is independent.
/// </summary>
[TestClass]
public sealed class PublicHelperContextTests
{
    /// <summary>
    /// Supplies cancellation for actual isolated process comparisons.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Actual original and copied behavior agrees across public helper dependency routes without sharing source static state.
    /// </summary>
    /// <param name="shape">The direct, transitive, write, callback, generic, signature, initializer, cycle or stateless route.</param>
    [TestMethod]
    [DataRow("read")]
    [DataRow("transitive")]
    [DataRow("write")]
    [DataRow("callback")]
    [DataRow("generic")]
    [DataRow("signature")]
    [DataRow("cctor")]
    [DataRow("revisit")]
    [DataRow("cycle")]
    [DataRow("stateless")]
    [DataRow("external")]
    [DataRow("identity")]
    [DataRow("lookup")]
    public async Task Edit_PublicHelpersRetainRequiredContextAndIndependentExternalIdentity(string shape)
    {
        var session = new Session();
        var image = PublicHelperFixture.Create(shape);
        var assembly = session.Resolver.LoadImage(image);
        var owner = assembly.GetType("PublicContext.Owner")!;
        var helper = assembly.GetType("PublicContext.Helper")!;
        var other = assembly.GetType("PublicContext.Other")!;
        Assert.IsTrue(owner.IsVisible);
        Assert.IsTrue(helper.IsVisible);
        Assert.AreSame(owner.Assembly, helper.Assembly);
        Assert.AreSame(owner.Assembly, other.Assembly);
        var original = owner.GetMethod("Read")!;
        Assert.AreEqual(42, original.Invoke(null, null));
        var edit = session.PrepareEdit("int32 PublicContext.Owner::Read()", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        SetSentinels(owner, helper, other);
        session.CommitEdit(edit.Name, edit.Source);
        AssertSentinels(owner, helper, other);
        Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, null));
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        Assert.AreEqual(42, edit.Method.DeclaringType!.GetField("State")!.GetValue(null));
        AssertSentinels(owner, helper, other);
        AssertHelperDisposition(edit, shape, assembly, session);
        foreach (var line in PublicHelperFixture.Scenario().Split('\n'))
        {
            session.AddLine(line);
        }

        foreach (var command in new[] { "Copy ()", "Copy using Scenario" })
        {
            var unchanged = await Run(session, command);
            Assert.AreEqual("match", unchanged.Outcome, Details(unchanged));
            AssertSide(unchanged.Original, "42");
            AssertSide(unchanged.Edited, "42");
        }

        var originalConstant = shape == "write" ? "ldc.i4.s 41" : "ldc.i4.s 42";
        Assert.Contains(originalConstant, edit.Source);
        session.CommitEdit(edit.Name, edit.Source.Replace(originalConstant,
            shape == "write" ? "ldc.i4.s 42" : "ldc.i4.s 43", StringComparison.Ordinal));
        Assert.AreEqual(43, edit.Method!.Invoke(null, null));
        Assert.AreEqual(43, edit.Method.DeclaringType!.GetField("State")!.GetValue(null));
        AssertSentinels(owner, helper, other);
        Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, null));
        foreach (var command in new[] { "Copy ()", "Copy using Scenario" })
        {
            var changed = await Run(session, command);
            Assert.AreEqual("different", changed.Outcome, Details(changed));
            AssertSide(changed.Original, "42");
            AssertSide(changed.Edited, "43");
        }

        session.AddLine("call Copy");
        var exports = new[] { AssemblyExporter.Write(session, "public-helper-copy"), IlasmLocator.Assemble(session.ToIlAsm()) };
        foreach (var exportedImage in exports)
        {
            var context = new AssemblyLoadContext("public-helper-copy", isCollectible: true);
            context.Resolving += (_, name) => name.Name == assembly.GetName().Name ? assembly : null;
            try
            {
                var exported = context.LoadFromStream(new MemoryStream(exportedImage));
                if (shape is "stateless" or "identity")
                {
                    Assert.Contains(reference => reference.Name == assembly.GetName().Name,
                        exported.GetReferencedAssemblies());
                }
                else
                {
                    Assert.DoesNotContain(reference => reference.Name == assembly.GetName().Name, exported.GetReferencedAssemblies());
                }

                Assert.AreEqual(43, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                var copied = exported.GetType(edit.Method.DeclaringType.FullName!)!.GetMethod("Read")!;
                Assert.AreEqual(43, copied.Invoke(null, null));
                Assert.AreEqual(43, copied.DeclaringType!.GetField("State")!.GetValue(null));
                AssertSentinels(owner, helper, other);
            }
            finally
            {
                context.Unload();
            }
        }
    }

    private static void SetSentinels(Type owner, Type helper, Type other)
    {
        owner.GetField("State")!.SetValue(null, 7);
        helper.GetField("Cached")!.SetValue(null, 17);
        other.GetField("State")!.SetValue(null, 11);
    }

    private static void AssertSentinels(Type owner, Type helper, Type other)
    {
        Assert.AreEqual(7, owner.GetField("State")!.GetValue(null));
        Assert.AreEqual(17, helper.GetField("Cached")!.GetValue(null));
        Assert.AreEqual(11, other.GetField("State")!.GetValue(null));
    }

    private static void AssertHelperDisposition(MethodEdit edit, string shape, Assembly source, Session session)
    {
        var expected = shape is "stateless" or "identity" or "external" ? "external" : "copied";
        var symbol = shape == "external" ? "Math::Max" : "Helper::Fetch";
        Assert.Contains(dependency => dependency.Symbol.Contains(symbol, StringComparison.Ordinal)
            && dependency.Disposition == expected && dependency.Access == "public", edit.Dependencies);
        var target = MethodDisassembler.Disassemble(edit.Method!, session).Entries.Where(entry => entry.Instruction is not null)
            .Select(entry => entry.Instruction!.Operand).OfType<ResolvedMethod>().Select(method => method.Method!)
            .First(method => method.Name == (shape == "external" ? "Max" : "Fetch"));
        if (shape == "external")
        {
            Assert.AreEqual(typeof(Math), target.DeclaringType);
        }
        else if (shape is "stateless" or "identity")
        {
            Assert.AreSame(source.GetType("PublicContext.Helper"), target.DeclaringType);
        }
        else
        {
            Assert.AreSame(edit.Method!.Module.Assembly, target.Module.Assembly);
        }

        if (shape == "generic")
        {
            Assert.AreSame(edit.Method!.DeclaringType, Assert.ContainsSingle(target.GetGenericArguments()));
        }

        if (shape == "signature")
        {
            Assert.AreSame(edit.Method!.DeclaringType, Assert.ContainsSingle(target.GetParameters()).ParameterType);
        }
    }

    private Task<ComparisonReply> Run(Session session, string command) =>
        ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, command), TestContext.CancellationToken);

    private static void AssertSide(ComparisonSide side, string expected)
    {
        Assert.AreEqual("completed", side.Outcome, side.Detail);
        Assert.IsNull(side.Exception);
        Assert.IsNotNull(side.Result);
        Assert.AreEqual("scalar", side.Result.Kind);
        Assert.AreEqual(expected, side.Result.Value);
        var invocation = Assert.ContainsSingle(side.Invocations);
        Assert.IsNull(invocation.Exception);
        var output = invocation.Outputs.Single(member => member.Name == "return").Value;
        Assert.AreEqual("scalar", output.Kind);
        Assert.AreEqual(expected, output.Value);
        Assert.AreEqual("null", invocation.Inputs.Single(member => member.Name == "receiver").Value.Kind);
        Assert.DoesNotContain(member => member.Name.StartsWith("argument ", StringComparison.Ordinal), invocation.Inputs);
    }

    private static string Details(ComparisonReply result) => result.Original.Detail + "; " + result.Edited.Detail;
}
