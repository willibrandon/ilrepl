using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilMethodAttributes = Mono.Cecil.MethodAttributes;

namespace IlRepl.Tests.Engine;

/// <summary>
/// String lookups preserve the copied type context through invocation, comparison, and standalone export.
/// </summary>
[TestClass]
public sealed class TypeLookupEditTests
{
    /// <summary>
    /// Supplies cancellation for real comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// An unrelated assembly's System.Type.GetType method keeps its own signature and behavior.
    /// </summary>
    [TestMethod]
    public async Task Compare_LookalikeTypeLookupRemainsAnExternalCall()
    {
        var session = new Session();
        var (assembly, _, _) = CecilFixture.Build((module, owner) =>
        {
            owner.Namespace = "System";
            owner.Name = "Type";
            var method = new MethodDefinition("GetType", CecilMethodAttributes.Public | CecilMethodAttributes.Static,
                module.TypeSystem.Int32);
            method.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            owner.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4, 42);
            il.Emit(OpCodes.Ret);
        }, session.Resolver);
        foreach (var line in IlLines.Expand(".method int32 Work() {", "ldstr \"name\"",
            "call int32 [" + assembly.GetName().Name + "]System.Type::GetType(string)", "ret", "}"))
        {
            session.AddLine(line);
        }

        var edit = session.PrepareEdit("Work", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("42", result.Original.Result!.Value);
        Assert.AreEqual("42", result.Edited.Result!.Value);
    }

