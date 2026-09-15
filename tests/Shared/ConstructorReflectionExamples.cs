namespace IlRepl.Tests.Shared;

/// <summary>
/// Constructs real reflected instances and reads their initialized fields through downstream reflection.
/// </summary>
public static class ConstructorReflectionExamples
{
    /// <summary>
    /// Declares a public or private constructor, a construction counter, and the selected reflection method.
    /// </summary>
    /// <param name="lookup">The constructor lookup API: one, two, four, five, index, first, single, declared, or as-type.</param>
    /// <param name="argument">Whether the constructor accepts one integer argument.</param>
    /// <param name="privateConstructor">Whether the constructor is private.</param>
    /// <param name="flow">direct, local, return, unknown, or binder.</param>
    /// <param name="invoker">Whether to use ConstructorInvoker instead of ConstructorInfo.</param>
    /// <param name="generic">Whether the owner declares a generic parameter.</param>
    /// <returns>The complete owner declaration.</returns>
    public static string Source(string lookup, bool argument, bool privateConstructor, string flow = "direct",
        bool invoker = false, bool generic = false)
    {
        var owner = Owner(generic);
        var source = ".class public ConstructorOwner" + (generic ? "<T>" : "") + " {\n"
            + ".field public int32 Value\n.field public static int32 Constructions\n"
            + ".method private static int32 Initialize(int32 value) {\nldarg.0\nldc.i4.1\nadd\nret\n}\n"
            + ".method " + (privateConstructor ? "private" : "public") + " instance void .ctor(" + (argument ? "int32 value" : "")
            + ") {\nldarg.0\ncall instance void Object::.ctor()\n"
            + "ldsfld int32 " + owner + "::Constructions\nldc.i4.1\nadd\nstsfld int32 " + owner + "::Constructions\n"
            + "ldarg.0\n" + (argument ? "ldarg.1\n" : "ldc.i4.s 41\n")
            + "call int32 " + owner + "::Initialize(int32)\nstfld int32 " + owner + "::Value\nret\n}\n";
        source += ".method public instance int32 ReadValue() {\nldarg.0\nldfld int32 " + owner + "::Value\nret\n}\n";
        if (flow == "return") source += ".method private static class ConstructorInfo Find() {\n"
            + Lookup(lookup, argument, privateConstructor, generic, false) + "ret\n}\n";
        return source + Method(lookup, argument, privateConstructor, flow, invoker, generic) + "\n}";
    }

    /// <summary>
    /// Looks up and invokes the constructor, then reads the actual constructed object's Value field.
    /// </summary>
    /// <param name="lookup">The lookup API family.</param>
    /// <param name="argument">Whether the constructor receives the integer 41.</param>
    /// <param name="privateConstructor">Whether to include nonpublic constructors.</param>
    /// <param name="flow">The constructor receiver's provenance.</param>
    /// <param name="invoker">Whether to use ConstructorInvoker.</param>
    /// <param name="generic">Whether the owner is generic.</param>
    /// <param name="edited">Whether to add one to the observed initialized value.</param>
    /// <returns>The selected method declaration.</returns>
    public static string Method(string lookup, bool argument, bool privateConstructor, string flow = "direct",
        bool invoker = false, bool generic = false, bool edited = false)
    {
        var parameter = flow switch { "unknown" => "class ConstructorInfo constructor", "binder" => "class Binder binder", _ => "" };
        var source = ".method public static int32 Read(" + parameter + ") {\n"
            + ".locals init (class ConstructorInfo selected, object created)\n";
        source += flow switch
        {
            "unknown" => "ldarg.0\n",
            "return" => "call class ConstructorInfo " + Owner(generic) + "::Find()\n",
            _ => Lookup(lookup, argument, privateConstructor, generic, flow == "binder"),
        };
        if (flow == "local") source += "stloc.0\nldloc.0\n";
        if (invoker)
        {
            source += "call class ConstructorInvoker ConstructorInvoker::Create(class ConstructorInfo)\n";
            if (argument) source += "ldc.i4.s 41\nbox int32\n";
            source += "callvirt instance object ConstructorInvoker::Invoke(" + (argument ? "object" : "") + ")\n";
        }
        else
        {
            source += lookup == "five" ? "ldc.i4.0\nldnull\n" + Arguments(argument) + "ldnull\n"
                + "callvirt instance object ConstructorInfo::Invoke(valuetype BindingFlags, class Binder, object[], class CultureInfo)\n"
                : Arguments(argument) + "callvirt instance object ConstructorInfo::Invoke(object[])\n";
        }
        source += "stloc.1\nldloc.1\ncallvirt instance class Type Object::GetType()\nldstr \"ReadValue\"\n"
            + "callvirt instance class MethodInfo Type::GetMethod(string)\nldloc.1\nldnull\n"
            + "callvirt instance object MethodBase::Invoke(object, object[])\nunbox.any int32\n";
        return source + (edited ? "ldc.i4.1\nadd\n" : "") + "ret\n}";
    }

