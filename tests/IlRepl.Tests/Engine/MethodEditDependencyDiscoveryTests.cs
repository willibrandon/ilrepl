using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilFieldAttributes = Mono.Cecil.FieldAttributes;
using CecilMethodAttributes = Mono.Cecil.MethodAttributes;
using CecilTypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Dependency discovery revisits retained calls when a later dependency requires copying their declaring owner.
/// </summary>
[TestClass]
public sealed class MethodEditDependencyDiscoveryTests
{
    /// <summary>
    /// A later internal helper requires an earlier public call on the same owner to join the executable copied family.
    /// </summary>
    /// <param name="indirect">Whether a queued helper discovers the internal dependency after scanning the selected method.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Commit_LateCopiedOwnerIncludesPreviouslyExternalCalls(bool indirect)
    {
        var session = new Session();
        var (assembly, _, original) = CecilFixture.Build((module, selectedOwner) =>
        {
            var helperOwner = new TypeDefinition("N", "Helpers", CecilTypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(helperOwner);
            var state = new FieldDefinition("State", CecilFieldAttributes.Private | CecilFieldAttributes.Static, module.TypeSystem.Int32);
            helperOwner.Fields.Add(state);
            var initializer = Method(helperOwner, ".cctor", CecilMethodAttributes.Private | CecilMethodAttributes.SpecialName
                | CecilMethodAttributes.RTSpecialName);
            initializer.ReturnType = module.TypeSystem.Void;
            initializer.Body.GetILProcessor().Emit(OpCodes.Ldc_I4, 41);
            initializer.Body.GetILProcessor().Emit(OpCodes.Stsfld, state);
            initializer.Body.GetILProcessor().Emit(OpCodes.Ret);
            var first = Method(helperOwner, "First", CecilMethodAttributes.Public);
            first.Body.GetILProcessor().Emit(OpCodes.Ldsfld, state);
            first.Body.GetILProcessor().Emit(OpCodes.Ret);
            var second = Method(helperOwner, "Second", CecilMethodAttributes.Assembly);
            var update = second.Body.GetILProcessor();
            update.Emit(OpCodes.Ldsfld, state);
            update.Emit(OpCodes.Ldc_I4_1);
            update.Emit(OpCodes.Add);
            update.Emit(OpCodes.Stsfld, state);
            update.Emit(OpCodes.Ldc_I4_1);
            update.Emit(OpCodes.Ret);
            var late = second;
            if (indirect)
            {
                late = new MethodDefinition("Late", CecilMethodAttributes.Private | CecilMethodAttributes.Static, module.TypeSystem.Int32);
                selectedOwner.Methods.Add(late);
                late.Body.GetILProcessor().Emit(OpCodes.Call, second);
                late.Body.GetILProcessor().Emit(OpCodes.Ret);
            }

            var read = new MethodDefinition("Read", CecilMethodAttributes.Public | CecilMethodAttributes.Static, module.TypeSystem.Int32);
            selectedOwner.Methods.Add(read);
            var il = read.Body.GetILProcessor();
            il.Emit(OpCodes.Call, first);
            il.Emit(OpCodes.Call, late);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ret);
        }, session.Resolver);

        Assert.AreEqual(42, original.GetMethod("Read")!.Invoke(null, null));
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]N.Fixture::Read()", "Copy");

        Assert.IsEmpty(edit.Problems, string.Join("\n", edit.Problems));
        Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, null));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        AssertCopiedHelpers(edit.Method.Module.Assembly);
        foreach (var image in new[] { AssemblyExporter.Write(session, "late-owner"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("late-owner-" + Guid.NewGuid(), isCollectible: true);
            try
            {
                var exported = context.LoadFromStream(new MemoryStream(image));
                var owner = exported.GetType(edit.Method.DeclaringType!.FullName!, throwOnError: true)!;
                Assert.AreEqual(42, owner.GetMethod("Read")!.Invoke(null, null));
                AssertCopiedHelpers(exported);
            }
            finally
            {
                context.Unload();
            }
        }
    }

    private static MethodDefinition Method(TypeDefinition owner, string name, CecilMethodAttributes visibility)
    {
        var method = new MethodDefinition(name, visibility | CecilMethodAttributes.Static, owner.Module.TypeSystem.Int32);
        owner.Methods.Add(method);
        return method;
    }

    private static void AssertCopiedHelpers(Assembly assembly)
    {
        var first = assembly.GetTypes().Select(type => type.GetMethod("First")).Single(method => method is not null)!;
        Assert.AreEqual(42, first.Invoke(null, null));
        var second = first.DeclaringType!.GetMethod("Second", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsTrue(second.IsAssembly);
        Assert.AreEqual(1, second.Invoke(null, null));
        Assert.AreEqual(43, first.Invoke(null, null));
    }
}
