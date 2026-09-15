namespace IlRepl.Tests.Shared;

/// <summary>
/// Supplies separate session declarations whose custom attributes depend on copied types and named property accessors.
/// </summary>
public static class AttributeDependencyExamples
{
    /// <summary>
    /// Creates an attribute with type, boxed type, array, and named property values on several metadata targets.
    /// </summary>
    /// <returns>The complete session declarations.</returns>
    public static string Source()
    {
        const string attribute = ".custom instance void Tag::.ctor(class Type, object, class Type[]) = { "
            + "type(Marker) type(Marker[]) { type(Marker) type(Marker[]) } property string Name = string('saved') }\n";
        return """
            .class public Marker {
              .field public int32 Original
            }
            .class public Tag extends Attribute {
              .field public static int32 Runs
              .field public class Type Kind
              .field public object Boxed
              .field public class Type[] Types
              .field private string _name
              .method public instance void .ctor(class Type kind, object boxed, class Type[] types) {
                ldarg.0
                call instance void Attribute::.ctor()
                ldsfld int32 Tag::Runs
                ldc.i4.1
                add
                stsfld int32 Tag::Runs
                ldarg.0
                ldarg.1
                stfld class Type Tag::Kind
                ldarg.0
                ldarg.2
                stfld object Tag::Boxed
                ldarg.0
                ldarg.3
                stfld class Type[] Tag::Types
                ret
              }
              .method public instance string get_Name() {
                ldarg.0
                ldfld string Tag::_name
                ret
              }
              .method public instance void set_Name(string value) {
                ldarg.0
                ldarg.1
                stfld string Tag::_name
                ret
              }
              .property instance string Name() {
                .get instance string Tag::get_Name()
                .set instance void Tag::set_Name(string)
              }
            }
            .class public Tagged {
            """ + "\n" + attribute + ".field public static int32 Value\n" + attribute
            + ".method public static int32 Read(int32 value) {\n" + attribute + ".param [0]\n" + attribute + ".param [1]\n"
            + attribute + "ldarg.0\nret\n}\n}";
    }
}
