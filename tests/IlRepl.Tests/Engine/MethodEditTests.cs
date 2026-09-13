using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using IlRepl.Engine;
using Mono.Cecil;
using CecilAttributeArgument = Mono.Cecil.CustomAttributeNamedArgument;
using CecilFieldAttributes = Mono.Cecil.FieldAttributes;
using CecilMethodAttributes = Mono.Cecil.MethodAttributes;
using CecilMethodDefinition = Mono.Cecil.MethodDefinition;
using CecilOpCodes = Mono.Cecil.Cil.OpCodes;
using CecilTypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Contextual copies use real sessions and loaded assemblies, with executable behavior and independently inspected exports.
/// </summary>
[TestClass]
public sealed class MethodEditTests
{
    /// <summary>
    /// Framework drafts retain source and native-helper blockers, then accept an explicit body that removes those dependencies.
    /// </summary>
    /// <param name="input">The argument passed to the original and copied methods.</param>
    /// <param name="expected">The expected absolute value.</param>
    [TestMethod]
    [DataRow(-42, 42)]
    [DataRow(0, 0)]
    [DataRow(42, 42)]
    public void PrepareEdit_FrameworkBlockerRetainsDraftAndAcceptsCorrectedBody(int input, int expected)
    {
        var session = new Session();
        var draft = session.PrepareEdit("int32 Math::Abs(int32)", "Absolute");
        Assert.AreEqual("Absolute", draft.Name);
        Assert.IsNull(draft.Method);
        Assert.AreEqual("Abs", draft.Original.Method.Name);
        Assert.AreEqual(typeof(int), ((MethodInfo)draft.Original.Method).ReturnType);
        Assert.AreSequenceEqual(new[] { typeof(int) }, draft.Original.Method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.StartsWith(".method ", draft.Source);
        Assert.Contains(".maxstack ", draft.Source);
        Assert.HasCount(1, session.Edits);

        Assert.Contains(problem => problem.Contains("MemmoveInternal", StringComparison.Ordinal), draft.Problems,
            string.Join("\n", draft.Problems));
        var originalSource = draft.Source;
        var rejected = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(draft.Name, draft.Source));
        Assert.Contains("MemmoveInternal", rejected.Message);
        Assert.IsNull(draft.Method);
        Assert.AreEqual(0, draft.Revision);
        Assert.AreEqual(originalSource, draft.Source);
        Assert.AreSame(draft, session.PrepareEdit(draft.Name));
        var unavailable = Assert.ThrowsExactly<ReplException>(() => _ = draft.OriginalMethod);
        Assert.Contains("original context cannot be reproduced", unavailable.Message);

        var committed = session.CommitEdit(draft.Name, AbsoluteSource(draft));
        Assert.IsEmpty(committed.Problems);
        Assert.IsNotNull(committed.Method);
        Assert.AreEqual(expected, committed.Method.Invoke(null, [input]));
        Assert.AreEqual(expected, Math.Abs(input));
        Assert.AreSame(draft.Original, committed.Original);
    }

    /// <summary>
    /// An explicit replacement body preserves the framework overflow exception through its public constructor.
    /// </summary>
    [TestMethod]
    public void CommitEdit_CorrectedFrameworkBodyPreservesOverflowException()
    {
        var session = new Session();
        var draft = session.PrepareEdit("int32 Math::Abs(int32)", "Absolute");
        var committed = session.CommitEdit(draft.Name, AbsoluteSource(draft));
        var expected = Assert.ThrowsExactly<OverflowException>(() => Math.Abs(int.MinValue));
        var error = Assert.ThrowsExactly<TargetInvocationException>(() => committed.Method!.Invoke(null, [int.MinValue]));
        Assert.IsInstanceOfType<OverflowException>(error.InnerException);
        Assert.AreEqual(expected.Message, error.InnerException.Message);
        Assert.AreEqual(expected.HResult, error.InnerException.HResult);
        Assert.AreEqual(7, committed.Method!.Invoke(null, [-7]));
    }

