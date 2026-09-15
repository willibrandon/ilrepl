namespace IlRepl.Tests.Shared;

/// <summary>
/// Supplies a session marshaler whose factory and interface methods must survive an independent export.
/// </summary>
public static class MarshallingDependencyExamples
{
    /// <summary>
    /// Declares a custom marshaler with an observable factory and executable interface implementation.
    /// </summary>
    public const string Source = """
        .class public Marshaler implements System.Runtime.InteropServices.ICustomMarshaler {
          .field public static int32 Runs
          .method public instance void .ctor() {
            ldarg.0
            call instance void Object::.ctor()
            ret
          }
          .method public static class System.Runtime.InteropServices.ICustomMarshaler GetInstance(string cookie) {
            ldsfld int32 Marshaler::Runs
            ldc.i4.1
            add
            stsfld int32 Marshaler::Runs
            newobj instance void Marshaler::.ctor()
            ret
          }
          .method public virtual instance void CleanUpManagedData(object value) {
            ret
          }
          .method public virtual instance void CleanUpNativeData(native int value) {
            ret
          }
          .method public virtual instance int32 GetNativeDataSize() {
            ldc.i4.s 42
            ret
          }
          .method public virtual instance native int MarshalManagedToNative(object value) {
            ldarg.1
            unbox.any int32
            conv.i
            ret
          }
          .method public virtual instance object MarshalNativeToManaged(native int value) {
            ldarg.1
            conv.i4
            box int32
            ret
          }
        }
        """;
}
