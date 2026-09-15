using System.Globalization;
using System.Reflection;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Direct comparisons close generic methods and owners using captured type arguments in fresh worker processes.
/// </summary>
[TestClass]
public sealed class DirectGenericComparisonTests
{
    /// <summary>
    /// Supplies cancellation for the actual comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A direct call preserves closed method or owner arguments and observes the changed primitive or string result.
    /// </summary>
    /// <param name="genericOwner">Whether the generic parameter belongs to the declaring type.</param>
    /// <param name="strings">Whether the closed argument is string rather than int32.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Direct_ClosedMethodAndOwnerRetainLiteralArguments(bool genericOwner, bool strings)
    {
        var session = genericOwner
            ? IlLines.Load(".class public Choice`1<T> {", ".method public static !0 Pick(!0 first, !0 second) {",
                "ldarg.0", "ret", "}", "}")
            : IlLines.Load(".class public Choice {", ".method public static !!0 Pick<T>(!!0 first, !!0 second) {",
                "ldarg.0", "ret", "}", "}");
        var argument = strings ? "string" : "int32";
        var reference = genericOwner ? $"!0 Choice`1<{argument}>::Pick(!0, !0)" : $"!!0 Choice::Pick<{argument}>(!!0, !!0)";
        var edit = session.PrepareEdit(reference, "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldarg.0", "ldarg.1", StringComparison.Ordinal));
        Assert.IsFalse(edit.Method!.ContainsGenericParameters);
        var literals = strings ? "\"first\", \"second\"" : "41, 42";
        var package = ComparisonCapture.Create(session, "Copy (" + literals + ")");
        var type = strings ? typeof(string) : typeof(int);
        foreach (var image in new[] { package.Original, package.Edited })
        {
            Assert.HasCount(genericOwner ? 1 : 0, image.TypeArguments);
            Assert.HasCount(genericOwner ? 0 : 1, image.MethodArguments);
            Assert.AreEqual(type.AssemblyQualifiedName, (genericOwner ? image.TypeArguments : image.MethodArguments).Single());
            Assert.HasCount(2, image.Arguments);
        }

        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);

        AssertDifferent(result, strings ? "first" : "41", strings ? "second" : "42");
        foreach (var side in new[] { result.Original, result.Edited })
        {
            var inputs = side.Invocations.Single().Inputs;
            Assert.AreEqual(strings ? "first" : "41", inputs.Single(member => member.Name == "argument 0").Value.Value);
            Assert.AreEqual(strings ? "second" : "42", inputs.Single(member => member.Name == "argument 1").Value.Value);
            Assert.EndsWith(type.FullName!, side.Result!.Type);
        }
    }

    /// <summary>
    /// A parameterless method closed over a separately declared session struct resolves that argument in each exported image.
    /// </summary>
    /// <param name="genericOwner">Whether the captured argument belongs to the owner instead of the method.</param>
    /// <param name="argument">The closed type argument containing the session struct.</param>
    /// <param name="redefine">Whether the source struct changes before the comparison is captured.</param>
    [TestMethod]
    [DataRow(false, "Payload", false)]
    [DataRow(true, "Payload", false)]
    [DataRow(false, "Payload[]", false)]
    [DataRow(false, "Payload[,]", false)]
    [DataRow(false, "List<List<Payload[]>>", false)]
    [DataRow(true, "List<Payload[]>", false)]
    [DataRow(false, "Envelope`1/Nested`1<Payload, Payload[]>", false)]
    [DataRow(false, "Payload", true)]
    [DataRow(true, "Payload", true)]
    public async Task Direct_SessionStructGenericArgumentSurvivesFreshWorkers(bool genericOwner, string argument, bool redefine)
    {
        var session = IlLines.Load(".class public sequential sealed Payload extends System.ValueType {",
            ".field public int32 Number", "}", ".class public Envelope`1<T> {",
            ".class nested public Nested`1<T, U> {", "}", "}");
        Add(session, genericOwner ? ".class public Choice`1<T> {" : ".class public Choice {",
            genericOwner ? ".method public static int32 Size() {" : ".method public static int32 Size<T>() {",
            genericOwner ? "sizeof !0" : "sizeof !!0", "ret", "}", "}");
        var reference = genericOwner ? $"int32 Choice`1<{argument}>::Size()" : $"int32 Choice::Size<{argument}>()";
        var edit = session.PrepareEdit(reference, "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
        Assert.IsFalse(edit.Method!.ContainsGenericParameters);
        var requested = (MethodInfo)edit.Original.Requested;
        var originalArgument = genericOwner ? requested.DeclaringType!.GenericTypeArguments.Single()
            : requested.GetGenericArguments().Single();
        var size = argument == "Payload" ? 4 : IntPtr.Size;
        if (argument == "Payload")
        {
            Assert.Contains(dependency => dependency.Symbol == "Payload"
                && dependency.Assembly == originalArgument.Assembly.FullName && dependency.Access == "public"
                && dependency.Location.EndsWith(": generic argument", StringComparison.Ordinal)
                && dependency.Disposition == "copied (distinct type identity)", edit.Dependencies);
        }

        if (redefine)
        {
            Add(session, ".class public sequential sealed Payload extends System.ValueType {", ".field public int64 Number", "}");
            var updated = session.Types.Single(type => type.RuntimeType?.Name == "Payload").RuntimeType!;
            Assert.AreNotSame(originalArgument, updated);
            var current = genericOwner
                ? requested.DeclaringType!.GetGenericTypeDefinition().MakeGenericType(updated).GetMethod("Size")!
                : requested.GetGenericMethodDefinition().MakeGenericMethod(updated);
            Assert.AreEqual(8, current.Invoke(null, null));
            session.CommitEdit(edit.Name, edit.Source);
        }

        Assert.AreEqual(size, edit.OriginalMethod.Invoke(null, null));
        Assert.AreEqual(size + 1, edit.Method.Invoke(null, null));
        var package = ComparisonCapture.Create(session, "Copy ()");
        Assert.HasCount(1, genericOwner ? package.Original.TypeArguments : package.Original.MethodArguments);
        Assert.HasCount(1, genericOwner ? package.Edited.TypeArguments : package.Edited.MethodArguments);
        Assert.IsEmpty(package.Original.Arguments);
        Assert.IsEmpty(package.Edited.Arguments);

        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);

        AssertDifferent(result, size.ToString(CultureInfo.InvariantCulture), (size + 1).ToString(CultureInfo.InvariantCulture));
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.HasCount(1, side.Invocations.Single().Inputs);
            Assert.AreEqual("receiver", side.Invocations.Single().Inputs[0].Name);
            Assert.AreEqual("null", side.Invocations.Single().Inputs[0].Value.Kind);
        }
    }

    /// <summary>
    /// Generic activation retains the selected argument's constructor and its initialized field state in both workers.
    /// </summary>
    [TestMethod]
    public async Task Direct_GenericActivationPreservesTheArgumentConstructor()
    {
        var session = IlLines.Load(".class public Constructed {", ".field public int32 Number",
            ".method public specialname rtspecialname instance void .ctor() {", "ldarg.0",
            "call instance void Object::.ctor()", "ldarg.0", "ldc.i4.s 41", "stfld int32 Constructed::Number", "ret", "}", "}",
            ".class public Factory {", ".method public static object Make<class .ctor T>() {",
            "call !!0 System.Activator::CreateInstance<!!0>()", "box !!0", "ret", "}", "}");
        var edit = session.PrepareEdit("object Factory::Make<Constructed>()", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ret",
            "dup\ncastclass Constructed\nldc.i4.s 42\nstfld int32 Constructed::Number\nret", StringComparison.Ordinal));
        var original = edit.OriginalMethod.Invoke(null, null)!;
        var changed = edit.Method!.Invoke(null, null)!;
        Assert.AreEqual(41, original.GetType().GetField("Number")!.GetValue(original));
        Assert.AreEqual(42, changed.GetType().GetField("Number")!.GetValue(changed));

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);

        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "\n" + result.Edited.Detail);
        Assert.AreEqual("completed", result.Original.Outcome);
        Assert.AreEqual("completed", result.Edited.Outcome);
        Assert.AreEqual("object", result.Original.Result!.Kind);
        Assert.AreEqual("object", result.Edited.Result!.Kind);
        Assert.AreEqual("41", result.Original.Result.Members.Single(member => member.Name.EndsWith("::Number",
            StringComparison.Ordinal)).Value.Value);
        Assert.AreEqual("42", result.Edited.Result.Members.Single(member => member.Name.EndsWith("::Number",
            StringComparison.Ordinal)).Value.Value);
        Assert.HasCount(1, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);
    }

    /// <summary>
    /// An external original with an unavailable native helper retains the session argument used to close its method or owner.
    /// </summary>
    /// <param name="genericOwner">Whether the type argument belongs to the declaring type.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Direct_ExternalOriginalCapturesTheSessionGenericArgument(bool genericOwner)
    {
        var session = IlLines.Load(".class public sequential sealed Payload extends System.ValueType {",
            ".field public int32 Number", "}");
        var name = "ExternalGeneric" + Guid.NewGuid().ToString("N");
        var source = """
            .class private N.NativeCalls extends [System.Runtime]System.Object {
              .method assembly static void Native() runtime managed internalcall {}
            }
            .class public N.Fixture`1<T> extends [System.Runtime]System.Object {
              .method public static int32 Size() cil managed {
                sizeof !T
                ret
                call void N.NativeCalls::Native()
                ldc.i4.0
                ret
              }
            }
            """;
        if (!genericOwner)
        {
            source = source.Replace("N.Fixture`1<T>", "N.Fixture", StringComparison.Ordinal)
                .Replace("Size()", "Size<T>()", StringComparison.Ordinal)
                .Replace("sizeof !T", "sizeof !!T", StringComparison.Ordinal);
        }

        var image = IlasmLocator.Assemble(".assembly extern System.Runtime {}\n.assembly " + name + " {}\n.module " + name
            + ".dll\n" + source);
        var assembly = session.Resolver.LoadImage(image);
        var payload = session.Types.Single(type => type.RuntimeType?.Name == "Payload").RuntimeType!;
        var requested = genericOwner ? assembly.GetType("N.Fixture`1")!.MakeGenericType(payload).GetMethod("Size")!
            : assembly.GetType("N.Fixture")!.GetMethod("Size")!.MakeGenericMethod(payload);
        Assert.AreEqual(4, requested.Invoke(null, null));
        var reference = genericOwner ? "]N.Fixture`1<Payload>::Size()" : "]N.Fixture::Size<Payload>()";
        var edit = session.PrepareEdit("int32 [" + name + reference, "Copy");
        Assert.Contains(problem => problem.Contains("Native", StringComparison.Ordinal), edit.Problems,
            string.Join("\n", edit.Problems));
        Assert.IsNull(edit.Method);
        var unavailable = Assert.ThrowsExactly<ReplException>(() => _ = edit.OriginalMethod);
        Assert.Contains("original context cannot be reproduced", unavailable.Message);
        session.CommitEdit(edit.Name, genericOwner
            ? ".method public static int32 Size() cil managed {\nsizeof !0\nldc.i4.1\nadd\nret\n}"
            : ".method public static int32 Size<T>() cil managed {\nsizeof !!0\nldc.i4.1\nadd\nret\n}");
        Assert.IsEmpty(edit.Problems);
        Assert.AreEqual(5, edit.Method!.Invoke(null, null));
        var package = ComparisonCapture.Create(session, "Copy ()");
        Assert.IsNotEmpty(package.Original.Image);
        Assert.AreEqual(assembly.FullName, package.Original.OriginalAssembly);
        Assert.AreEqual(assembly.ManifestModule.ModuleVersionId, package.Original.OriginalModule);
        Assert.IsEmpty(package.Original.MethodArguments);
        Assert.Contains(dependency => dependency.Name == payload.Assembly.FullName, package.Dependencies);
        Assert.Contains(dependency => dependency.Name == assembly.FullName, package.Dependencies);

        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);

        AssertDifferent(result, "4", "5");
    }

    private static void Add(Session session, params string[] source)
    {
        foreach (var line in IlLines.Expand(source))
        {
            session.AddLine(line);
        }
    }

    private static void AssertDifferent(ComparisonReply result, string original, string edited)
    {
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "\n" + result.Edited.Detail);
        Assert.AreEqual("completed", result.Original.Outcome);
        Assert.AreEqual("completed", result.Edited.Outcome);
        Assert.IsNull(result.Original.Exception);
        Assert.IsNull(result.Edited.Exception);
        Assert.AreEqual(original, result.Original.Result!.Value);
        Assert.AreEqual(edited, result.Edited.Result!.Value);
        Assert.HasCount(1, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);
    }
}
