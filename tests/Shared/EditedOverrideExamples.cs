namespace IlRepl.Tests.Shared;

/// <summary>
/// Supplies real interface and base slots whose original explicit mappings can be replaced by an edited body.
/// </summary>
public static class EditedOverrideExamples
{
    /// <summary>
    /// Declares pinned explicit mappings and an alternate virtual body that returns 43.
    /// </summary>
    /// <param name="generic">Whether the owner implements a constructed generic interface using its own parameter.</param>
    /// <returns>The complete original declarations.</returns>
    public static string Source(bool generic) => """
        .class public interface abstract IValue<T> {
          .method public virtual newslot abstract instance int32 Read() {}
        }
        .class public interface abstract IKeep {
          .method public virtual newslot abstract instance int32 Keep() {}
        }
        .class public Base {
          .method public instance void .ctor() {
            ldarg.0
            call instance void Object::.ctor()
            ret
          }
          .method public virtual newslot instance int32 Read() {
            ldc.i4.s 40
            ret
          }
        }
        """ + "\n.class public Owner" + (generic ? "<T>" : "") + " extends Base implements IValue<"
        + (generic ? "!T" : "int32") + ">, IKeep {\n"
        + ".method public instance void .ctor() {\nldarg.0\ncall instance void Base::.ctor()\nret\n}\n"
        + ".method private virtual final newslot instance int32 Original() {\n.override IValue<"
        + (generic ? "!T" : "int32") + ">::Read\n.override Base::Read\nldc.i4.s 42\nret\n}\n"
        + ".method public virtual newslot instance int32 Keep() {\n.override IKeep::Keep\nldc.i4.7\nret\n}\n"
        + Method(generic, "none") + "\n}";

    /// <summary>
    /// Adds or replaces the chosen explicit slot while leaving the implementation's return value fixed.
    /// </summary>
    /// <param name="generic">Whether the owner type parameter is in scope.</param>
    /// <param name="slot">The interface, base, both, object or none mapping selection.</param>
    /// <param name="repeat">Whether to repeat identical directives to verify idempotence.</param>
    /// <returns>The complete selected method source.</returns>
    public static string Method(bool generic, string slot, bool repeat = false)
    {
        var mappings = slot switch
        {
            "interface" => ".override IValue<" + (generic ? "!T" : "int32") + ">::Read\n",
            "base" => ".override Base::Read\n",
            "both" => ".override IValue<" + (generic ? "!T" : "int32") + ">::Read\n.override Base::Read\n",
            "object" => ".override Object::GetHashCode\n",
            _ => "",
        };

        return ".method public virtual newslot instance int32 Alternate() {\n" + mappings + (repeat ? mappings : "")
            + "ldc.i4.s 43\nret\n}";
    }

    /// <summary>
    /// Selects the original instance method, closing the owner when it is generic.
    /// </summary>
    /// <param name="generic">Whether to close the owner over int32.</param>
    /// <returns>The exact original method reference.</returns>
    public static string Reference(bool generic) => "instance int32 Owner" + (generic ? "<int32>" : "") + "::Alternate()";

    /// <summary>
    /// Calls the selected body and then dispatches through the copied interface or base slot in a real scenario.
    /// </summary>
    /// <param name="generic">Whether the copied owner is generic.</param>
    /// <param name="slot">The interface or base slot to observe.</param>
    /// <returns>The complete parameterless scenario.</returns>
    public static string Scenario(bool generic, string slot)
    {
        var owner = "IlRepl.Edits.Copy.Owner" + (generic ? "`1<int32>" : "");
        var target = slot == "interface"
            ? "callvirt instance class Type[] Type::GetInterfaces()\nldc.i4.0\nldelem.ref\n"
            : "callvirt instance class Type Type::get_BaseType()\n";
        return ".method int32 Scenario() {\n.locals init (object instance)\nnewobj instance void " + owner
            + "::.ctor()\ndup\nstloc.0\ncall Copy\npop\nldtoken " + owner
            + "\ncall class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\n" + target
            + "ldstr \"Read\"\ncallvirt instance class MethodInfo Type::GetMethod(string)\nldloc.0\nldnull\n"
            + "callvirt instance object MethodBase::Invoke(object, object[])\nunbox.any int32\nret\n}";
    }
}
