using System.Runtime.CompilerServices;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Weak identity scopes preserve reference continuity while each phase captures current fields and independent traversal state.
/// </summary>
[TestClass]
public sealed class StructuralObservationIdentityTests
{
    /// <summary>
    /// The same object keeps its identity while changed fields and equal replacement children receive fresh structural snapshots.
    /// </summary>
    [TestMethod]
    public void Capture_NextPhaseRetainsIdentityAndRecapturesMutatedFields()
    {
        var child = new ObservationIdentityNode { Number = 7 };
        var root = new ObservationIdentityNode { Number = 42, Child = child };
        var observer = new StructuralObservation(new Dictionary<string, string>());
        var before = observer.Capture(root);
        root.Number = 43;
        root.Child = new ObservationIdentityNode { Number = 7 };
        Assert.AreNotSame(child, root.Child);
        var next = new StructuralObservation(new Dictionary<string, string>(), new ObservationIdentityMap(observer.Identities));
        var after = next.Capture(root);
        Assert.AreEqual("object", before.Kind);
        Assert.AreEqual("object", after.Kind);
        Assert.AreEqual(before.Identity, after.Identity);
        Assert.AreEqual("42", Field(before, "Number").Value);
        Assert.AreEqual("43", Field(after, "Number").Value);
        var originalChild = Field(before, "Child");
        var replacement = Field(after, "Child");
        Assert.AreEqual(originalChild.Type, replacement.Type);
        Assert.AreEqual("7", Field(originalChild, "Number").Value);
        Assert.AreEqual("7", Field(replacement, "Number").Value);
        Assert.AreNotEqual(originalChild.Identity, replacement.Identity);
        Assert.HasCount(2, after.Members);
        Assert.AreEqual("42", Field(before, "Number").Value);
    }

    /// <summary>
    /// Retained scalar references preserve their identities while distinct equal strings and boxes receive different identities.
    /// </summary>
    [TestMethod]
    public void Capture_NextPhaseDistinguishesEqualScalarReferencesAndRetainedAliases()
    {
        var text = new string('x', 1);
        object number = 42;
        object[] values = [text, number, text, number];
        var observer = new StructuralObservation(new Dictionary<string, string>());
        var before = observer.Capture(values);
        values[0] = new string('x', 1);
        values[1] = 42;
        Assert.AreNotSame(values[0], values[2]);
        Assert.AreNotSame(values[1], values[3]);
        var next = new StructuralObservation(new Dictionary<string, string>(), new ObservationIdentityMap(observer.Identities));
        var after = next.Capture(values);
        Assert.AreEqual("array", after.Kind);
        Assert.AreEqual(before.Identity, after.Identity);
        Assert.HasCount(4, after.Members);
        Assert.AreSequenceEqual(before.Members.Select(member => member.Value.Value), after.Members.Select(member => member.Value.Value));
        foreach (var index in new[] { 0, 1 })
        {
            Assert.AreNotEqual(before.Members[index].Value.Identity, after.Members[index].Value.Identity);
        }

        foreach (var index in new[] { 2, 3 })
        {
            Assert.AreEqual(before.Members[index].Value.Identity, after.Members[index].Value.Identity);
        }

        foreach (var member in after.Members)
        {
            Assert.AreEqual("scalar", member.Value.Kind);
            Assert.IsEmpty(member.Value.Members);
        }
    }

    /// <summary>
    /// Cycles are traversed afresh in each phase and terminate at a reference to that phase's complete root.
    /// </summary>
    [TestMethod]
    public void Capture_NextPhasePreservesCyclesAndCurrentState()
    {
        var root = new ObservationIdentityNode { Number = 42 };
        root.Child = root;
        var observer = new StructuralObservation(new Dictionary<string, string>());
        var before = observer.Capture(root);
        root.Number = 43;
        var next = new StructuralObservation(new Dictionary<string, string>(), new ObservationIdentityMap(observer.Identities));
        var after = next.Capture(root);
        Assert.AreEqual(before.Identity, after.Identity);
        Assert.AreEqual("object", after.Kind);
        Assert.AreEqual("43", Field(after, "Number").Value);
        var cycle = Field(after, "Child");
        Assert.AreEqual("reference", cycle.Kind);
        Assert.AreEqual(after.Identity, cycle.Identity);
        Assert.IsEmpty(cycle.Members);
        Assert.AreEqual("42", Field(before, "Number").Value);
    }

    /// <summary>
    /// Retaining a completed phase's identity scope does not retain the observed user object or its graph.
    /// </summary>
    [TestMethod]
    public void Capture_IdentityScopeDoesNotKeepUserObjectsAlive()
    {
        var (scope, reference) = CaptureCollectible();
        FullCollection.Run();
        Assert.IsFalse(reference.IsAlive, "The identity scope must not keep a completed invocation's input alive.");
        GC.KeepAlive(scope);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ObservationIdentityMap Scope, WeakReference Reference) CaptureCollectible()
    {
        var value = new ObservationIdentityNode { Number = 42, Child = new ObservationIdentityNode { Number = 7 } };
        var observer = new StructuralObservation(new Dictionary<string, string>());
        var snapshot = observer.Capture(value);
        Assert.AreEqual("42", Field(snapshot, "Number").Value);
        return (observer.Identities, new WeakReference(value));
    }

    private static ObservedValue Field(ObservedValue value, string name) =>
        value.Members.Single(member => member.Name.EndsWith("::" + name, StringComparison.Ordinal)).Value;
}