    /// <summary>
    /// A second edit of an existing copy keeps both aliases and their lookup contexts independent in saved assemblies.
    /// </summary>
    [TestMethod]
    public void Export_CopyOfCopyKeepsBothLookupContexts()
    {
        var session = IlLines.Load(TypeLookupExamples.Source(4, 0, true).Split('\n'));
        var first = session.PrepareEdit("bool Lookup.Owner::Read()", "First");
        session.CommitEdit(first.Name, first.Source);
        var second = session.PrepareEdit("First", "Second");
        Assert.IsEmpty(second.Problems, string.Join('\n', second.Problems));
        session.CommitEdit(second.Name, second.Source.Replace("ret", "ldc.i4.0\nceq\nret", StringComparison.Ordinal));
        Assert.IsTrue((bool)first.Method!.Invoke(null, null)!);
        Assert.IsFalse((bool)second.Method!.Invoke(null, null)!);
        session.AddLine("call First");
        session.AddLine("call Second");
        session.AddLine("xor");
        foreach (var image in new[] { AssemblyExporter.Write(session, "copied-lookups"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("copied-lookups", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(1, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// Reflected lookup method tokens keep their framework identity after copying.
    /// </summary>
    [TestMethod]
    public void Edit_LookupMethodTokenKeepsItsIdentity()
    {
        var session = IlLines.Load(".method class Type Owner() {", "ldtoken method class Type Type::GetType(string)",
            "call class MethodBase MethodBase::GetMethodFromHandle(valuetype RuntimeMethodHandle)",
            "callvirt instance class Type MemberInfo::get_DeclaringType()", "ret", "}");
        var edit = session.PrepareEdit("Owner", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(typeof(Type), edit.Original.Requested.Invoke(null, null));
        Assert.AreEqual(typeof(Type), edit.Method!.Invoke(null, null));
    }

    /// <summary>
    /// Resolver overloads use the copied context for default resolution and preserve explicit callback inputs.
    /// </summary>
    /// <param name="resolver">The callback to supply, or none.</param>
    /// <param name="options">The number of Boolean lookup options.</param>
    [TestMethod]
    [DataRow("none", 0)]
    [DataRow("none", 1)]
    [DataRow("none", 2)]
    [DataRow("assembly", 0)]
    [DataRow("assembly", 2)]
    [DataRow("type", 0)]
    [DataRow("type", 2)]
    public async Task Compare_ResolverOverloadsKeepTheirCallbacks(string resolver, int options)
    {
        var session = IlLines.Load(TypeLookupResolverExamples.Source(resolver, options).Split('\n'));
        var edit = session.PrepareEdit("bool Lookup.Owner::Read()", "Copy");
        Assert.IsTrue((bool)edit.Original.Requested.Invoke(null, null)!);
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.IsTrue((bool)edit.Method!.Invoke(null, null)!);
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("true", result.Original.Result!.Value);
        Assert.AreEqual("true", result.Edited.Result!.Value);
        Assert.AreEqual(resolver == "type" ? "Lookup.Owner" + Environment.NewLine : "", result.Edited.StandardOutput);
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "lookup-resolvers"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("lookup-resolvers", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                Assert.IsTrue((bool)assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null)!);
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// Qualified names, null inputs, missing types, case sensitivity, and throwing options follow the runtime API.
    /// </summary>
    [TestMethod]
    public void Invoke_LookupOptionsRetainRuntimeBehavior()
    {
        var session = IlLines.Load(".class public Lookup.Owner {",
            ".method public static class Type Read(string name, bool fail, bool ignore) {",
            "ldarg.0", "ldarg.1", "ldarg.2", "call class Type Type::GetType(string, bool, bool)", "ret", "}", "}");
        var edit = session.PrepareEdit("class Type Lookup.Owner::Read(string, bool, bool)", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        foreach (var method in new[] { edit.Original.Requested, edit.OriginalMethod, edit.Method! })
        {
            var qualified = edit.Original.Requested.DeclaringType!.AssemblyQualifiedName;
            var resolved = (Type)method.Invoke(null, [qualified, true, false])!;
            Assert.AreEqual(method.DeclaringType, resolved,
                method.DeclaringType!.AssemblyQualifiedName + "; " + resolved.AssemblyQualifiedName);
            var later = new AssemblyName(edit.Original.Requested.DeclaringType!.Assembly.FullName!) { Version = new Version(2, 0, 0, 0) };
            Assert.IsNull(method.Invoke(null, ["Lookup.Owner, " + later.FullName, false, false]));
            Assert.AreEqual(method.DeclaringType, method.Invoke(null, ["lookup.owner", true, true]));
            Assert.AreEqual(typeof(int), method.Invoke(null, [typeof(int).AssemblyQualifiedName, true, false]));
            foreach (var name in new[] { "Lookup.Missing", "lookup.owner", "" })
            {
                Assert.IsNull(method.Invoke(null, [name, false, false]));
                var exception = Assert.ThrowsExactly<TargetInvocationException>(() => method.Invoke(null, [name, true, false]));
                Assert.IsInstanceOfType<TypeLoadException>(exception.InnerException);
            }

            var nullName = Assert.ThrowsExactly<TargetInvocationException>(() => method.Invoke(null, [null, false, false]));
            Assert.IsInstanceOfType<ArgumentNullException>(nullName.InnerException);
        }
    }

    /// <summary>
    /// Literal and runtime names resolve the same types as tokens without changing the caller's input string.
    /// </summary>
    /// <param name="shape">The type-name shape to resolve.</param>
    /// <param name="options">The number of Boolean lookup options.</param>
    /// <param name="literal">Whether the lookup uses a literal instead of an argument.</param>
    [TestMethod]
    [DataRow(0, 0, true)]
    [DataRow(0, 1, true)]
    [DataRow(0, 2, true)]
    [DataRow(1, 0, true)]
    [DataRow(2, 0, true)]
    [DataRow(3, 0, true)]
    [DataRow(4, 0, true)]
    [DataRow(4, 2, true)]
    [DataRow(0, 0, false)]
    [DataRow(4, 2, false)]
    public async Task Compare_StringLookupsResolveCopiedTypes(int shape, int options, bool literal)
    {
        var session = IlLines.Load(TypeLookupExamples.Source(shape, options, literal).Split('\n'));
        var edit = session.PrepareEdit("bool Lookup.Owner::Read(" + (literal ? "" : "string") + ")", "Copy");
        var name = TypeLookupExamples.Name(shape, options == 2);
        Assert.IsTrue((bool)edit.Original.Requested.Invoke(null, literal ? [] : [name])!);
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.IsTrue((bool)edit.Method!.Invoke(null, literal ? [] : [name])!);
        var arguments = literal ? "()" : "(" + LiteralParser.Escape(name) + ")";
        var baseline = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy " + arguments),
            TestContext.CancellationToken);
        Assert.AreEqual("match", baseline.Outcome, baseline.Original.Detail + "; " + baseline.Edited.Detail);
        Assert.AreEqual("true", baseline.Original.Result!.Value);
        Assert.AreEqual("true", baseline.Edited.Result!.Value);
        Assert.AreEqual(name + Environment.NewLine, baseline.Edited.StandardOutput);
        session.CommitEdit(edit.Name, TypeLookupExamples.Method(shape, options, literal, true));
        var changed = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy " + arguments),
            TestContext.CancellationToken);
        Assert.AreEqual("different", changed.Outcome, changed.Original.Detail + "; " + changed.Edited.Detail);
        Assert.AreEqual("true", changed.Original.Result!.Value);
        Assert.AreEqual("false", changed.Edited.Result!.Value);
        Assert.AreEqual(name + Environment.NewLine, changed.Edited.StandardOutput);
        if (!literal)
        {
            session.AddLine("ldstr " + LiteralParser.Escape(name));
        }

        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "type-lookups"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("type-lookups", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                Assert.IsFalse((bool)assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null)!);
                Assert.DoesNotContain(reference => reference.Name == "IlRepl.Engine", assembly.GetReferencedAssemblies());
            }
            finally
            {
                context.Unload();
            }
        }
    }
}
