using System.Reflection;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Properties and events declared with their accessor directives are real properties and
/// events on the live type.
/// </summary>
[TestClass]
public sealed class PropertyEventTests
{
    private static Session Load(params string[] lines) => IlLines.Load(lines);

    private static object? Run(Session session, params string[] lines)
    {
        foreach (var line in IlLines.Expand(lines))
        {
            session.AddLine(line);
        }

        return session.Run().Value;
    }

    /// <summary>
    /// A property with a getter and a setter, and an indexer with a parameter.
    /// </summary>
    [TestMethod]
    public void Property_GetterSetterAndIndexer()
    {
        var session = Load(
            ".class public Sized {",
            ".field private int32 _length",
            ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }",
            ".method public specialname instance int32 get_Length() { ldarg.0; ldfld int32 Sized::_length; ret }",
            ".method public specialname instance void set_Length(int32 v) { ldarg.0; ldarg v; stfld int32 Sized::_length; ret }",
            ".method public specialname instance int32 get_Item(int32 i) { ldarg i; ldc.i4 2; mul; ret }",
            ".property instance int32 Length() {",
            ".get instance int32 Sized::get_Length()",
            ".set instance void Sized::set_Length(int32)",
            "}",
            ".property instance int32 Item(int32) {",
            ".get instance int32 Sized::get_Item(int32)",
            "}",
            "}");
        var sized = session.Types[0].RuntimeType!;
        var length = sized.GetProperty("Length")!;
        Assert.AreEqual("get_Length", length.GetMethod!.Name);
        Assert.AreEqual("set_Length", length.SetMethod!.Name);
        var item = sized.GetProperty("Item")!;
        Assert.HasCount(1, item.GetIndexParameters());
        Assert.IsNull(item.SetMethod);
        var instance = Run(session, "newobj instance void Sized::.ctor()", "dup", "ldc.i4 4", "call instance void Sized::set_Length(int32)")!;
        Assert.AreEqual(4, length.GetValue(instance));
        Assert.AreEqual(6, item.GetValue(instance, [3]));
    }

    /// <summary>
    /// An event with add and remove accessors, and a raise method.
    /// </summary>
    [TestMethod]
    public void Event_AddRemoveAndFire()
    {
        var session = Load(
            ".class public Button {",
            ".field private class [System.Runtime]System.EventHandler _click",
            ".method public specialname instance void add_Click(class [System.Runtime]System.EventHandler h) { ldarg.0; ldarg.0; ldfld class [System.Runtime]System.EventHandler Button::_click; ldarg h; call class [System.Runtime]System.Delegate [System.Runtime]System.Delegate::Combine(class [System.Runtime]System.Delegate, class [System.Runtime]System.Delegate); castclass [System.Runtime]System.EventHandler; stfld class [System.Runtime]System.EventHandler Button::_click; ret }",
            ".method public specialname instance void remove_Click(class [System.Runtime]System.EventHandler h) { ldarg.0; ldarg.0; ldfld class [System.Runtime]System.EventHandler Button::_click; ldarg h; call class [System.Runtime]System.Delegate [System.Runtime]System.Delegate::Remove(class [System.Runtime]System.Delegate, class [System.Runtime]System.Delegate); castclass [System.Runtime]System.EventHandler; stfld class [System.Runtime]System.EventHandler Button::_click; ret }",
            ".method public specialname instance void raise_Click(object s, class [System.Runtime]System.EventArgs e) { ret }",
            ".event [System.Runtime]System.EventHandler Click {",
            ".addon instance void Button::add_Click(class [System.Runtime]System.EventHandler)",
            ".removeon instance void Button::remove_Click(class [System.Runtime]System.EventHandler)",
            ".fire instance void Button::raise_Click(object, class [System.Runtime]System.EventArgs)",
            "}",
            "}");
        var button = session.Types[0].RuntimeType!;
        var click = button.GetEvent("Click")!;
        Assert.AreEqual(typeof(EventHandler), click.EventHandlerType);
        Assert.AreEqual("add_Click", click.AddMethod!.Name);
        Assert.AreEqual("remove_Click", click.RemoveMethod!.Name);
        Assert.AreEqual("raise_Click", click.RaiseMethod!.Name);
        Assert.IsTrue(click.AddMethod.IsSpecialName);
    }

    /// <summary>
    /// A static property lives on the type, and its declaration is listed.
    /// </summary>
    [TestMethod]
    public void StaticProperty_OnTheType()
    {
        var session = Load(
            ".class public Config {",
            ".method public specialname static int32 get_Max() { ldc.i4 99; ret }",
            ".property int32 Max() {",
            ".get int32 Config::get_Max()",
            "}",
            "}");
        var max = session.Types[0].RuntimeType!.GetProperty("Max", BindingFlags.Public | BindingFlags.Static)!;
        Assert.AreEqual(99, max.GetValue(null));
        Assert.AreEqual("static property int32 Max { get }", session.Types[0].Declaration.Properties[0].Describe());
    }

    /// <summary>
    /// A plain indexed-property declaration without exact annotation entries retains its runtime signature.
    /// </summary>
    [TestMethod]
    public void IndexedProperty_WithoutExactTypes_UsesRuntimeSignature()
    {
        var property = new PropertyDeclaration(
            "Item", typeof(int), [typeof(string)], false, PropertyAttributes.None,
            null, null, [], null, false, [], ".property int32 Item(string)", []);

        Assert.AreEqual("property int32 Item(string) {  }", property.Describe());
    }
}
