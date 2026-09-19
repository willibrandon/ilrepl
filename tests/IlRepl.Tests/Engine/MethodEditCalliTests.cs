using System.Runtime.Loader;
using IlRepl.Engine;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Standalone call signatures retain the types captured when an edit opens.
/// </summary>
[TestClass]
public sealed class MethodEditCalliTests
{
    /// <summary>
    /// Types named only by a calli signature remain pinned through replacement, invocation, and independent export.
    /// </summary>
    /// <param name="shape">The signature position or nesting that carries the captured type.</param>
    [TestMethod]
    [DataRow("return")]
    [DataRow("parameter")]
    [DataRow("modifier")]
    [DataRow("nested")]
    public void Commit_CalliOnlySessionType_RemainsPinned(string shape)
    {
        var signature = shape switch
        {
            "return" => "class Local()",
            "parameter" => "void(class Local)",
            "modifier" => "int32 modopt(Local)()",
            _ => "void(method class Local *())",
        };

        var arguments = shape == "parameter" ? "ldnull\n" : shape == "nested" ? "ldc.i4.0\nconv.i\n" : "";
        var pop = shape is "return" or "modifier" ? "pop\n" : "";
        var session = IlLines.Load((".class public Local {\n.field public int32 Original\n}\n"
            + ".method int32 Read(native int pointer) {\n" + arguments + "ldarg.0\ncalli " + signature
            + "\n" + pop + "ldc.i4.s 41\nret\n}").Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join("\n", edit.Problems));
        Assert.Contains(dependency => dependency.Symbol == "Local"
            && dependency.Disposition == "copied (distinct type identity)"
            && dependency.Location.Contains("calli", StringComparison.Ordinal), edit.Dependencies);
        foreach (var line in IlLines.Expand(".class public Local {", ".field public int64 Added", "}"))
        {
            session.AddLine(line);
        }

        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.s 41", "ldc.i4.s 42", StringComparison.Ordinal));
        var captured = edit.Method!.Module.Assembly.GetTypes().Single(type => type.GetField("Original") is not null);
        Assert.IsNull(captured.GetField("Added"));
        Assert.IsNotNull(session.Types.Single().RuntimeType!.GetField("Added"));
        var typeName = captured.FullName!;
        var target = shape switch
        {
            "return" => ".method class " + typeName + " Target() {\nldnull\nret\n}",
            "parameter" => ".method void Target(class " + typeName + " value) {\nret\n}",
            "modifier" => ".method int32 Target() {\nldc.i4.0\nret\n}",
            _ => ".method void Target(method class " + typeName + " *() pointer) {\nret\n}",
        };

        foreach (var line in (target + "\n.method int32 Scenario() {\nldftn Target\ncall Copy\nret\n}").Split('\n'))
        {
            session.AddLine(line);
        }

        session.AddLine("call Scenario");
        Assert.AreEqual(42, session.Run().Value);
        session.AddLine("call Scenario");
        foreach (var image in new[] { AssemblyExporter.Write(session, "calli-types"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            using var module = ModuleDefinition.ReadModule(new MemoryStream(image));
            var owner = module.Types.Single(type => type.FullName == edit.Method.DeclaringType!.FullName);
            var site = owner.Methods.Single(method => method.Name == "Read").Body.Instructions
                .Select(instruction => instruction.Operand).OfType<CallSite>().Single();
            var referenced = shape switch
            {
                "return" => site.ReturnType,
                "parameter" => site.Parameters[0].ParameterType,
                "modifier" => ((OptionalModifierType)site.ReturnType).ModifierType,
                _ => ((FunctionPointerType)site.Parameters[0].ParameterType).ReturnType,
            };

            Assert.AreSame(module, referenced.Scope);
            var context = new AssemblyLoadContext("calli-export", isCollectible: true);
            try
            {
                var exported = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(42, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// Optional vararg parameters copy a private type that appears nowhere else in the imported method.
    /// </summary>
    [TestMethod]
    public void Capture_CalliOptionalParameter_CopiesItsPrivateType()
    {
        var session = new Session();
        var (_, _, original) = CecilFixture.Build((module, owner) =>
        {
            var hidden = new TypeDefinition("N", "CalliOptionalHidden", TypeAttributes.NotPublic, module.TypeSystem.Object);
            module.Types.Add(hidden);
            var read = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            read.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
            owner.Methods.Add(read);
            var site = new CallSite(module.TypeSystem.Void) { CallingConvention = MethodCallingConvention.VarArg };
            site.Parameters.Add(new ParameterDefinition(new SentinelType(hidden)));
            var il = read.Body.GetILProcessor();
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Calli, site);
            il.Emit(OpCodes.Ldc_I4, 42);
            il.Emit(OpCodes.Ret);
        }, session.Resolver);

        var family = ImportedMethodFamily.Capture("Copy", original.GetMethod("Read")!, session);
        var dependency = family.Dependencies.Single(dependency => dependency.Symbol == "CalliOptionalHidden");
        Assert.AreEqual("copied (distinct type identity)", dependency.Disposition);
        Assert.AreEqual("nonpublic", dependency.Access);
        var writer = new CecilWriter(SessionAssemblyKind.Types);
        family.Write(writer);
        using var exported = ModuleDefinition.ReadModule(new MemoryStream(writer.Write()));
        var site = exported.Types.SelectMany(type => type.Methods).Single(method => method.Name == "Read")
            .Body.Instructions.Select(instruction => instruction.Operand).OfType<CallSite>().Single();
        var parameter = (SentinelType)site.Parameters.Single().ParameterType;
        Assert.AreEqual(MethodCallingConvention.VarArg, site.CallingConvention);
        Assert.AreSame(exported, parameter.ElementType.Scope);
        Assert.Contains(type => type.FullName == parameter.ElementType.FullName, exported.Types);
    }
}
