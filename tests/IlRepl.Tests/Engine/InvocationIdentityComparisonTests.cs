using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Real method calls preserve object identity across one invocation while snapshots retain current field values.
/// </summary>
[TestClass]
public sealed class InvocationIdentityComparisonTests
{
    /// <summary>
    /// Supplies cancellation for actual isolated worker processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Equal replacements differ even when the scenario returns the same value and every original field is unchanged.
    /// </summary>
    /// <param name="shape">The specific reference path, return or control.</param>
    /// <param name="replace">Whether the edited method substitutes a distinct equal object.</param>
    [TestMethod]
    [DataRow("ref-object", false)]
    [DataRow("ref-object", true)]
    [DataRow("ref-string", true)]
    [DataRow("ref-box", true)]
    [DataRow("receiver", true)]
    [DataRow("array", true)]
    [DataRow("dictionary", true)]
    [DataRow("return", false)]
    [DataRow("return", true)]
    [DataRow("mutate", false)]
    [DataRow("null", false)]
    [DataRow("out", false)]
    [DataRow("task", true)]
    [DataRow("valuetask", true)]
    [DataRow("throw", true)]
    public async Task Compare_BoundaryIdentityDistinguishesRetainedAndReplacedReferences(string shape, bool replace)
    {
        var session = IlLines.Load(InvocationIdentityExamples.Source(shape).Split('\n'));
        var edit = session.PrepareEdit(InvocationIdentityExamples.Reference(shape), "Copy");
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        session.CommitEdit(edit.Name, InvocationIdentityExamples.Method(shape, replace));
        await AssertActualReference(edit.Original.Requested, shape, replaced: false);
        await AssertActualReference(edit.OriginalMethod, shape, replaced: false);
        await AssertActualReference(edit.Method!, shape, replace);
        foreach (var line in InvocationIdentityExamples.Scenarios(shape).Split('\n'))
        {
            session.AddLine(line);
        }

        var witness = session.Methods.Single(method => method.Signature.Name == "Witness").Version.Body;
        Assert.AreEqual(replace || shape == "out" ? 0 : 1, witness.Invoke(null, null));
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);
        Assert.AreEqual(replace ? "different" : "match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        AssertBoundary(result.Original, shape, replaced: false);
        AssertBoundary(result.Edited, shape, replace);
        session.AddLine("call Witness");
        foreach (var image in new[] { AssemblyExporter.Write(session, "boundary-identity"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("boundary-identity", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(replace || shape == "out" ? 0 : 1, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                var exported = assembly.GetType(edit.Method!.DeclaringType!.FullName!)!.GetMethod(edit.Method.Name)!;
                await AssertActualReference(exported, shape, replace);
            }
            finally
            {
                context.Unload();
            }
        }
    }

    private static async Task AssertActualReference(MethodBase method, string shape, bool replaced)
    {
        var owner = method.DeclaringType!;
        var before = shape == "null" ? null : owner.GetMethod("NewValue")!.Invoke(null, null);
        object? receiver = null;
        object?[] arguments;
        if (shape == "receiver")
        {
            receiver = Activator.CreateInstance(owner)!;
            owner.GetField("Value")!.SetValue(receiver, before);
            arguments = [];
        }
        else if (shape == "array")
        {
            arguments = [new object?[] { new[] { before } }];
        }
        else if (shape == "dictionary")
        {
            arguments = [new Dictionary<string, object?> { ["item"] = before }];
        }
        else
        {
            arguments = [before];
        }

        object? returned;
        if (shape == "throw")
        {
            var exception = Assert.ThrowsExactly<TargetInvocationException>(() => method.Invoke(receiver, arguments));
            returned = exception.InnerException;
            Assert.IsInstanceOfType<Exception>(returned);
            Assert.AreEqual("same exception", ((Exception)returned).Message);
        }
        else
        {
            returned = method.Invoke(receiver, arguments);
        }

        if (shape is "task" or "valuetask")
        {
            var task = shape == "task" ? Assert.IsInstanceOfType<Task<object>>(returned)
                : Assert.IsInstanceOfType<ValueTask<object>>(returned).AsTask();
            Assert.IsFalse(task.IsCompleted, "Read must return before its completion source is completed.");
            owner.GetMethod("Complete")!.Invoke(null, null);
            returned = await task;
        }

        var after = shape switch
        {
            "receiver" => owner.GetField("Value")!.GetValue(receiver),
            "array" => ((object?[])((object?[])arguments[0]!)[0]!)[0],
            "dictionary" => ((Dictionary<string, object?>)arguments[0]!)["item"],
            "return" or "task" or "valuetask" or "throw" => returned,
            _ => arguments[0],
        };
        if (replaced || shape == "out")
        {
            Assert.AreNotSame(before, after);
        }
        else
        {
            Assert.AreSame(before, after);
        }

        if (shape == "ref-string")
        {
            Assert.AreEqual("x", after);
        }
        else if (shape == "ref-box")
        {
            Assert.AreEqual(42, after);
        }
        else if (shape == "null")
        {
            Assert.IsNull(after);
        }
        else if (shape != "throw")
        {
            Assert.AreEqual(shape == "mutate" ? 43 : 42, after!.GetType().GetField("Number")!.GetValue(after));
        }
    }

    private static void AssertBoundary(ComparisonSide side, string shape, bool replaced)
    {
        Assert.AreEqual("completed", side.Outcome, side.Detail);
        Assert.IsNull(side.Exception);
        Assert.IsNotNull(side.Result);
        Assert.AreEqual("scalar", side.Result.Kind);
        Assert.AreEqual("42", side.Result.Value);
        var invocation = Assert.ContainsSingle(side.Invocations);
        var input = Root(invocation.Inputs, shape == "receiver" ? "receiver" : "argument 0");
        var output = Root(invocation.Outputs, shape == "receiver" ? "receiver" : "argument 0");
        if (shape == "null")
        {
            Assert.AreEqual("null", input.Kind);
            Assert.IsNull(input.Identity);
            Assert.AreEqual("null", output.Kind);
            Assert.IsNull(output.Identity);
            return;
        }

        if (shape == "out")
        {
            Assert.AreEqual("null", input.Kind);
            Assert.IsNull(input.Identity);
            Assert.AreEqual("object", output.Kind);
            Assert.IsNotNull(output.Identity);
            Assert.AreEqual("42", Field(output, "Number").Value);
            return;
        }

        if (shape is "receiver" or "array" or "dictionary")
        {
            Assert.AreEqual(input.Identity, output.Identity);
            Assert.AreEqual(input.Kind, output.Kind);
            input = Nested(input, shape);
            output = Nested(output, shape);
        }

        if (shape is "return" or "task" or "valuetask")
        {
            Assert.AreEqual(input.Identity, output.Identity);
            Assert.AreEqual("object", output.Kind);
            Assert.AreEqual("42", Field(output, "Number").Value);
            output = Root(invocation.Outputs, "return");
        }

        if (shape == "throw")
        {
            Assert.AreEqual(input.Identity, output.Identity);
            Assert.IsNotNull(invocation.Exception);
            Assert.AreEqual("same exception", invocation.Exception.Message);
            Assert.EndsWith("System.Exception", invocation.Exception.Type);
            if (replaced)
            {
                Assert.AreNotEqual(input.Identity, invocation.Exception.Identity);
            }
            else
            {
                Assert.AreEqual(input.Identity, invocation.Exception.Identity);
            }

            return;
        }

        Assert.IsNull(invocation.Exception);
        if (replaced)
        {
            Assert.AreNotEqual(input.Identity, output.Identity);
        }
        else
        {
            Assert.AreEqual(input.Identity, output.Identity);
        }

        if (shape is "ref-string" or "ref-box")
        {
            Assert.AreEqual("scalar", input.Kind);
            Assert.AreEqual("scalar", output.Kind);
            Assert.AreEqual(shape == "ref-string" ? "x" : "42", input.Value);
            Assert.AreEqual(input.Value, output.Value);
        }
        else
        {
            Assert.AreEqual("object", input.Kind);
            Assert.AreEqual("42", Field(input, "Number").Value);
            if (shape is "return" or "task" or "valuetask" && !replaced)
            {
                Assert.AreEqual("reference", output.Kind);
                Assert.IsEmpty(output.Members);
            }
            else
            {
                Assert.AreEqual("object", output.Kind);
                Assert.AreEqual(shape == "mutate" ? "43" : "42", Field(output, "Number").Value);
            }
        }
    }

    private static ObservedValue Root(IReadOnlyList<ObservedMember> members, string name) =>
        members.Single(member => member.Name == name).Value;

    private static ObservedValue Field(ObservedValue value, string name) =>
        value.Members.Single(member => member.Name.EndsWith("::" + name, StringComparison.Ordinal)).Value;

    private static ObservedValue Nested(ObservedValue value, string shape) => shape switch
    {
        "receiver" => Field(value, "Value"),
        "array" => value.Members[0].Value.Members[0].Value,
        _ => Root(value.Members.Single(member => member.Name == "0").Value.Members, "value"),
    };
}
