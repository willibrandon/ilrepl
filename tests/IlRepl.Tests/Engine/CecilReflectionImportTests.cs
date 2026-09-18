using IlRepl.Engine;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Core-library imports remain local to their destination module and preserve external metadata and executable signatures.
/// </summary>
[TestClass]
public sealed class CecilReflectionImportTests
{
    /// <summary>
    /// Independent writers emit exact core and external identities and execute boxed results through constructed signatures.
    /// </summary>
    /// <param name="export">Whether writers use the export constructor rather than session assembly names.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CoreImports_PreserveModuleIsolationAndExecutableMetadata(bool export)
    {
        var first = Create(export);
        var second = Create(export);
        var core = typeof(object).Assembly.GetName();
        var external = typeof(HttpClient).Assembly.GetName();
        var firstScope = first.Import(typeof(object)).Scope;
        var secondScope = second.Import(typeof(object)).Scope;
        Assert.AreNotSame(firstScope, secondScope);
        foreach (var writer in new[] { first, second })
        {
            var scope = writer.Import(typeof(object)).Scope;
            Assert.AreSame(scope, writer.Import(typeof(int)).Scope);
            Assert.AreSame(scope, writer.Import(typeof(Delegate)).Scope);
            Assert.AreSame(scope, writer.Import(typeof(List<int[]>)).Scope);
            Assert.AreSame(scope, writer.Import(typeof(int[])).Scope);
            Assert.AreNotSame(scope, writer.Import(typeof(HttpClient)).Scope);
            var type = writer.DefineType("Samples", "Generated", TypeAttributes.Public, writer.Object);
            type.Fields.Add(new FieldDefinition("Values", FieldAttributes.Public, writer.Import(typeof(List<int[]>))));
            type.Fields.Add(new FieldDefinition("Dependency", FieldAttributes.Public, writer.Import(typeof(HttpClient))));
            var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, writer.Object);
            var body = method.Body.GetILProcessor();
            body.Emit(OpCodes.Ldc_I4_S, (sbyte)42);
            body.Emit(OpCodes.Box, writer.Import(typeof(int)));
            body.Emit(OpCodes.Ret);
            type.Methods.Add(method);

            var image = writer.Write();
            using var stream = new MemoryStream(image, writable: false);
            using var metadata = AssemblyDefinition.ReadAssembly(stream);
            Assert.AreSequenceEqual(new[] { core.FullName, external.FullName }.Order(StringComparer.Ordinal),
                metadata.MainModule.AssemblyReferences.Select(reference => reference.FullName).Order(StringComparer.Ordinal));
            var loaded = SessionAssemblies.Load(image, writer.Name, writer.Kind, writer.Dependencies);
            try
            {
                var runtime = loaded.Assembly.GetType("Samples.Generated", throwOnError: true)!;
                Assert.AreEqual(typeof(List<int[]>), runtime.GetField("Values")!.FieldType);
                Assert.AreEqual(typeof(HttpClient), runtime.GetField("Dependency")!.FieldType);
                Assert.AreEqual(42, runtime.GetMethod("Read")!.Invoke(null, null));
            }
            finally
            {
                SessionAssemblies.Release(loaded);
            }
        }
    }

    private static CecilWriter Create(bool export) => export
        ? new CecilWriter("CoreScope" + Guid.NewGuid().ToString("N"))
        : new CecilWriter(SessionAssemblyKind.Cell);
}
