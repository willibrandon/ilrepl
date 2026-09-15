using IlRepl.Engine;
using IlRepl.Host;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Copied owners remain valid type arguments for ordinary external generic methods and fields.
/// </summary>
[TestClass]
public sealed class MethodEditGenericBoundaryTests
{
    /// <summary>
    /// The cancellation context for real comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// List and StrongBox members bind through their generic slots to the copied type and execute in both workers.
    /// </summary>
    /// <returns>The completed generic binding and runtime assertions.</returns>
    [TestMethod]
    public async Task Copy_ExternalGenericSlotsAcceptCopiedOwner()
    {
        var session = IlLines.Load("""
            .class public Item {
              .method public specialname rtspecialname instance void .ctor() {
                ldarg.0
                call instance void Object::.ctor()
                ret
              }
              .method public static int32 Count() {
                newobj instance void class List<class Item>::.ctor()
                dup
                newobj instance void class StrongBox<class Item>::.ctor()
                dup
                newobj instance void Item::.ctor()
                stfld class Item class StrongBox<class Item>::Value
                ldfld class Item class StrongBox<class Item>::Value
                callvirt instance void class List<class Item>::Add(class Item)
                callvirt instance int32 class List<class Item>::get_Count()
                ret
              }
            }
            """.Split('\n'));
        var edit = session.PrepareEdit("int32 Item::Count()", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(1, edit.OriginalMethod.Invoke(null, null));
        Assert.AreEqual(1, edit.Method!.Invoke(null, null));
        session.AddLine("call Copy");
        Assert.AreEqual(1, session.Run().Value);

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"), TestContext.CancellationToken);

        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("1", result.Original.Result!.Value);
        Assert.AreEqual("1", result.Edited.Result!.Value);
        Assert.HasCount(1, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);
    }
}
