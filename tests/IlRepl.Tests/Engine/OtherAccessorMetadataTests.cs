using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Real copied accessors retain property and event associations through reflection, execution, and both export formats.
/// </summary>
[TestClass]
public sealed class OtherAccessorMetadataTests
{
    private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Static | BindingFlags.Instance;

    /// <summary>
    /// Supplies cancellation for the comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Copied other accessors retain their standard accessors, visibility, and metadata dependencies.
    /// </summary>
    /// <param name="eventMember">Whether the selected method belongs to an event.</param>
    /// <param name="standard">Whether standard accessors are reachable from the selected method.</param>
    /// <param name="extra">Whether another associated method is reachable.</param>
    /// <param name="privateAccessor">Whether the selected accessor is private.</param>
    [TestMethod]
    [DataRow(false, false, false, false)]
    [DataRow(false, false, true, true)]
    [DataRow(false, true, true, false)]
    [DataRow(true, false, false, false)]
    [DataRow(true, false, true, true)]
    [DataRow(true, true, true, false)]
    public async Task Edit_OtherAccessors_PreserveTheirMetadata(bool eventMember, bool standard, bool extra, bool privateAccessor)
    {
        var session = new Session();
        var image = AccessorMetadataFixture.Create(eventMember, standard, extra, privateAccessor);
        var assembly = session.Resolver.LoadImage(image);
        var original = assembly.GetType("Owner")!;
        Assert.AreEqual(42, original.GetMethod("Read", Declared)!.Invoke(null, null));
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]Owner::Read()", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join("\n", edit.Problems));
        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.s 42", "ldc.i4.s 43", StringComparison.Ordinal));
        AssertRuntime(edit.OriginalMethod.DeclaringType!, eventMember, standard, extra, privateAccessor, 42);
        AssertRuntime(edit.Method!.DeclaringType!, eventMember, standard, extra, privateAccessor, 43);

        foreach (var exported in new[] { AssemblyExporter.Write(session, "other-accessors"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            AssertMetadata(exported, eventMember, standard, extra);
            var context = new AssemblyLoadContext("other-accessors-" + Guid.NewGuid(), isCollectible: true);
            try
            {
                var saved = context.LoadFromStream(new MemoryStream(exported));
                AssertRuntime(saved.GetType(edit.Method.DeclaringType!.FullName!)!, eventMember, standard, extra, privateAccessor, 43);
            }
            finally
            {
                context.Unload();
            }
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"), TestContext.CancellationToken);
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("42", result.Original.Result!.Value);
        Assert.AreEqual("43", result.Edited.Result!.Value);

        var again = session.PrepareEdit("Copy", "Again");
        Assert.IsEmpty(again.Problems, string.Join("\n", again.Problems));
        session.CommitEdit(again.Name, again.Source);
        AssertRuntime(again.Method!.DeclaringType!, eventMember, standard, extra, privateAccessor, 43);
    }

    private static void AssertRuntime(Type owner, bool eventMember, bool standard, bool extra, bool privateAccessor, int expected)
    {
        MemberInfo member;
        MethodInfo[] others;
        if (eventMember)
        {
            var entry = owner.GetEvent("Changed", Declared)!;
            Assert.IsNotNull(entry);
            Assert.IsNotNull(entry.GetAddMethod(true));
            Assert.IsNotNull(entry.GetRemoveMethod(true));
            Assert.AreEqual(standard, entry.GetRaiseMethod(true) is not null);
            others = entry.GetOtherMethods(true);
            member = entry;
        }
        else
        {
            var property = owner.GetProperty("Value", Declared)!;
            Assert.IsNotNull(property);
            Assert.IsNotNull(property.GetGetMethod(true));
            Assert.IsNotNull(property.GetSetMethod(true));
            others = property.GetAccessors(true).Where(method => method != property.GetGetMethod(true)
                && method != property.GetSetMethod(true)).ToArray();
            member = property;
        }

        Assert.AreSequenceEqual(extra ? ["Extra", "Read"] : ["Read"], others.Select(method => method.Name).Order().ToArray());
        var read = others.Single(method => method.Name == "Read");
        Assert.AreEqual(privateAccessor, read.IsPrivate);
        Assert.AreEqual(expected, read.Invoke(null, null));
        var attribute = member.GetCustomAttributesData().Single();
        var marker = (Type)attribute.ConstructorArguments.Single().Value!;
        Assert.AreSame(owner.Assembly, attribute.AttributeType.Assembly);
        Assert.AreSame(owner.Assembly, marker.Assembly);
        Assert.IsNotNull(member.GetCustomAttributes(inherit: false).Single());
    }

    private static void AssertMetadata(byte[] image, bool eventMember, bool standard, bool extra)
    {
        using var pe = new PEReader(new MemoryStream(image));
        var metadata = pe.GetMetadataReader();
        MethodDefinitionHandle[] others;
        if (eventMember)
        {
            var entry = metadata.GetEventDefinition(metadata.EventDefinitions.Single());
            var accessors = entry.GetAccessors();
            Assert.IsFalse(accessors.Adder.IsNil);
            Assert.IsFalse(accessors.Remover.IsNil);
            Assert.AreEqual(!standard, accessors.Raiser.IsNil);
            Assert.AreEqual("Action", metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)entry.Type).Name));
            others = [.. accessors.Others];
        }
        else
        {
            var property = metadata.GetPropertyDefinition(metadata.PropertyDefinitions.Single());
            var accessors = property.GetAccessors();
            Assert.IsFalse(accessors.Getter.IsNil);
            Assert.IsFalse(accessors.Setter.IsNil);
            var signature = metadata.GetBlobReader(property.Signature);
            Assert.AreEqual(8, signature.ReadByte());
            Assert.AreEqual(0, signature.ReadCompressedInteger());
            Assert.AreEqual(0x12, signature.ReadByte());
            Assert.AreEqual(HandleKind.TypeDefinition, signature.ReadTypeHandle().Kind);
            others = [.. accessors.Others];
        }

        var names = others.Select(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name)).Order().ToArray();
        Assert.AreSequenceEqual(extra ? ["Extra", "Read"] : ["Read"], names);
    }
}