    /// <summary>
    /// A scalar framework method with two parameters imports its complete signature and both branch results.
    /// </summary>
    /// <param name="left">The first integer.</param>
    /// <param name="right">The second integer.</param>
    /// <param name="expected">The larger integer.</param>
    [TestMethod]
    [DataRow(-7, 3, 3)]
    [DataRow(8, 2, 8)]
    [DataRow(4, 4, 4)]
    public void CommitEdit_FrameworkMaxPreservesScalarSignature(int left, int right, int expected)
    {
        var session = new Session();
        var draft = session.PrepareEdit("int32 Math::Max(int32, int32)", "Maximum");
        var committed = session.CommitEdit(draft.Name, draft.Source);
        Assert.AreEqual(expected, committed.Method!.Invoke(null, [left, right]));
        Assert.AreSequenceEqual(new[] { typeof(int), typeof(int) },
            committed.Method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.AreEqual(typeof(int), ((MethodInfo)committed.Method).ReturnType);
    }

    /// <summary>
    /// A runtime-owned receiver retains its editable draft and precise blockers while rejected commits remain atomic.
    /// </summary>
    [TestMethod]
    public void PrepareEdit_RuntimeOwnedStringReceiverIsDiagnosedAtomically()
    {
        var session = new Session();
        var draft = session.PrepareEdit("instance string String::ToString()", "StringEdit");
        Assert.Contains(problem => problem.Contains("runtime-owned", StringComparison.Ordinal), draft.Problems);
        Assert.StartsWith(".method ", draft.Source);
        Assert.Contains("ToString", draft.Source);
        var source = draft.Source;

        var error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(draft.Name, source));

        Assert.Contains("runtime-owned", error.Message);
        Assert.Contains("string", error.Message);
        Assert.HasCount(1, session.Edits);
        Assert.AreSame(draft, session.Edits[0]);
        Assert.AreEqual(source, draft.Source);
        Assert.IsNull(draft.Method);
        Assert.AreEqual(0, draft.Revision);
        Assert.IsEmpty(session.Methods);
    }

    /// <summary>
    /// Edited methods remain callable by name, and reopening preserves their captured original implementation.
    /// </summary>
    [TestMethod]
    public void CommitEdit_ChangesCopyAndPreservesOriginalAndReopenBaseline()
    {
        var session = IlLines.Load(".method int32 Double(int32 n) { ldarg.0; ldc.i4.2; mul; ret }");
        var original = session.Methods.Single().Version.Body;
        var draft = session.PrepareEdit("Double", "Edited");
        var changed = draft.Source.Replace(": mul", ": add", StringComparison.Ordinal);
        Assert.AreNotEqual(draft.Source, changed, "the editable body includes the actual multiplication instruction");
        var committed = session.CommitEdit(draft.Name, changed);
        Assert.AreEqual(7, committed.Method!.Invoke(null, [5]));
        Assert.AreEqual(10, original.Invoke(null, [5]));
        var reopened = session.PrepareEdit("Edited");
        Assert.AreEqual(changed, reopened.Source);
        Assert.AreSame(draft.Original, reopened.Original);
        Assert.AreSame(committed.Method, reopened.Method);

        session.AddLine("ldc.i4.5");
        session.AddLine("call int32 Edited(int32)");
        Assert.AreEqual(7, session.Run().Value);
    }

    /// <summary>
    /// Generated names distinguish drafts, and explicit collisions preserve existing methods and edit documents.
    /// </summary>
    [TestMethod]
    public void PrepareEdit_NamesAreCollisionFreeAndExplicitConflictsAreAtomic()
    {
        var session = IlLines.Load(".method int32 Existing() { ldc.i4.s 42; ret }");
        var first = session.PrepareEdit("Existing");
        var second = session.PrepareEdit("Existing");
        Assert.AreNotEqual(first.Name, second.Name);
        var names = session.Edits.Select(edit => edit.Name).ToArray();
        Assert.ThrowsExactly<ReplException>(() => session.PrepareEdit("Existing", "Existing"));
        Assert.ThrowsExactly<ReplException>(() => session.PrepareEdit("Existing", first.Name));
        Assert.AreSequenceEqual(names, session.Edits.Select(edit => edit.Name));
        Assert.AreEqual(42, session.Methods.Single().Version.Body.Invoke(null, null));
    }

    /// <summary>
    /// A later session method cannot take a name reserved by a draft or committed edit.
    /// </summary>
    /// <param name="committed">Whether the edit already has a callable revision.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AddLine_MethodNameConflictingWithEdit_IsRejectedBeforeOpening(bool committed)
    {
        var session = IlLines.Load(".method int32 Existing() { ldc.i4.s 41; ret }");
        var edit = session.PrepareEdit("Existing", "Copy");
        if (committed)
        {
            session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.s 41", "ldc.i4.s 42", StringComparison.Ordinal));
        }

        var revision = session.CompletionRevision;
        var error = Assert.ThrowsExactly<ReplException>(() => session.AddLine(".method int32 Copy() {"));

        Assert.AreEqual("'Copy' already belongs to an edit; choose another method name", error.Message);
        Assert.IsNull(session.OpenMethod);
        Assert.HasCount(1, session.Methods);
        Assert.AreEqual(revision, session.CompletionRevision);
        Assert.AreSame(edit, session.Edits.Single());
        if (!committed)
        {
            session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.s 41", "ldc.i4.s 42", StringComparison.Ordinal));
        }

        session.AddLine("call Copy");
        Assert.AreEqual(42, session.Run().Value);
        session.ClearCell();
        session.AddLine("call Existing");
        Assert.AreEqual(41, session.Run().Value);
    }

