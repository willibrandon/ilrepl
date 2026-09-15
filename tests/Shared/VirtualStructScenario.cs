namespace IlRepl.Tests.Shared;

/// <summary>
/// Calls a struct implementation through a copied default interface and reads the caller's original storage.
/// </summary>
public static class VirtualStructScenario
{
    /// <summary>
    /// Mutates the unboxed receiver through constrained interface dispatch and returns its stored value.
    /// </summary>
    public const string Source = """
        .class public sequential sealed Derived extends ValueType implements IlRepl.Edits.Copy.Owner {
          .field public int32 Value
          .method public instance void .ctor(int32 value) {
            ldarg.0
            ldarg.1
            stfld int32 Derived::Value
            ret
          }
          .method public final virtual newslot instance int32 Read(int32 value) {
            ldarg.0
            dup
            ldfld int32 Derived::Value
            ldarg.1
            add
            stfld int32 Derived::Value
            ldarg.0
            ldfld int32 Derived::Value
            ret
          }
        }
        .method int32 Scenario() {
          .locals init (valuetype Derived receiver)
          ldc.i4.7
          newobj instance void Derived::.ctor(int32)
          stloc.0
          ldloca.s 0
          ldc.i4.7
          constrained. Derived
          callvirt instance int32 IlRepl.Edits.Copy.Owner::Read(int32)
          pop
          ldloca.s 0
          ldfld int32 Derived::Value
          ret
        }
        """;
}