    /// <summary>
    /// Selects the closed owner when the fixture contains a generic type.
    /// </summary>
    /// <param name="generic">Whether the owner is generic.</param>
    /// <param name="flow">The constructor or binder parameter form.</param>
    /// <returns>The exact selected method reference.</returns>
    public static string Reference(bool generic = false, string flow = "direct") => "int32 "
        + (generic ? "ConstructorOwner<int32>" : "ConstructorOwner") + "::Read("
        + (flow == "unknown" ? "class ConstructorInfo" : flow == "binder" ? "class Binder" : "") + ")";

    /// <summary>
    /// Invokes an actual type initializer through MethodBase.Invoke before inspecting its initialized static field.
    /// </summary>
    /// <returns>The target and selected owner declarations.</returns>
    public static string InitializerSource() => """
        .class public InitializerTarget {
          .field public static int32 Value
          .method private static void .cctor() {
            ldc.i4.s 42
            stsfld int32 InitializerTarget::Value
            ret
          }
        }
        .class public ConstructorOwner {
          .method public static int32 Read() {
            ldtoken InitializerTarget
            call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)
            callvirt instance class ConstructorInfo Type::get_TypeInitializer()
            ldnull
            ldnull
            callvirt instance object MethodBase::Invoke(object, object[])
            pop
            ldtoken InitializerTarget
            call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)
            ldstr "Value"
            callvirt instance class FieldInfo Type::GetField(string)
            ldnull
            callvirt instance object FieldInfo::GetValue(object)
            unbox.any int32
            ret
          }
        }
        """;

    private static string Owner(bool generic) => generic ? "class ConstructorOwner`1<!0>" : "ConstructorOwner";

    private static string Lookup(string lookup, bool argument, bool privateConstructor, bool generic, bool binder)
    {
        var flags = privateConstructor ? "ldc.i4.s 36\n" : "ldc.i4.s 20\n";
        var source = "ldtoken " + Owner(generic) + "\ncall class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\n";
        if (lookup is "declared" or "as-type")
        {
            source += "call class TypeInfo IntrospectionExtensions::GetTypeInfo(class Type)\n";
            if (lookup == "as-type") source += "callvirt instance class Type TypeInfo::AsType()\n";
        }
        if (lookup is "index" or "first" or "single")
        {
            source += privateConstructor ? flags
                + "callvirt instance class ConstructorInfo[] Type::GetConstructors(valuetype BindingFlags)\n"
                : "callvirt instance class ConstructorInfo[] Type::GetConstructors()\n";
        }
        else if (lookup == "declared")
        {
            source += "callvirt instance class IEnumerable<class ConstructorInfo> TypeInfo::get_DeclaredConstructors()\n";
        }
        else
        {
            var arity = lookup == "as-type" ? "one" : lookup;
            if (arity != "one") source += flags;
            if (arity is "four" or "five") source += binder ? "ldarg.0\n" : "ldnull\n";
            if (arity == "five") source += "ldc.i4.3\n";
            source += argument ? "ldc.i4.1\nnewarr class Type\ndup\nldc.i4.0\nldtoken int32\n"
                + "call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\nstelem.ref\n"
                : "ldc.i4.0\nnewarr class Type\n";
            if (arity is "four" or "five") source += "ldnull\n";
            var parameters = arity switch
            {
                "one" => "class Type[]", "two" => "valuetype BindingFlags, class Type[]",
                "four" => "valuetype BindingFlags, class Binder, class Type[], valuetype ParameterModifier[]",
                _ => "valuetype BindingFlags, class Binder, valuetype CallingConventions, class Type[], valuetype ParameterModifier[]",
            };
            return source + "callvirt instance class ConstructorInfo Type::GetConstructor(" + parameters + ")\n";
        }
        return source + (lookup == "index" ? "ldc.i4.0\nldelem.ref\n"
            : "call !!0 Enumerable::" + (lookup is "single" or "declared" ? "Single" : "First")
                + "<class ConstructorInfo>(class IEnumerable<!!0>)\n");
    }

    private static string Arguments(bool argument) => argument
        ? "ldc.i4.1\nnewarr object\ndup\nldc.i4.0\nldc.i4.s 41\nbox int32\nstelem.ref\n"
        : "ldc.i4.0\nnewarr object\n";
}
