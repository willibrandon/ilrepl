using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// SAFEARRAY subtype identities follow copied private declarations through comparisons and independent exports.
/// </summary>
[TestClass]
public sealed class SafeArrayDependencyTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Native subtype strings retain their generic and array shapes while referring to the copied private definition.
    /// </summary>
    /// <param name="target">The field, parameter, or return descriptor.</param>
    /// <param name="generic">Whether the subtype includes a generic collection and an array.</param>
    [TestMethod]
    [DataRow("field", false)]
    [DataRow("parameter", false)]
    [DataRow("return", false)]
    [DataRow("field", true)]
    [DataRow("parameter", true)]
    [DataRow("return", true)]
    public async Task Commit_SafeArraySubtypeFollowsCopiedIdentity(string target, bool generic)
    {
        var session = new Session();
        session.Resolver.LoadImage(SafeArrayMetadataFixture.Create(target, generic));
        var edit = session.PrepareEdit("object[] Owner::Read(object[])", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, ".method public static object[] Read(object[] items) {\nldc.i4.s 43\nnewarr object\nret\n}");
        Assert.Contains(dependency => dependency.Location.Contains("marshalling", StringComparison.Ordinal)
            && dependency.Disposition.StartsWith("copied", StringComparison.Ordinal), edit.Dependencies);
        var input = new object[42];
        Assert.AreSame(input, edit.OriginalMethod.Invoke(null, [input]));
        Assert.HasCount(43, (object[])edit.Method!.Invoke(null, [input])!);
        AssertSubtype(edit.Method.DeclaringType!, target, generic);
        foreach (var line in IlLines.Expand(
            ".method int32 Scenario() { ldc.i4.s 42; newarr object; call Copy; ldlen; conv.i4; ret }"))
        {
            session.AddLine(line);
        }

        var comparison = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", comparison.Outcome, comparison.Original.Detail + "; " + comparison.Edited.Detail);
        Assert.AreEqual("42", comparison.Original.Result!.Value);
        Assert.AreEqual("43", comparison.Edited.Result!.Value);
        session.AddLine("call Scenario");
        foreach (var image in new[] { AssemblyExporter.Write(session, "safe-array"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("safe-array-export", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(43, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                AssertSubtype(assembly.GetType(edit.Method.DeclaringType!.FullName!)!, target, generic);
            }
            finally
            {
                context.Unload();
            }
        }

        var again = session.PrepareEdit("Copy", "Again");
        Assert.IsEmpty(again.Problems, string.Join('\n', again.Problems));
        session.CommitEdit(again.Name, again.Source);
        AssertSubtype(again.Method!.DeclaringType!, target, generic);
        Assert.HasCount(43, (object[])again.Method.Invoke(null, [input])!);
    }

    private static void AssertSubtype(Type owner, string target, bool generic)
    {
        var method = owner.GetMethod("Read")!;
        var marshal = target == "field" ? owner.GetField("Items")!.GetCustomAttribute<MarshalAsAttribute>()!
            : (target == "return" ? method.ReturnParameter : method.GetParameters().Single()).GetCustomAttribute<MarshalAsAttribute>()!;
        Assert.AreEqual(UnmanagedType.SafeArray, marshal.Value);
        var metadata = ModuleMetadata.TryOpen(owner.Module)!;
        var descriptor = target == "field" ? metadata.GetFieldDefinition(
            MetadataTokens.FieldDefinitionHandle(owner.GetField("Items")!.MetadataToken & 0x00ffffff)).GetMarshallingDescriptor()
            : metadata.GetParameter(metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(method.MetadataToken & 0x00ffffff))
                .GetParameters().Single(handle => metadata.GetParameter(handle).SequenceNumber == (target == "return" ? 0 : 1)))
                .GetMarshallingDescriptor();
        var reader = metadata.GetBlobReader(descriptor);
        Assert.AreEqual((byte)0x1d, reader.ReadByte());
        Assert.AreEqual((int)VarEnum.VT_UNKNOWN, reader.ReadCompressedInteger());
        var subtype = Type.GetType(reader.ReadSerializedString()!, identity => identity.FullName == owner.Assembly.FullName
            ? owner.Assembly : Assembly.Load(identity), null, throwOnError: true)!;
        Assert.AreEqual(0, reader.RemainingBytes);
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(VarEnum.VT_UNKNOWN, marshal.SafeArraySubType);
            Assert.AreEqual(subtype, marshal.SafeArrayUserDefinedSubType);
        }
        if (generic)
        {
            Assert.AreEqual(typeof(List<>), subtype.GetGenericTypeDefinition());
            Assert.IsTrue(subtype.GetGenericArguments().Single().IsSZArray);
            subtype = subtype.GetGenericArguments().Single().GetElementType()!;
        }

        Assert.AreSame(owner.Assembly, subtype.Assembly);
        Assert.AreSame(owner, subtype.DeclaringType);
        Assert.IsTrue(subtype.IsNestedPrivate);
        Assert.AreEqual("Hidden\\+Record", subtype.Name);
        Assert.AreEqual(typeof(int), subtype.GetField("Value")!.FieldType);
    }
}
