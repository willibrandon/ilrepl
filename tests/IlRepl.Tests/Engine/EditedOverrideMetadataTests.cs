using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Edited explicit mappings control real interface and base dispatch without changing pinned originals.
/// </summary>
[TestClass]
public sealed class EditedOverrideMetadataTests
{
    /// <summary>
    /// Supplies cancellation for actual isolated workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A mapping replaces the pinned slot, repeated directives are idempotent, and removing them restores the original.
    /// </summary>
    /// <param name="generic">Whether the declaration is a constructed interface on a generic owner.</param>
    /// <param name="slot">The exact slots to redirect.</param>
    [TestMethod]
    [DataRow(false, "interface")]
    [DataRow(false, "base")]
    [DataRow(false, "both")]
    [DataRow(false, "object")]
    [DataRow(true, "interface")]
    [DataRow(true, "base")]
    [DataRow(true, "both")]
    public void Commit_OverridesReplaceOnlySelectedSlotsAndRestoreAcrossRevisions(bool generic, string slot)
    {
        var session = IlLines.Load(EditedOverrideExamples.Source(generic).Split('\n'));
        var edit = session.PrepareEdit(EditedOverrideExamples.Reference(generic), "Copy");
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        AssertDispatch(edit.Original.Requested.DeclaringType!, "none");
        AssertDispatch(edit.OriginalMethod.DeclaringType!, "none");
        session.CommitEdit(edit.Name, EditedOverrideExamples.Method(generic, slot, repeat: true));
        AssertDispatch(edit.Method!.DeclaringType!, slot);
        AssertDispatch(edit.OriginalMethod.DeclaringType!, "none");
        Assert.AreEqual(1, edit.Revision);
        AssertExports(session, edit, slot);
        session.CommitEdit(edit.Name, EditedOverrideExamples.Method(generic, slot));
        Assert.AreEqual(2, edit.Revision);
        AssertDispatch(edit.Method!.DeclaringType!, slot);
        AssertMappingCount(AssemblyExporter.Write(session, "repeated-override"), slot == "object" ? 4 : 3);
        session.CommitEdit(edit.Name, EditedOverrideExamples.Method(generic, "none"));
        Assert.AreEqual(3, edit.Revision);
        AssertDispatch(edit.Method!.DeclaringType!, "none");
        AssertMappingCount(AssemblyExporter.Write(session, "restored-override"), 3);
        AssertDispatch(edit.Original.Requested.DeclaringType!, "none");
    }

    /// <summary>
    /// Isolated workers observe changed dispatch through the copied slot while the directly selected body stays identical.
    /// </summary>
    /// <param name="generic">Whether the owner is closed over int32.</param>
    /// <param name="slot">The interface or base dispatch path.</param>
    [TestMethod]
    [DataRow(false, "interface")]
    [DataRow(false, "base")]
    [DataRow(true, "interface")]
    [DataRow(true, "base")]
    public async Task Compare_OverridesChangeActualDispatch(bool generic, string slot)
    {
        var session = IlLines.Load(EditedOverrideExamples.Source(generic).Split('\n'));
        var edit = session.PrepareEdit(EditedOverrideExamples.Reference(generic), "Copy");
        session.CommitEdit(edit.Name, EditedOverrideExamples.Method(generic, slot));
        foreach (var line in EditedOverrideExamples.Scenario(generic, slot).Split('\n')) session.AddLine(line);
        var reply = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", reply.Outcome, reply.Original.Detail + "; " + reply.Edited.Detail);
        AssertSide(reply.Original, "42");
        AssertSide(reply.Edited, "43");
        Assert.AreEqual(43, session.Methods.Single(method => method.Signature.Name == "Scenario").Version.Body.Invoke(null, null));
        AssertDispatch(edit.Original.Requested.DeclaringType!, "none");
    }

