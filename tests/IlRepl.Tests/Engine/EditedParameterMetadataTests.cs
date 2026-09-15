using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilParameterAttributes = Mono.Cecil.ParameterAttributes;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using ParameterAttributes = System.Reflection.ParameterAttributes;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Edited parameter names and flags reach aliases, exports, and comparison observations without losing retained metadata.
/// </summary>
[TestClass]
public sealed class EditedParameterMetadataTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Renamed ref parameters and changed direction flags remain distinct from the original in every executable image.
    /// </summary>
    /// <param name="before">The original parameter flags.</param>
    /// <param name="after">The edited parameter flags.</param>
    /// <param name="isPrivate">Whether the alias needs a forwarding entry.</param>
    [TestMethod]
    [DataRow("", "[out]", false)]
    [DataRow("[out]", "", false)]
    [DataRow("[out]", "[in] [out] [opt]", false)]
    [DataRow("[in] [out] [opt]", "[out]", false)]
    [DataRow("[in]", "[in] [opt]", false)]
    [DataRow("", "[out]", true)]
    [DataRow("[out]", "", true)]
    [DataRow("[out]", "[in] [out] [opt]", true)]
    [DataRow("[in] [out] [opt]", "[out]", true)]
    [DataRow("[in]", "[in] [opt]", true)]
    public async Task Commit_ParameterNamesAndFlagsControlExportsAndObservations(string before, string after, bool isPrivate)
    {
        var access = isPrivate ? "private" : "public";
        var session = IlLines.Load(".class public Owner {", $".method {access} static void Set({before} int32& original) {{",
            "ldarg original", "ldc.i4.s 42", "stind.i4", "ret", "}", "}");
        var edit = session.PrepareEdit("void Owner::Set(int32&)", "Copy");
        session.CommitEdit(edit.Name, $".method {access} static void Set({after} int32& renamed) {{\n"
            + "ldarg renamed\nldc.i4.s 43\nstind.i4\nret\n}");
        AssertParameter(edit.OriginalMethod.GetParameters().Single(), "original", before);
        AssertParameter(edit.Method!.GetParameters().Single(), "renamed", after);
        AssertParameter(session.TypeTable.MethodAliases[edit.Name].GetParameters().Single(), "renamed", after);
        Assert.Contains("renamed", MethodDisassembler.Disassemble(edit.Method, session).Header);
        foreach (var line in IlLines.Expand(".method int32 Scenario() { .locals init (int32 value); ldc.i4 123; stloc.0; "
            + "ldloca.s 0; call Copy; ldloc.0; ret }"))
        {
            session.AddLine(line);
        }

        session.AddLine("call Scenario");
        foreach (var image in new[] { AssemblyExporter.Write(session, "edited-parameters"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("edited-parameters-" + Guid.NewGuid(), isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                var method = assembly.GetType(edit.Method.DeclaringType!.FullName!)!
                    .GetMethod("Set", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
                AssertParameter(method.GetParameters().Single(), "renamed", after);
                Assert.AreEqual(43, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }

        var compared = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);
        var outcome = OnlyOut(before) == OnlyOut(after) ? "different" : "different-inputs";
        Assert.AreEqual(outcome, compared.Outcome, compared.Original.Detail + "; " + compared.Edited.Detail);
        AssertObservation(compared.Original, before, "42");
        AssertObservation(compared.Edited, after, "43");
        Assert.AreEqual(43, session.Run().Value);
    }

    /// <summary>
    /// Changing editable flags preserves the original constant, marshalling descriptor, and custom attribute.
    /// </summary>
    [TestMethod]
    public void Commit_ParameterFlagsRetainOtherMetadata()
    {
        var session = new Session();
        var (assembly, _, _) = CecilFixture.Build((module, owner) =>
        {
            var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            owner.Methods.Add(method);
            var attributes = CecilParameterAttributes.Optional | CecilParameterAttributes.HasDefault
                | CecilParameterAttributes.HasFieldMarshal;
            var parameter = new ParameterDefinition("original", attributes, module.TypeSystem.Int32)
            {
                Constant = 7,
                MarshalInfo = new MarshalInfo(NativeType.I4),
            };
            method.Parameters.Add(parameter);
            var attribute = new CustomAttribute(module.ImportReference(typeof(DescriptionAttribute).GetConstructor([typeof(string)])!));
            attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, "preserved"));
            parameter.CustomAttributes.Add(attribute);
            method.Body.GetILProcessor().Emit(OpCodes.Ldarg_0);
            method.Body.GetILProcessor().Emit(OpCodes.Ret);
        }, session.Resolver);
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]N.Fixture::Read(int32)", "Copy");
        session.CommitEdit(edit.Name, ".method public static int32 Read([in] int32 renamed) {\nldarg renamed\nret\n}");
        AssertRetainedMetadata(edit.Method!.GetParameters().Single());
        session.AddLine("ldc.i4.s 42");
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "parameter-metadata"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("parameter-metadata-" + Guid.NewGuid(), isCollectible: true);
            try
            {
                var exported = context.LoadFromStream(new MemoryStream(image));
                var method = exported.GetType(edit.Method.DeclaringType!.FullName!)!.GetMethod("Read")!;
                AssertRetainedMetadata(method.GetParameters().Single());
                Assert.AreEqual(42, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    private static void AssertParameter(ParameterInfo parameter, string name, string flags)
    {
        Assert.AreEqual(name, parameter.Name);
        Assert.AreEqual(flags.Contains("[in]", StringComparison.Ordinal), parameter.IsIn);
        Assert.AreEqual(flags.Contains("[out]", StringComparison.Ordinal), parameter.IsOut);
        Assert.AreEqual(flags.Contains("[opt]", StringComparison.Ordinal), parameter.IsOptional);
    }

    private static void AssertRetainedMetadata(ParameterInfo parameter)
    {
        Assert.AreEqual("renamed", parameter.Name);
        Assert.AreEqual(ParameterAttributes.In | ParameterAttributes.HasDefault | ParameterAttributes.HasFieldMarshal,
            parameter.Attributes);
        Assert.AreEqual(7, parameter.RawDefaultValue);
        Assert.AreEqual(UnmanagedType.I4, parameter.GetCustomAttribute<MarshalAsAttribute>()!.Value);
        Assert.AreEqual("preserved", parameter.GetCustomAttribute<DescriptionAttribute>()!.Description);
    }

    private static void AssertObservation(ComparisonSide side, string flags, string result)
    {
        Assert.AreEqual("completed", side.Outcome);
        Assert.AreEqual(result, side.Result!.Value);
        var invocation = side.Invocations.Single();
        var input = invocation.Inputs.Single(member => member.Name == "argument 0").Value;
        var onlyOut = OnlyOut(flags);
        Assert.AreEqual(onlyOut ? "null" : "scalar", input.Kind);
        Assert.AreEqual(onlyOut ? null : "123", input.Value);
        Assert.AreEqual(result, invocation.Outputs.Single(member => member.Name == "argument 0").Value.Value);
    }

    private static bool OnlyOut(string flags) => flags.Contains("[out]", StringComparison.Ordinal)
        && !flags.Contains("[in]", StringComparison.Ordinal);
}
