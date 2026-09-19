namespace IlRepl.Tests.Shared;

/// <summary>
/// Supplies reflection lookups whose private members have no direct reference from the selected method.
/// </summary>
public static class ReflectionDependencyExamples
{
    /// <summary>
    /// Declares private methods, a constructor, a property, and a nested type used through runtime reflection.
    /// </summary>
    /// <param name="lookup">The method, enumeration, property, constructor, or nested type lookup.</param>
    /// <returns>The complete session declarations.</returns>
    public static string Source(string lookup)
    {
        const string owner = "ldtoken Owner\ncall class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\n";
        const string create = owner + "ldc.i4.1\ncall object Activator::CreateInstance(class Type, bool)\n";
        const string invoke = "ldnull\nldnull\ncallvirt instance object MethodBase::Invoke(object, object[])\nunbox.any int32\n";
        var body = lookup switch
        {
            "runtime" => "newobj instance void Owner::.ctor()\ncallvirt instance class Type Object::GetType()\n"
                + "ldstr \"Hidden\"\nldc.i4.s 40\n"
                + "callvirt instance class MethodInfo Type::GetMethod(string, valuetype BindingFlags)\n" + invoke,
            "method" => owner + "ldstr \"Hidden\"\nldc.i4.s 40\n"
                + "callvirt instance class MethodInfo Type::GetMethod(string, valuetype BindingFlags)\n" + invoke,
            "enumeration" => owner + "ldc.i4.s 40\ncallvirt instance class MethodInfo[] Type::GetMethods(valuetype BindingFlags)\n"
                + "ldc.i4.0\nldelem.ref\n" + invoke,
            "property" => owner + "ldstr \"Value\"\nldc.i4.s 36\n"
                + "callvirt instance class PropertyInfo Type::GetProperty(string, valuetype BindingFlags)\n" + create
                + "callvirt instance object PropertyInfo::GetValue(object)\nunbox.any int32\n",
            "constructor" => create + "castclass Owner\nldfld int32 Owner::_value\n",
            _ => owner + "ldstr \"Nested\"\nldc.i4.s 32\n"
                + "callvirt instance class Type Type::GetNestedType(string, valuetype BindingFlags)\nldstr \"Read\"\n"
                + "callvirt instance class MethodInfo Type::GetMethod(string)\n" + invoke,
        };

        return """
            .class public Owner {
              .field private int32 _value
              .method private instance void .ctor() {
                ldarg.0
                call instance void Object::.ctor()
                ldarg.0
                ldc.i4.s 42
                stfld int32 Owner::_value
                ret
              }
              .method private static int32 Hidden() {
                ldc.i4.s 42
                ret
              }
              .method private instance int32 get_Value() {
                ldarg.0
                ldfld int32 Owner::_value
                ret
              }
              .property instance int32 Value() {
                .get instance int32 Owner::get_Value()
              }
              .class nested private Nested {
                .method public static int32 Read() {
                  ldc.i4.s 42
                  ret
                }
              }
              .method public static int32 Read() {
            """ + "\n" + body + "ret\n}\n}";
    }
}