    /// <summary>
    /// Invalid targets leave the published executable, source, alias and revision intact before a valid correction.
    /// </summary>
    /// <param name="mapping">The invalid explicit target.</param>
    /// <param name="diagnostic">The actionable diagnostic fragment.</param>
    [TestMethod]
    [DataRow("method instance void IDisposable::Dispose()", "does not match")]
    [DataRow("method instance class Type Object::GetType()", "virtual")]
    [DataRow("IUnrelated::Read", "bases or interfaces")]
    public void Commit_InvalidOverridePreservesPublishedRevision(string mapping, string diagnostic)
    {
        var session = IlLines.Load((EditedOverrideExamples.Source(false)
            + "\n.class public interface abstract IUnrelated {\n"
            + ".method public virtual newslot abstract instance int32 Read() {}\n}").Split('\n'));
        var edit = session.PrepareEdit(EditedOverrideExamples.Reference(false), "Copy");
        session.CommitEdit(edit.Name, EditedOverrideExamples.Method(false, "interface"));
        var method = edit.Method;
        var alias = session.TypeTable.MethodAliases[edit.Name];
        var source = edit.Source;
        var completion = session.CompletionRevision;
        var invalid = EditedOverrideExamples.Method(false, "none").Replace("ldc.i4.s 43", ".override " + mapping
            + "\nldc.i4.s 99", StringComparison.Ordinal);
        var error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, invalid));
        Assert.Contains(diagnostic, error.Message);
        Assert.AreSame(method, edit.Method);
        Assert.AreSame(alias, session.TypeTable.MethodAliases[edit.Name]);
        Assert.AreEqual(source, edit.Source);
        Assert.AreEqual(completion, session.CompletionRevision);
        Assert.AreEqual(1, edit.Revision);
        AssertDispatch(edit.Method!.DeclaringType!, "interface");
        session.CommitEdit(edit.Name, EditedOverrideExamples.Method(false, "base"));
        Assert.AreEqual(2, edit.Revision);
        AssertDispatch(edit.Method!.DeclaringType!, "base");
    }

    /// <summary>
    /// A matching final virtual base slot rejects an edit atomically while the original inherited dispatch stays executable.
    /// </summary>
    [TestMethod]
    public void Commit_FinalBaseSlotRejectsWithoutChangingInheritedDispatch()
    {
        var session = IlLines.Load("""
            .class public FinalBase {
              .method public instance void .ctor() {
                ldarg.0
                call instance void Object::.ctor()
                ret
              }
              .method public virtual final newslot instance int32 Read() {
                ldc.i4.s 42
                ret
              }
            }
            .class public FinalOwner extends FinalBase {
              .method public instance void .ctor() {
                ldarg.0
                call instance void FinalBase::.ctor()
                ret
              }
              .method public virtual newslot instance int32 Alternate() {
                ldc.i4.s 43
                ret
              }
            }
            """.Split('\n'));
        var edit = session.PrepareEdit("instance int32 FinalOwner::Alternate()", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var method = edit.Method!;
        var alias = session.TypeTable.MethodAliases[edit.Name];
        var source = edit.Source;
        var completion = session.CompletionRevision;
        const string invalid = ".method public virtual newslot instance int32 Alternate() {\n"
            + ".override FinalBase::Read\nldc.i4.s 99\nret\n}";
        var error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, invalid));
        Assert.Contains("is final and cannot be overridden", error.Message);
        Assert.AreSame(method, edit.Method);
        Assert.AreSame(alias, session.TypeTable.MethodAliases[edit.Name]);
        Assert.AreEqual(source, edit.Source);
        Assert.AreEqual(completion, session.CompletionRevision);
        Assert.AreEqual(1, edit.Revision);
        foreach (var selected in new[] { method, edit.Original.Requested, edit.OriginalMethod })
        {
            var owner = selected.DeclaringType!;
            var instance = Activator.CreateInstance(owner);
            Assert.AreEqual(43, selected.Invoke(instance, null));
            Assert.AreEqual(42, owner.BaseType!.GetMethod("Read")!.Invoke(instance, null));
        }
    }

    private static void AssertDispatch(Type owner, string slot)
    {
        var instance = Activator.CreateInstance(owner)!;
        var contract = owner.GetInterfaces().Single(type => type.IsGenericType && type.GetMethod("Read") is not null);
        var redirected = slot is "interface" or "both";
        Assert.AreEqual(redirected ? 43 : 42, contract.GetMethod("Read")!.Invoke(instance, null));
        var map = owner.GetInterfaceMap(contract);
        Assert.AreEqual(redirected ? "Alternate" : "Original", Assert.ContainsSingle(map.TargetMethods).Name);
        Assert.AreEqual(slot is "base" or "both" ? 43 : 42, owner.BaseType!.GetMethod("Read")!.Invoke(instance, null));
        var keep = owner.GetInterfaces().Single(type => type.GetMethod("Keep") is not null);
        Assert.AreEqual(7, keep.GetMethod("Keep")!.Invoke(instance, null));
        Assert.AreEqual("Keep", Assert.ContainsSingle(owner.GetInterfaceMap(keep).TargetMethods).Name);
        Assert.AreEqual(43, owner.GetMethod("Alternate")!.Invoke(instance, null));
        if (slot == "object") Assert.AreEqual(43, instance.GetHashCode());
        if (owner.IsGenericType)
            Assert.AreSequenceEqual(new[] { typeof(int) }, contract.GetGenericArguments());
    }

    private static void AssertExports(Session session, MethodEdit edit, string slot)
    {
        foreach (var image in new[] { AssemblyExporter.Write(session, "override-edit"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            AssertMappingCount(image, slot == "object" ? 4 : 3);
            var context = new AssemblyLoadContext("override-edit", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                var original = edit.Method!.DeclaringType!;
                var owner = assembly.GetType(original.IsGenericType ? original.GetGenericTypeDefinition().FullName! : original.FullName!)!;
                if (owner.IsGenericTypeDefinition) owner = owner.MakeGenericType(typeof(int));
                AssertDispatch(owner, slot);
            }
            finally
            {
                context.Unload();
            }
        }
    }

    private static void AssertMappingCount(byte[] image, int expected)
    {
        using var pe = new PEReader(new MemoryStream(image));
        var metadata = pe.GetMetadataReader();
        var owner = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
            .Single(type => metadata.GetString(type.Namespace) == "IlRepl.Edits.Copy"
                && metadata.GetString(type.Name) is "Owner" or "Owner`1");
        Assert.HasCount(expected, owner.GetMethodImplementations());
    }

    private static void AssertSide(ComparisonSide side, string expected)
    {
        Assert.AreEqual("completed", side.Outcome, side.Detail);
        Assert.IsNull(side.Exception);
        Assert.IsNotNull(side.Result);
        Assert.AreEqual("scalar", side.Result.Kind);
        Assert.AreEqual(expected, side.Result.Value);
        var invocation = Assert.ContainsSingle(side.Invocations);
        Assert.IsNull(invocation.Exception);
        Assert.AreEqual("43", invocation.Outputs.Single(member => member.Name == "return").Value.Value);
    }
}