    /// <summary>
    /// Edit names are case-sensitive and do not reserve member names inside declared types.
    /// </summary>
    [TestMethod]
    public void AddLine_DistinctCaseAndQualifiedMember_DoNotConflictWithEdit()
    {
        var session = IlLines.Load(".method int32 Existing() { ldc.i4.s 41; ret }");
        var edit = session.PrepareEdit("Existing", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        foreach (var line in IlLines.Expand(".method int32 copy() { ldc.i4.s 42; ret }", ".class public Other {",
            ".method public static int32 Copy() { ldc.i4.s 43; ret }", "}"))
        {
            session.AddLine(line);
        }

        session.AddLine("call copy");
        Assert.AreEqual(42, session.Run().Value);
        session.ClearCell();
        session.AddLine("call Other::Copy()");
        Assert.AreEqual(43, session.Run().Value);
        session.ClearCell();
        session.AddLine("call Copy");
        Assert.AreEqual(41, session.Run().Value);
    }

    /// <summary>
    /// Clearing a cell retains reserved edit names while resetting the session releases them.
    /// </summary>
    [TestMethod]
    public void Reset_ReleasesEditNamesForSessionMethods()
    {
        var session = IlLines.Load(".method int32 Existing() { ldc.i4.s 41; ret }");
        session.PrepareEdit("Existing", "Copy");
        session.ClearCell();
        Assert.ThrowsExactly<ReplException>(() => session.AddLine(".method int32 Copy() {"));

        session.Reset();
        foreach (var line in IlLines.Expand(".method int32 Copy() { ldc.i4.s 42; ret }", "call Copy"))
        {
            session.AddLine(line);
        }

        Assert.IsEmpty(session.Edits);
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Importing a private helper keeps access and calls inside the copied declaring type.
    /// </summary>
    [TestMethod]
    public void CommitEdit_LoadedMethodCopiesPrivateHelperAndItsAccess()
    {
        var session = new Session();
        var (assembly, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var helper = new CecilMethodDefinition("Square", CecilMethodAttributes.Private | CecilMethodAttributes.Static,
                module.TypeSystem.Int32);
            helper.Parameters.Add(new ParameterDefinition("n", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
            type.Methods.Add(helper);
            var helperIl = helper.Body.GetILProcessor();
            helperIl.Emit(CecilOpCodes.Ldarg_0);
            helperIl.Emit(CecilOpCodes.Ldarg_0);
            helperIl.Emit(CecilOpCodes.Mul);
            helperIl.Emit(CecilOpCodes.Ret);
            var method = new CecilMethodDefinition("M", CecilMethodAttributes.Public | CecilMethodAttributes.Static,
                module.TypeSystem.Int32);
            method.Parameters.Add(new ParameterDefinition("n", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
            type.Methods.Add(method);
            method.Body.GetILProcessor().Emit(CecilOpCodes.Ldarg_0);
            method.Body.GetILProcessor().Emit(CecilOpCodes.Call, helper);
            method.Body.GetILProcessor().Emit(CecilOpCodes.Ret);
        }, session.Resolver);
        var draft = session.PrepareEdit($"int32 [{assembly.GetName().Name}]N.Fixture::M(int32)", "PrivateHelper");
        var committed = session.CommitEdit(draft.Name, draft.Source);
        Assert.AreEqual(49, committed.Method!.Invoke(null, [7]));
        Assert.AreEqual(49, fixture.GetMethod("M")!.Invoke(null, [7]));
        var copiedHelper = committed.Method.DeclaringType!.GetMethod("Square", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(copiedHelper);
        Assert.IsTrue(copiedHelper.IsPrivate);
        Assert.AreNotSame(fixture, committed.Method.DeclaringType);
        Assert.AreEqual(81, copiedHelper.Invoke(null, [9]));
    }

    /// <summary>
    /// A quoted explicit-interface method name containing dots and generic-looking angle brackets retains its interface dispatch mapping.
    /// </summary>
    [TestMethod]
    public void CommitEdit_ExplicitInterfaceNamePreservesDispatch()
    {
        var session = new Session();
        var (assembly, _, _) = CecilFixture.Build((module, type) =>
        {
            var contract = module.ImportReference(typeof(IComparable<int>));
            type.Interfaces.Add(new InterfaceImplementation(contract));
            var contractMethod = module.ImportReference(typeof(IComparable<int>).GetMethod("CompareTo")!);
            var implementation = new CecilMethodDefinition("System.IComparable<System.Int32>.CompareTo",
                CecilMethodAttributes.Private | CecilMethodAttributes.Final | CecilMethodAttributes.Virtual
                    | CecilMethodAttributes.NewSlot | CecilMethodAttributes.HideBySig, module.TypeSystem.Int32);
            implementation.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            implementation.Overrides.Add(contractMethod);
            type.Methods.Add(implementation);
            implementation.Body.GetILProcessor().Emit(CecilOpCodes.Ldarg_1);
            implementation.Body.GetILProcessor().Emit(CecilOpCodes.Ldc_I4_5);
            implementation.Body.GetILProcessor().Emit(CecilOpCodes.Sub);
            implementation.Body.GetILProcessor().Emit(CecilOpCodes.Ret);
            AddConstructor(module, type, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            var method = new CecilMethodDefinition("M", CecilMethodAttributes.Public, module.TypeSystem.Int32);
            method.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            type.Methods.Add(method);
            method.Body.GetILProcessor().Emit(CecilOpCodes.Ldarg_0);
            method.Body.GetILProcessor().Emit(CecilOpCodes.Ldarg_1);
            method.Body.GetILProcessor().Emit(CecilOpCodes.Callvirt, contractMethod);
            method.Body.GetILProcessor().Emit(CecilOpCodes.Ret);
        }, session.Resolver);
        var draft = session.PrepareEdit($"instance int32 [{assembly.GetName().Name}]N.Fixture::M(int32)", "ExplicitInterface");
        var committed = session.CommitEdit(draft.Name, draft.Source);
        var receiver = Activator.CreateInstance(committed.Method!.DeclaringType!);
        Assert.AreEqual(-2, committed.Method.Invoke(receiver, [3]));
        var comparable = Assert.IsInstanceOfType<IComparable<int>>(receiver);
        Assert.AreEqual(4, comparable.CompareTo(9));
        var map = receiver!.GetType().GetInterfaceMap(typeof(IComparable<int>));
        Assert.AreEqual("System.IComparable<System.Int32>.CompareTo", map.TargetMethods.Single().Name);
        Assert.IsTrue(map.TargetMethods.Single().IsPrivate);
    }

    /// <summary>
    /// Legal protected access to a visible base in the source assembly remains an external base call with the original inherited layout.
    /// </summary>
    [TestMethod]
    public void CommitEdit_ProtectedBaseAccessRetainsOriginalBaseIdentity()
    {
        var session = new Session();
        var (assembly, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var baseType = new TypeDefinition("N", "Base",
                CecilTypeAttributes.Public | CecilTypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(baseType);
            type.BaseType = baseType;
            var field = new FieldDefinition("Value", CecilFieldAttributes.Family, module.TypeSystem.Int32);
            baseType.Fields.Add(field);
            var baseConstructor = AddConstructor(module, baseType, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            var baseIl = baseConstructor.Body.GetILProcessor();
            baseIl.Remove(baseConstructor.Body.Instructions[^1]);
            baseIl.Emit(CecilOpCodes.Ldarg_0);
            baseIl.Emit(CecilOpCodes.Ldc_I4_S, (sbyte)40);
            baseIl.Emit(CecilOpCodes.Stfld, field);
            baseIl.Emit(CecilOpCodes.Ret);
            AddConstructor(module, type, baseConstructor);
            var helper = new CecilMethodDefinition("Advance", CecilMethodAttributes.Family, module.TypeSystem.Int32);
            helper.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            baseType.Methods.Add(helper);
            helper.Body.GetILProcessor().Emit(CecilOpCodes.Ldarg_0);
            helper.Body.GetILProcessor().Emit(CecilOpCodes.Ldfld, field);
            helper.Body.GetILProcessor().Emit(CecilOpCodes.Ldarg_1);
            helper.Body.GetILProcessor().Emit(CecilOpCodes.Add);
            helper.Body.GetILProcessor().Emit(CecilOpCodes.Ret);
            var method = new CecilMethodDefinition("M", CecilMethodAttributes.Public, module.TypeSystem.Int32);
            method.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            type.Methods.Add(method);
            method.Body.GetILProcessor().Emit(CecilOpCodes.Ldarg_0);
            method.Body.GetILProcessor().Emit(CecilOpCodes.Ldarg_1);
            method.Body.GetILProcessor().Emit(CecilOpCodes.Call, helper);
            method.Body.GetILProcessor().Emit(CecilOpCodes.Ret);
        }, session.Resolver);
        var draft = session.PrepareEdit($"instance int32 [{assembly.GetName().Name}]N.Fixture::M(int32)", "ProtectedBase");
        var committed = session.CommitEdit(draft.Name, draft.Source);
        var owner = committed.Method!.DeclaringType!;
        Assert.AreSame(fixture.BaseType, owner.BaseType);
        var receiver = Activator.CreateInstance(owner);
        Assert.AreEqual(42, committed.Method.Invoke(receiver, [2]));
        Assert.AreEqual(40, owner.GetField("Value", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(receiver));
        Assert.Contains(dependency => dependency.Symbol.Contains("Advance", StringComparison.Ordinal)
            && dependency.Disposition == "external", committed.Dependencies);
    }

    /// <summary>
    /// Instance copies preserve receivers, constructors, private property storage, and volatile field signatures.
    /// </summary>
    [TestMethod]
    public void CommitEdit_InstanceMethodPreservesConstructorFieldsAndReceiver()
    {
        var session = new Session();
        var assembly = session.Resolver.Load(SampleHost.Samples.FixturesDll);
        var originalType = assembly.GetType("Fixtures.Holder")!;
        var draft = session.PrepareEdit("instance int32 [Fixtures]Fixtures.Holder::Read()", "HolderEdit");
        var committed = session.CommitEdit(draft.Name, draft.Source);
        var method = committed.Method!;
        var owner = method.DeclaringType!;
        Assert.IsFalse(method.IsStatic);
        Assert.StartsWith("IlRepl.Edits.HolderEdit", owner.FullName!);
        Assert.AreNotSame(originalType, owner);
        var receiver = Activator.CreateInstance(owner, ["hello"]);
        Assert.IsNotNull(receiver);
        owner.GetField("Flag")!.SetValue(receiver, 10);
        Assert.AreEqual(15, method.Invoke(receiver, null));
        Assert.AreEqual("hello", owner.GetProperty("Name")!.GetValue(receiver));
        Assert.AreSequenceEqual(new[] { typeof(IsVolatile) },
            owner.GetField("Flag")!.GetRequiredCustomModifiers());
        var original = Activator.CreateInstance(originalType, ["hi"]);
        Assert.AreEqual(2, originalType.GetMethod("Read")!.Invoke(original, null));
    }

    /// <summary>
    /// A mutable struct retains its managed-reference receiver and changes the caller's boxed instance in place.
    /// </summary>
    [TestMethod]
    public void CommitEdit_ValueTypeReceiverPreservesMutation()
    {
        var session = IlLines.Load(
            ".class public sequential sealed Counter extends [System.Runtime]System.ValueType {",
            ".field public int32 Value",
            ".method public instance int32 Increment() {",
            "ldarg.0", "dup", "ldfld int32 Counter::Value", "ldc.i4.1", "add", "stfld int32 Counter::Value",
            "ldarg.0", "ldfld int32 Counter::Value", "ret", "}", "}");
        var draft = session.PrepareEdit("instance int32 Counter::Increment()", "CounterEdit");
        var committed = session.CommitEdit(draft.Name, draft.Source);
        var method = committed.Method!;
        var owner = method.DeclaringType!;
        Assert.IsTrue(owner.IsValueType);
        Assert.IsFalse(method.IsStatic);
        var receiver = Activator.CreateInstance(owner);
        owner.GetField("Value")!.SetValue(receiver, 40);
        Assert.AreEqual(41, method.Invoke(receiver, null));
        Assert.AreEqual(41, owner.GetField("Value")!.GetValue(receiver));
        Assert.AreEqual(42, method.Invoke(receiver, null));
        Assert.AreEqual(42, owner.GetField("Value")!.GetValue(receiver));
    }

    /// <summary>
    /// Generic methods retain constraints, execute valid instantiations, and reject invalid type arguments.
    /// </summary>
    [TestMethod]
    public void CommitEdit_GenericMethodPreservesConstraintAndExecution()
    {
        var session = new Session();
        session.Resolver.Load(SampleHost.Samples.FixturesDll);
        var draft = session.PrepareEdit("!!0 [Fixtures]Fixtures.Shapes::Larger<[1]>(!!0, !!0)", "LargerEdit");
        var committed = session.CommitEdit(draft.Name, draft.Source);
        var method = Assert.IsInstanceOfType<MethodInfo>(committed.Method);
        Assert.IsTrue(method.IsGenericMethodDefinition);
        var parameter = method.GetGenericArguments().Single();
        var constraint = parameter.GetGenericParameterConstraints().Single();
        Assert.AreEqual(typeof(IComparable<>), constraint.GetGenericTypeDefinition());
        Assert.AreSame(parameter, constraint.GenericTypeArguments.Single());
        Assert.AreEqual(7, method.MakeGenericMethod(typeof(int)).Invoke(null, [3, 7]));
        Assert.AreEqual("z", method.MakeGenericMethod(typeof(string)).Invoke(null, ["z", "a"]));
        Assert.ThrowsExactly<ArgumentException>(() => method.MakeGenericMethod(typeof(object)));
    }

    /// <summary>
    /// Owner and method generic parameters remain distinct through copied constructors, fields, and methods.
    /// </summary>
    [TestMethod]
    public void CommitEdit_GenericOwnerAndMethodKeepDistinctParameterBindings()
    {
        var session = IlLines.Load(
            ".class public Box`1<T> {",
            ".field public !0 Value",
            ".method public instance void .ctor(!0 v) {", "ldarg.0",
            "call instance void [System.Runtime]System.Object::.ctor()", "ldarg.0", "ldarg.1",
            "stfld !0 class Box`1<!0>::Value", "ret", "}",
            ".method public instance !!0 Choose<U>(!0 ignored, !!0 other) { ldarg.2; ret }",
            "}");
        var draft = session.PrepareEdit("instance !!0 Box`1::Choose<[1]>(!0, !!0)", "GenericOwner");
        var committed = session.CommitEdit(draft.Name, draft.Source);
        var definition = Assert.IsInstanceOfType<MethodInfo>(committed.Method);
        Assert.IsTrue(definition.IsGenericMethodDefinition);
        var owner = definition.DeclaringType!;
        Assert.IsTrue(owner.IsGenericTypeDefinition);
        Assert.AreEqual("T", owner.GetGenericArguments().Single().Name);
        Assert.AreEqual("U", definition.GetGenericArguments().Single().Name);
        var closedOwner = owner.MakeGenericType(typeof(int));
        var receiver = Activator.CreateInstance(closedOwner, [42]);
        var method = closedOwner.GetMethod(definition.Name)!.MakeGenericMethod(typeof(string));
        Assert.AreSequenceEqual(new[] { typeof(int), typeof(string) }, method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.AreEqual("chosen", method.Invoke(receiver, [7, "chosen"]));
        Assert.AreEqual(42, closedOwner.GetField("Value")!.GetValue(receiver));
    }

    /// <summary>
    /// Imported C# exception bodies preserve clause order, instructions, and normal and exceptional results.
    /// </summary>
    /// <param name="name">The compiled fixture method.</param>
    /// <param name="parameterType">The IL spelling of its parameter type.</param>
    /// <param name="input">The argument that selects the execution path.</param>
    /// <param name="expected">The expected result on that path.</param>
    [TestMethod]
    [DataRow("Safe", "int32", 0, 42)]
    [DataRow("Safe", "int32", 1, 1)]
    [DataRow("Guarded", "bool", true, 11)]
    [DataRow("Guarded", "bool", false, 2)]
    public void CommitEdit_CompiledExceptionBodyPreservesClausesAndBehavior(string name, string parameterType, object input, int expected)
    {
        var session = new Session();
        session.Resolver.Load(SampleHost.Samples.FixturesDll);
        var draft = session.PrepareEdit($"int32 [Fixtures]Fixtures.Shapes::{name}({parameterType})", name + "Edit");
        Assert.IsNotEmpty(draft.Original.Clauses);
        var committed = session.CommitEdit(draft.Name, draft.Source);
        Assert.AreEqual(expected, committed.Method!.Invoke(null, [input]));
        var copied = MethodDisassembler.Disassemble(committed.Method, session);
        Assert.AreEqual(draft.Original.InitLocals, copied.InitLocals);
        Assert.AreSequenceEqual(draft.Original.Clauses.Select(DescribeClause), copied.Clauses.Select(DescribeClause));
        Assert.AreSequenceEqual(draft.Original.Entries.Where(entry => entry.Raw is not null).Select(entry => entry.Raw!.Op.Name),
            copied.Entries.Where(entry => entry.Raw is not null).Select(entry => entry.Raw!.Op.Name));
    }

    /// <summary>
    /// Prepared copies retain captured dependencies when those dependencies are redefined before committing.
    /// </summary>
    [TestMethod]
    public void PrepareEdit_PinsSessionDependencyBeforeLaterRedefinition()
    {
        var session = IlLines.Load(".method int32 Helper() { ldc.i4.2; ret }",
            ".method int32 Caller() { call int32 Helper(); ldc.i4.3; mul; ret }");
        var draft = session.PrepareEdit("Caller", "CallerEdit");
        var original = draft.Original;
        Assert.AreEqual(6, draft.OriginalMethod.Invoke(null, null));
        foreach (var line in IlLines.Expand(".method int32 Helper() { ldc.i4.5; ret }"))
        {
            session.AddLine(line);
        }

        Assert.AreEqual(15, session.Methods.Single(method => method.Signature.Name == "Caller").Version.Body.Invoke(null, null));
        Assert.AreEqual(6, draft.OriginalMethod.Invoke(null, null), "captured execution keeps the old dependency binding");
        var committed = session.CommitEdit(draft.Name, draft.Source);
        Assert.AreEqual(6, committed.Method!.Invoke(null, null));
        Assert.AreSame(original, session.PrepareEdit("CallerEdit").Original);
        Assert.AreEqual(6, committed.Method.Invoke(null, null));
    }

    /// <summary>
    /// Invalid edits preserve the callable revision and original, and corrected submissions replace the revision.
    /// </summary>
    [TestMethod]
    public void CommitEdit_InvalidBodyPreservesPreviousCopyAndCanBeCorrected()
    {
        var session = IlLines.Load(".method int32 Double(int32 n) { ldarg.0; ldc.i4.2; mul; ret }");
        var draft = session.PrepareEdit("Double", "DoubleEdit");
        var committed = session.CommitEdit(draft.Name, draft.Source);
        var previous = committed.Method;
        var invalid = committed.Source.Replace(": mul", ": nop", StringComparison.Ordinal);
        Assert.AreNotEqual(committed.Source, invalid);
        Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(draft.Name, invalid));
        var retained = session.Edits.Single(edit => edit.Name == draft.Name);
        Assert.AreSame(previous, retained.Method);
        Assert.AreSame(draft.Original, retained.Original);
        Assert.AreEqual(10, retained.Method!.Invoke(null, [5]));
        var corrected = session.CommitEdit(draft.Name, draft.Source.Replace(": mul", ": add", StringComparison.Ordinal));
        Assert.AreEqual(7, corrected.Method!.Invoke(null, [5]));
    }

    /// <summary>
    /// Preparing and committing execute no user code; explicit invocation produces the expected environment effect.
    /// </summary>
    [TestMethod]
    public void PrepareAndCommit_DoNotRunUserCode()
    {
        var variable = "ILREPL_EDIT_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            var session = IlLines.Load(".method int32 Mark() {", $"ldstr \"{variable}\"", "ldstr \"ran\"",
                "call void [System.Runtime]System.Environment::SetEnvironmentVariable(string, string)", "ldc.i4.s 42", "ret", "}");
            var draft = session.PrepareEdit("Mark", "MarkEdit");
            Assert.IsNull(Environment.GetEnvironmentVariable(variable));
            var committed = session.CommitEdit(draft.Name, draft.Source);
            Assert.IsNull(Environment.GetEnvironmentVariable(variable));
            Assert.AreEqual(42, committed.Method!.Invoke(null, null));
            Assert.AreEqual("ran", Environment.GetEnvironmentVariable(variable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// Exported copied owners retain private support context and execute after resetting the parent session.
    /// </summary>
    [TestMethod]
    public void Export_CommittedInstanceFamilyRunsAfterSessionReset()
    {
        var session = new Session();
        session.Resolver.Load(SampleHost.Samples.FixturesDll);
        var draft = session.PrepareEdit("instance int32 [Fixtures]Fixtures.Holder::Read()", "ExportedHolder");
        var committed = session.CommitEdit(draft.Name, draft.Source);
        var ownerName = committed.Method!.DeclaringType!.FullName!;
        var methodName = committed.Method.Name;
        var image = AssemblyExporter.Write(session, "exported-edit");
        session.Reset();
        Assert.IsEmpty(session.Edits);
        var context = new AssemblyLoadContext("exported-edit-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(image));
            Assert.DoesNotContain(reference => reference.Name!.StartsWith("ilrepl", StringComparison.OrdinalIgnoreCase),
                assembly.GetReferencedAssemblies());
            var owner = assembly.GetType(ownerName, throwOnError: true)!;
            var receiver = Activator.CreateInstance(owner, ["saved"]);
            owner.GetField("Flag")!.SetValue(receiver, 37);
            Assert.AreEqual(42, owner.GetMethod(methodName)!.Invoke(receiver, null));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Copied runtime-generated accessors preserve metadata and reach the original private constructor and field.
    /// </summary>
    [TestMethod]
    public void CommitEdit_OriginalUnsafeAccessorDeclarationsRetainRuntimeImplementation()
    {
        var session = new Session();
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var target = new TypeDefinition("N", "AccessorTarget", CecilTypeAttributes.Public | CecilTypeAttributes.Class,
                module.TypeSystem.Object);
            module.Types.Add(target);
            var field = new FieldDefinition("_value", CecilFieldAttributes.Private, module.TypeSystem.Int32);
            target.Fields.Add(field);
            var constructor = new CecilMethodDefinition(".ctor", CecilMethodAttributes.Private | CecilMethodAttributes.HideBySig
                | CecilMethodAttributes.SpecialName | CecilMethodAttributes.RTSpecialName, module.TypeSystem.Void);
            constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            target.Methods.Add(constructor);
            var constructorIl = constructor.Body.GetILProcessor();
            constructorIl.Emit(CecilOpCodes.Ldarg_0);
            constructorIl.Emit(CecilOpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            constructorIl.Emit(CecilOpCodes.Ldarg_0);
            constructorIl.Emit(CecilOpCodes.Ldarg_1);
            constructorIl.Emit(CecilOpCodes.Stfld, field);
            constructorIl.Emit(CecilOpCodes.Ret);

            var create = new CecilMethodDefinition("Create",
                CecilMethodAttributes.Private | CecilMethodAttributes.Static | CecilMethodAttributes.HideBySig,
                module.TypeSystem.Object);
            create.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            var accessorConstructor = module.ImportReference(
                typeof(UnsafeAccessorAttribute).GetConstructor([typeof(UnsafeAccessorKind)])!);
            var typeAttributeConstructor = module.ImportReference(
                typeof(UnsafeAccessorTypeAttribute).GetConstructor([typeof(string)])!);
            var createAttribute = new CustomAttribute(accessorConstructor);
            createAttribute.ConstructorArguments.Add(new CustomAttributeArgument(
                module.ImportReference(typeof(UnsafeAccessorKind)), (int)UnsafeAccessorKind.Constructor));
            create.CustomAttributes.Add(createAttribute);
            var returnAttribute = new CustomAttribute(typeAttributeConstructor);
            returnAttribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,
                target.FullName + ", " + module.Assembly.Name.FullName));
            create.MethodReturnType.CustomAttributes.Add(returnAttribute);
            type.Methods.Add(create);

            var read = new CecilMethodDefinition("Value",
                CecilMethodAttributes.Private | CecilMethodAttributes.Static | CecilMethodAttributes.HideBySig,
                new ByReferenceType(module.TypeSystem.Int32));
            var receiver = new ParameterDefinition(module.TypeSystem.Object);
            var receiverAttribute = new CustomAttribute(typeAttributeConstructor);
            receiverAttribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,
                target.FullName + ", " + module.Assembly.Name.FullName));
            receiver.CustomAttributes.Add(receiverAttribute);
            read.Parameters.Add(receiver);
            var readAttribute = new CustomAttribute(accessorConstructor);
            readAttribute.ConstructorArguments.Add(new CustomAttributeArgument(
                module.ImportReference(typeof(UnsafeAccessorKind)), (int)UnsafeAccessorKind.Field));
            readAttribute.Properties.Add(new CecilAttributeArgument("Name",
                new CustomAttributeArgument(module.TypeSystem.String, "_value")));
            read.CustomAttributes.Add(readAttribute);
            type.Methods.Add(read);

            var method = new CecilMethodDefinition("Read", CecilMethodAttributes.Public | CecilMethodAttributes.Static,
                module.TypeSystem.Int32);
            type.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            il.Emit(CecilOpCodes.Ldc_I4, 42);
            il.Emit(CecilOpCodes.Call, create);
            il.Emit(CecilOpCodes.Call, read);
            il.Emit(CecilOpCodes.Ldind_I4);
            il.Emit(CecilOpCodes.Ret);
        }, session.Resolver);
        Assert.AreEqual(42, fixture.GetMethod("Read")!.Invoke(null, null));
        var edit = session.PrepareEdit("int32 N.Fixture::Read()", "AccessorCopy");

        session.CommitEdit(edit.Name, edit.Source);

        Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, null));
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        var owner = edit.Method.DeclaringType!;
        var createCopy = owner.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)!;
        var fieldCopy = owner.GetMethod("Value", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsNull(createCopy.GetMethodBody());
        Assert.IsNull(fieldCopy.GetMethodBody());
        Assert.AreEqual(UnsafeAccessorKind.Constructor, createCopy.GetCustomAttribute<UnsafeAccessorAttribute>()!.Kind);
        Assert.AreEqual(UnsafeAccessorKind.Field, fieldCopy.GetCustomAttribute<UnsafeAccessorAttribute>()!.Kind);
        Assert.AreEqual("_value", fieldCopy.GetCustomAttribute<UnsafeAccessorAttribute>()!.Name);
        var originalCreate = fixture.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)!;
        var originalField = fixture.GetMethod("Value", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.AreEqual(originalCreate.ReturnParameter.GetCustomAttribute<UnsafeAccessorTypeAttribute>()!.TypeName,
            createCopy.ReturnParameter.GetCustomAttribute<UnsafeAccessorTypeAttribute>()!.TypeName);
        Assert.AreEqual(originalField.GetParameters()[0].GetCustomAttribute<UnsafeAccessorTypeAttribute>()!.TypeName,
            fieldCopy.GetParameters()[0].GetCustomAttribute<UnsafeAccessorTypeAttribute>()!.TypeName);
    }

    private static string AbsoluteSource(MethodEdit draft)
    {
        var overflow = Assert.ThrowsExactly<OverflowException>(() => Math.Abs(int.MinValue));
        return string.Join('\n', new[]
        {
            draft.Source.Split('\n')[0], ".maxstack 2", "ldarg.0", "ldc.i4 -2147483648", "bne.un SAFE",
            "ldstr " + LiteralParser.Escape(overflow.Message), "newobj instance void OverflowException::.ctor(string)", "throw",
            "SAFE: ldarg.0", "dup", "ldc.i4.0", "bge DONE", "neg", "DONE: ret", "}",
        });
    }

    private static string DescribeClause(IlExceptionClause clause) =>
        $"{clause.Kind}:{clause.TryStart}:{clause.TryEnd}:{clause.FilterStart}:"
        + $"{clause.HandlerStart}:{clause.HandlerEnd}:{clause.CatchType?.FullName}";

    private static CecilMethodDefinition AddConstructor(ModuleDefinition module, TypeDefinition type, MethodReference baseConstructor)
    {
        var constructor = new CecilMethodDefinition(".ctor", CecilMethodAttributes.Public | CecilMethodAttributes.SpecialName
            | CecilMethodAttributes.RTSpecialName, module.TypeSystem.Void);
        type.Methods.Add(constructor);
        constructor.Body.GetILProcessor().Emit(CecilOpCodes.Ldarg_0);
        constructor.Body.GetILProcessor().Emit(CecilOpCodes.Call, baseConstructor);
        constructor.Body.GetILProcessor().Emit(CecilOpCodes.Ret);
        return constructor;
    }
}
