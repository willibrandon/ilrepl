using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Edited custom attributes preserve exact targets, dependencies and duplicate rows across execution and independent exports.
/// </summary>
[TestClass]
public sealed class EditedCustomAttributeMetadataTests
{
    private static readonly string[] OriginalParameterTags =
        ["method", "method", "after-instruction", "return", "parameter-1", "parameter-2"];
    private static readonly string[] AddedParameterTags = [.. OriginalParameterTags, "added"];

    /// <summary>
    /// Supplies cancellation for real worker processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Typed attributes reach targets and aliases without executing constructors during edit, capture or export.
    /// </summary>
    /// <param name="isPrivate">Whether the callable alias forwards to a private method.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Commit_CustomAttributesReachTargetsAliasesWorkersAndExports(bool isPrivate)
    {
        var session = IlLines.Load(EditedCustomAttributeExamples.Source(isPrivate).Split('\n'));
        var originalTag = session.Types.Single(type => type.RuntimeType!.Name == "Tag").RuntimeType!;
        var edit = session.PrepareEdit("int32 Owner::Read(int32)", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        AssertOriginal(Assert.IsInstanceOfType<MethodInfo>(edit.Original.Requested));
        AssertOriginal(Assert.IsInstanceOfType<MethodInfo>(edit.OriginalMethod));
        session.CommitEdit(edit.Name, EditedCustomAttributeExamples.Method(isPrivate, edited: true));
        var method = Assert.IsInstanceOfType<MethodInfo>(edit.Method);
        var alias = Assert.IsInstanceOfType<MethodInfo>(session.TypeTable.MethodAliases[edit.Name]);
        var tag = method.Module.Assembly.GetTypes().Single(type => type.GetField("Runs") is not null);
        Assert.AreEqual(0, tag.GetField("Runs")!.GetValue(null));
        Assert.AreEqual(0, originalTag.GetField("Runs")!.GetValue(null));
        AssertData(method, added: false);
        AssertData(alias, added: false);
        Assert.AreEqual(0, tag.GetField("Runs")!.GetValue(null));
        Assert.Contains(dependency => dependency.Location.Contains(": attribute Tag", StringComparison.Ordinal)
            && dependency.Disposition == "copied", edit.Dependencies);
        foreach (var line in EditedCustomAttributeExamples.Scenario().Split('\n'))
        {
            session.AddLine(line);
        }

        var reply = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", reply.Outcome, reply.Original.Detail + "; " + reply.Edited.Detail);
        AssertSide(reply.Original, "111");
        AssertSide(reply.Edited, "423");
        session.AddLine("call Scenario");
        var images = new[] { AssemblyExporter.Write(session, "custom-edit"), IlasmLocator.Assemble(session.ToIlAsm()) };
        Assert.AreEqual(0, tag.GetField("Runs")!.GetValue(null));
        foreach (var image in images)
        {
            var context = new AssemblyLoadContext("custom-edit", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                foreach (var source in new[] { method, alias })
                {
                    var exported = Exported(assembly, source);
                    AssertData(exported, added: false);
                    AssertInstances(exported, added: false);
                    Assert.AreEqual(42, exported.Invoke(null, [42]));
                }

                Assert.AreEqual(423, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }

        AssertInstances(method, added: false);
        AssertInstances(alias, added: false);
        Assert.AreEqual(0, originalTag.GetField("Runs")!.GetValue(null));
        AssertOriginal(Assert.IsInstanceOfType<MethodInfo>(edit.Original.Requested));
        AssertOriginal(Assert.IsInstanceOfType<MethodInfo>(edit.OriginalMethod));
    }

    /// <summary>
    /// New directives use current types, original attribute dependencies stay pinned, and revisions do not accumulate rows.
    /// </summary>
    [TestMethod]
    public void Commit_AttributeRevisionsResolveNewTypesAndPreservePinnedOriginalMetadata()
    {
        var session = IlLines.Load(EditedCustomAttributeExamples.Source(isPrivate: true).Split('\n'));
        var edit = session.PrepareEdit("int32 Owner::Read(int32)", "Copy");
        var pinned = session.PrepareEdit("int32 Tagged::Read(int32)", "Pinned");
        session.CommitEdit(pinned.Name, pinned.Source);
        var source = EditedCustomAttributeExamples.Method(isPrivate: true, edited: true, additionalParameter: true);
        session.CommitEdit(edit.Name, source);
        AssertData(Assert.IsInstanceOfType<MethodInfo>(edit.Method), added: true);
        foreach (var line in IlLines.Expand(".class public Marker { .field public int64 Added; }"))
        {
            session.AddLine(line);
        }

        session.CommitEdit(edit.Name, source);
        session.CommitEdit(pinned.Name, pinned.Source);
        foreach (var target in new[] { pinned.Original.Requested, pinned.OriginalMethod, pinned.Method! })
        {
            var attribute = Assert.ContainsSingle(target.GetCustomAttributesData());
            var kind = Assert.IsInstanceOfType<Type>(attribute.ConstructorArguments[0].Value);
            Assert.IsNotNull(kind.GetField("Original"));
            Assert.IsNull(kind.GetField("Added"));
            Assert.AreEqual(42, target.Invoke(null, [42]));
        }

        Assert.AreEqual(2, edit.Revision);
        var method = Assert.IsInstanceOfType<MethodInfo>(edit.Method);
        var alias = Assert.IsInstanceOfType<MethodInfo>(session.TypeTable.MethodAliases[edit.Name]);
        foreach (var target in new[] { method, alias })
        {
            AssertData(target, added: true, redefined: true);
            AssertInstances(target, added: true, redefined: true);
            Assert.AreEqual(42, target.Invoke(null, [42, 99]));
        }

        foreach (var image in new[] { AssemblyExporter.Write(session, "added-custom"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var assembly = Assembly.Load(image);
            foreach (var target in new[] { method, alias })
            {
                AssertData(Exported(assembly, target), added: true, redefined: true);
                AssertInstances(Exported(assembly, target), added: true, redefined: true);
            }
        }

        session.CommitEdit(edit.Name, ".method private static int32 Read(int32 value) {\nldarg.0\nret\n}");
        Assert.AreEqual(3, edit.Revision);
        AssertOriginal(Assert.IsInstanceOfType<MethodInfo>(edit.Method));
        AssertOriginal(Assert.IsInstanceOfType<MethodInfo>(session.TypeTable.MethodAliases[edit.Name]));
        Assert.AreEqual(42, edit.Method!.Invoke(null, [42]));
    }

    /// <summary>
    /// Blob, enum and null attribute values remain real metadata without imposing C# duplicate restrictions.
    /// </summary>
    [TestMethod]
    public void Commit_BlobEnumAndNullAttributeValuesRemainExact()
    {
        var session = IlLines.Load(".class public Owner {", ".method public static int32 Read() {", "ldc.i4.s 42", "ret", "}", "}");
        var edit = session.PrepareEdit("int32 Owner::Read()", "Copy");
        session.CommitEdit(edit.Name, """
            .method public static int32 Read() {
              .custom instance void ObsoleteAttribute::.ctor(string) = ( 01 00 03 6F 6C 64 00 00 )
              .custom instance void ObsoleteAttribute::.ctor(string) = { nullref }
              .custom instance void AttributeUsageAttribute::.ctor(valuetype AttributeTargets) = { int32(64) }
              ldc.i4.s 42
              ret
            }
            """.Replace("int32(64) }", "int32(64) property bool AllowMultiple = bool(true) }", StringComparison.Ordinal));
        foreach (var method in new[] { edit.Method!, session.TypeTable.MethodAliases[edit.Name] })
        {
            AssertSimple(method);
        }

        foreach (var image in new[] { AssemblyExporter.Write(session, "simple-custom"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            AssertSimple(Exported(Assembly.Load(image), edit.Method!));
        }
    }

    /// <summary>
    /// Invalid target indices and named values leave the prior metadata and executable published until correction succeeds.
    /// </summary>
    /// <param name="directive">The invalid directive.</param>
    /// <param name="diagnostic">The expected useful diagnostic fragment.</param>
    [TestMethod]
    [DataRow(".param [2]", "parameter")]
    [DataRow(".custom instance void ObsoleteAttribute::.ctor(string) = ( 00 )", "attribute blob ends early")]
    [DataRow(".custom instance void ObsoleteAttribute::.ctor(string) = { string('x') property int32 Missing = int32(1) }", "Missing")]
    public void Commit_InvalidCustomAttributePreservesPublishedRevision(string directive, string diagnostic)
    {
        var session = IlLines.Load(EditedCustomAttributeExamples.Source(isPrivate: true).Split('\n'));
        var edit = session.PrepareEdit("int32 Owner::Read(int32)", "Copy");
        session.CommitEdit(edit.Name, EditedCustomAttributeExamples.Method(isPrivate: true, edited: true));
        var method = edit.Method;
        var alias = session.TypeTable.MethodAliases[edit.Name];
        var source = edit.Source;
        var completion = session.CompletionRevision;
        var invalid = ".method private static int32 Read(int32 value) {\n" + directive + "\nldc.i4.s 99\nret\n}";
        var error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, invalid));
        Assert.Contains(diagnostic, error.Message);
        Assert.AreSame(method, edit.Method);
        Assert.AreSame(alias, session.TypeTable.MethodAliases[edit.Name]);
        Assert.AreEqual(source, edit.Source);
        Assert.AreEqual(completion, session.CompletionRevision);
        Assert.AreEqual(1, edit.Revision);
        AssertData(Assert.IsInstanceOfType<MethodInfo>(edit.Method), added: false);
        Assert.AreEqual(42, alias.Invoke(null, [42]));
        session.CommitEdit(edit.Name, source);
        Assert.AreEqual(2, edit.Revision);
        AssertData(Assert.IsInstanceOfType<MethodInfo>(edit.Method), added: false);
    }

    private static void AssertOriginal(MethodInfo method)
    {
        AssertOriginalAttribute(method.GetCustomAttributesData(), "method");
        AssertOriginalAttribute(method.ReturnParameter.GetCustomAttributesData(), "return");
        AssertOriginalAttribute(Assert.ContainsSingle(method.GetParameters()).GetCustomAttributesData(), "parameter");
        Assert.AreEqual(42, method.Invoke(null, [42]));
    }

    private static void AssertOriginalAttribute(IList<CustomAttributeData> attributes, string target)
    {
        var attribute = Assert.ContainsSingle(attributes);
        Assert.AreEqual(typeof(ObsoleteAttribute), attribute.AttributeType);
        Assert.AreEqual("original-" + target, Assert.ContainsSingle(attribute.ConstructorArguments).Value);
    }

    private static void AssertData(MethodInfo method, bool added, bool redefined = false)
    {
        AssertTarget(method.GetCustomAttributesData(), "method", ["method", "method", "after-instruction"], redefined);
        AssertTarget(method.ReturnParameter.GetCustomAttributesData(), "return", ["return"], redefined);
        var parameters = method.GetParameters();
        Assert.HasCount(added ? 2 : 1, parameters);
        AssertTarget(parameters[0].GetCustomAttributesData(), "parameter", ["parameter-1", "parameter-2"], redefined);
        if (added)
        {
            AssertTarget(parameters[1].GetCustomAttributesData(), null, ["added"], redefined);
        }
    }

    private static void AssertTarget(IList<CustomAttributeData> attributes, string? original, string[] names, bool redefined)
    {
        Assert.HasCount(names.Length + (original is null ? 0 : 1), attributes);
        if (original is not null)
        {
            AssertOriginalAttribute(attributes.Where(attribute => attribute.AttributeType == typeof(ObsoleteAttribute)).ToArray(),
                original);
        }

        var tags = attributes.Where(attribute => attribute.AttributeType != typeof(ObsoleteAttribute)).ToArray();
        var actualNames = tags.Select(tag => (string)tag.NamedArguments.Single(argument => argument.MemberName == "Name")
            .TypedValue.Value!);
        Assert.AreSequenceEqual(names.Order(StringComparer.Ordinal), actualNames.Order(StringComparer.Ordinal));
        foreach (var tag in tags)
        {
            Assert.HasCount(3, tag.ConstructorArguments);
            Assert.HasCount(2, tag.NamedArguments);
            var kind = Assert.IsInstanceOfType<Type>(tag.ConstructorArguments[0].Value);
            Assert.AreSame(tag.AttributeType.Assembly, kind.Assembly);
            Assert.IsNotNull(kind.GetField(redefined ? "Added" : "Original"));
            Assert.IsNull(kind.GetField(redefined ? "Original" : "Added"));
            Assert.AreEqual(kind.MakeArrayType(), tag.ConstructorArguments[1].Value);
            var array = Assert.IsInstanceOfType<IReadOnlyCollection<CustomAttributeTypedArgument>>(tag.ConstructorArguments[2].Value);
            Assert.AreSequenceEqual(new[] { kind, kind.MakeArrayType() }, array.Select(value => (Type)value.Value!));
            var field = tag.NamedArguments.Single(argument => argument.MemberName == "Kind");
            Assert.IsTrue(field.IsField);
            Assert.AreEqual(kind, field.TypedValue.Value);
        }
    }

    private static void AssertInstances(MethodInfo method, bool added, bool redefined = false)
    {
        var instances = method.GetCustomAttributes(false).Concat(method.ReturnParameter.GetCustomAttributes(false))
            .Concat(method.GetParameters().SelectMany(parameter => parameter.GetCustomAttributes(false)))
            .Where(attribute => attribute is not ObsoleteAttribute).ToArray();
        Assert.HasCount(added ? 7 : 6, instances);
        var actualNames = instances.Select(instance => (string)instance.GetType().GetProperty("Name")!.GetValue(instance)!);
        Assert.AreSequenceEqual((added ? AddedParameterTags : OriginalParameterTags).Order(StringComparer.Ordinal),
            actualNames.Order(StringComparer.Ordinal));
        foreach (var instance in instances)
        {
            var type = instance.GetType();
            var kind = Assert.IsInstanceOfType<Type>(type.GetField("Kind")!.GetValue(instance));
            Assert.AreSame(type.Assembly, kind.Assembly);
            Assert.IsNotNull(kind.GetField(redefined ? "Added" : "Original"));
            Assert.IsNull(kind.GetField(redefined ? "Original" : "Added"));
            Assert.AreEqual(kind.MakeArrayType(), type.GetField("Boxed")!.GetValue(instance));
            Assert.AreSequenceEqual(new[] { kind, kind.MakeArrayType() }, (Type[])type.GetField("Types")!.GetValue(instance)!);
        }
    }

    private static void AssertSimple(MethodBase method)
    {
        var obsolete = method.GetCustomAttributes<ObsoleteAttribute>().ToArray();
        Assert.HasCount(2, obsolete);
        Assert.Contains(attribute => attribute.Message == "old", obsolete);
        Assert.Contains(attribute => attribute.Message is null, obsolete);
        var usage = method.GetCustomAttribute<AttributeUsageAttribute>()!;
        Assert.AreEqual(AttributeTargets.Method, usage.ValidOn);
        Assert.IsTrue(usage.AllowMultiple);
        Assert.AreEqual(42, method.Invoke(null, null));
    }

    private static MethodInfo Exported(Assembly assembly, MethodBase source) => assembly.GetType(source.DeclaringType!.FullName!)!
        .GetMethod(source.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void AssertSide(ComparisonSide side, string expected)
    {
        Assert.AreEqual("completed", side.Outcome, side.Detail);
        Assert.IsNull(side.Exception);
        Assert.IsNotNull(side.Result);
        Assert.AreEqual("scalar", side.Result.Kind);
        Assert.AreEqual(expected, side.Result.Value);
        var invocation = Assert.ContainsSingle(side.Invocations);
        Assert.IsNull(invocation.Exception);
        Assert.AreEqual("42", invocation.Inputs.Single(member => member.Name == "argument 0").Value.Value);
        Assert.AreEqual("42", invocation.Outputs.Single(member => member.Name == "return").Value.Value);
    }
}
