using System.Text.Json;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Verifies the source-generated native worker contract preserves reconstruction and attribution evidence.
/// </summary>
[TestClass]
public sealed class NativeProtocolTests
{
    /// <summary>
    /// Captured cells retain source and bindings while image-backed methods retain their exact closed identities.
    /// </summary>
    [TestMethod]
    public void NativePackage_RoundTripPreservesCellRecipeAndAssemblyGraph()
    {
        var implementation = new NativeMethodIdentity
        {
            Assembly = "ilrepl.methods.7, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Type = "IlRepl.Cell",
            Token = 0x06000002,
            DisplayName = "int32 Value<int32>(int32)",
            TypeArguments = ["System.String, System.Private.CoreLib"],
            MethodArguments = ["System.Int32, System.Private.CoreLib"],
            JitNames = ["IlRepl.Cell[System.String]:Value[int](int):int", "IlRepl.Cell[System.__Canon]:Value[int](int):int"],
        };

        var trampoline = implementation with { Assembly = "ilrepl.trampolines.6", Token = 0x06000001 };
        byte[] bytes = [0, 1, 127, 128, 255];
        var package = new NativePackage
        {
            Left = new NativeTarget
            {
                Name = "cell 3",
                Fingerprint = "cell-fingerprint",
                Cell = new NativeCell
                {
                    Declarations = [".args (int32 value = 42)", ".locals init (int32 saved)"],
                    Body = ["ldarg value", "call Value<int32>", "ret"],
                    TypeArguments = ["System.Int32, System.Private.CoreLib"],
                },
                Assemblies = [new NativeAssembly { Name = implementation.Assembly, Role = "methods", Image = bytes }],
                Bindings = [new NativeBinding { Name = "Value", Implementation = implementation, Trampoline = trampoline }],
                Types = new Dictionary<string, string> { ["Owner"] = "Owner, ilrepl.types.5" },
                Aliases = new Dictionary<string, NativeMethodIdentity> { ["Copy"] = implementation },
            },
            Right = new NativeTarget { Name = "Copy", Fingerprint = "method-fingerprint", Method = implementation },
            Options = new NativeOptions
            {
                Selector = "cell 3", Against = "Copy", Tier = "tier1", Run = true, Arguments = ["42"],
                Iterations = 321, TimeoutMilliseconds = 9876, StandardInput = "hello\nλ", FixtureDirectory = "fixture files",
                Assert = true, Raw = true, AllowInitializers = true, Pgo = false,
                Environment = new Dictionary<string, string> { ["DOTNET_PreferredVectorBitWidth"] = "128" },
            },
            Environment = new Dictionary<string, string> { ["DOTNET_EnableAVX512F"] = "0" },
            Culture = "fr-FR",
            UICulture = "ja-JP",
        };

        var json = JsonSerializer.Serialize(package, ProtocolJsonContext.Default.NativePackage);
        var restored = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.NativePackage);

        Assert.IsNotNull(restored);
        Assert.Contains("\"methodArguments\":[\"System.Int32, System.Private.CoreLib\"]", json);
        Assert.Contains("\"cell\":", json);
        Assert.IsNull(restored.Left.Method);
        Assert.IsNotNull(restored.Left.Cell);
        Assert.AreSequenceEqual(package.Left.Cell.Declarations, restored.Left.Cell.Declarations);
        Assert.AreSequenceEqual(package.Left.Cell.Body, restored.Left.Cell.Body);
        Assert.AreSequenceEqual(package.Left.Cell.TypeArguments, restored.Left.Cell.TypeArguments);
        Assert.AreEqual("cell-fingerprint", restored.Left.Fingerprint);
        var assembly = Assert.ContainsSingle(restored.Left.Assemblies);
        Assert.AreEqual(implementation.Assembly, assembly.Name);
        Assert.AreEqual("methods", assembly.Role);
        Assert.AreSequenceEqual(bytes, assembly.Image);
        var binding = Assert.ContainsSingle(restored.Left.Bindings);
        Assert.AreEqual("Value", binding.Name);
        Assert.AreEqual(trampoline.Assembly, binding.Trampoline.Assembly);
        Assert.AreEqual(trampoline.Token, binding.Trampoline.Token);
        AssertIdentity(implementation, binding.Implementation);
        Assert.AreEqual("Owner, ilrepl.types.5", restored.Left.Types["Owner"]);
        AssertIdentity(implementation, restored.Left.Aliases["Copy"]);
        Assert.IsNotNull(restored.Right);
        Assert.IsNull(restored.Right.Cell);
        Assert.IsNotNull(restored.Right.Method);
        AssertIdentity(implementation, restored.Right.Method);
        Assert.AreEqual("method-fingerprint", restored.Right.Fingerprint);
        Assert.AreEqual("tier1", restored.Options.Tier);
        Assert.IsTrue(restored.Options.Run);
        Assert.IsTrue(restored.Options.Assert);
        Assert.IsTrue(restored.Options.Raw);
        Assert.IsTrue(restored.Options.AllowInitializers);
        Assert.IsFalse(restored.Options.Pgo);
        Assert.AreSequenceEqual(["42"], restored.Options.Arguments!);
        Assert.AreEqual(321, restored.Options.Iterations);
        Assert.AreEqual(9876, restored.Options.TimeoutMilliseconds);
        Assert.AreEqual("hello\nλ", restored.Options.StandardInput);
        Assert.AreEqual("fixture files", restored.Options.FixtureDirectory);
        Assert.AreEqual("128", restored.Options.Environment["DOTNET_PreferredVectorBitWidth"]);
        Assert.AreEqual("0", restored.Environment["DOTNET_EnableAVX512F"]);
        Assert.AreEqual("fr-FR", restored.Culture);
        Assert.AreEqual("ja-JP", restored.UICulture);
    }

    /// <summary>
    /// Incomplete normalization retains raw code, wide runtime identities, and separate address evidence without implying equality.
    /// </summary>
    [TestMethod]
    public void NativeReply_RoundTripPreservesIncompleteEvidenceAndPublishedVersion()
    {
        const ulong address = 0xFEDCBA9876543210;
        var moduleVersionId = new Guid("a8efa8fd-b148-401b-ac75-7b93321c8531");
        var implementation = new NativeMethodIdentity
        {
            Assembly = "ilrepl.methods.7, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null", Type = "IlRepl.Cell",
            Token = 0x06000002, DisplayName = "string IlRepl.Cell::Value<int32>(int32)", ReturnsPointer = true,
            TypeArguments = ["System.String, System.Private.CoreLib"], MethodArguments = ["System.Int32, System.Private.CoreLib"],
            JitNames = ["IlRepl.Cell[System.String]:Value[int](int):int", "IlRepl.Cell[System.__Canon]:Value[int](int):int"],
        };

        var compilation = new NativeCompilation
        {
            Method = "IlRepl.Cell:Value(int)", Tier = "Instrumented Tier0", Pgo = "Dynamic", CodeSize = 23,
            MethodId = 0x123456789ABCDEF0, CodeVersion = 7, Address = address,
            Listing = "; Assembly listing for method IlRepl.Cell:Value(int) (Instrumented Tier0)\nret\n",
            Normalized = ["mov rax, <type:Owner>", "ret"],
            Inlinees = ["Owner:Helper()"],
        };

        var reply = new NativeReply
        {
            Outcome = "indeterminate",
            Left = new NativeReport
            {
                Outcome = "incomplete", Name = "Value", Fingerprint = "body-fingerprint", Raw = true,
                Implementation = implementation, ModuleVersionId = moduleVersionId, RuntimeIdentifier = "linux-arm64",
                Authorization = "explicit workload and required module initialization authorized",
                Runtime = ".NET 10.0.11", Jit = "jit-build-identity", Architecture = "Arm64", OperatingSystem = "Linux glibc",
                Collectible = false, Invocations = 79, StandardOutput = "workload output\n", StandardError = "workload error\n",
                InstructionSets = ["AdvSimd", "ArmBase"],
                Settings = new Dictionary<string, string> { ["DOTNET_TieredCompilation"] = "1" },
                Compilations = [compilation],
                UnattributedListings =
                [
                    "; Assembly listing for method IlRepl.Cell:Value(int) (Tier1)\n; BEGIN METHOD IlRepl.Cell:Value\nmov eax, 42\n",
                    "; Assembly listing for method IlRepl.Cell:Value(int) (Tier0)\n; BEGIN METHOD IlRepl.Cell:Value\nret\n"
                        + "; END METHOD IlRepl.Cell:Value\n; Total bytes of code 1\n",
                ],
                Addresses = [new NativeAddressFact
                {
                    Address = address, Length = 23, Kind = "code", Symbol = "Owner::Value", DisplaySymbol = "int32 Owner::Value()",
                    Evidence = "EventPipe MethodLoad",
                }],
                NormalizationProblems = ["unresolved indirect call cell"],
                Roles = ["implementation: IlRepl.Cell:Value(int)", "trampoline: IlRepl.Cell:Value(int)"],
            },
            Right = new NativeReport
            {
                Outcome = "incomplete", Name = "Other", Detail = "requested tier not observed", IsCapability = true,
            },
            Difference = ["? unresolved indirect call cell"],
        };

        var json = JsonSerializer.Serialize(reply, ProtocolJsonContext.Default.NativeReply);
        var restored = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.NativeReply);

        Assert.IsNotNull(restored);
        Assert.AreEqual("indeterminate", restored.Outcome);
        Assert.AreEqual("incomplete", restored.Left.Outcome);
        Assert.IsTrue(restored.Left.Raw);
        Assert.IsFalse(restored.Left.IsCapability);
        Assert.AreEqual("body-fingerprint", restored.Left.Fingerprint);
        Assert.IsNotNull(restored.Left.Implementation);
        AssertIdentity(implementation, restored.Left.Implementation);
        Assert.AreEqual(moduleVersionId, restored.Left.ModuleVersionId);
        Assert.AreEqual("linux-arm64", restored.Left.RuntimeIdentifier);
        Assert.AreEqual("explicit workload and required module initialization authorized", restored.Left.Authorization);
        Assert.AreEqual(".NET 10.0.11", restored.Left.Runtime);
        Assert.AreEqual("jit-build-identity", restored.Left.Jit);
        Assert.AreEqual("Arm64", restored.Left.Architecture);
        Assert.AreEqual("Linux glibc", restored.Left.OperatingSystem);
        Assert.IsFalse(restored.Left.Collectible);
        Assert.AreEqual(79, restored.Left.Invocations);
        Assert.AreEqual("workload output\n", restored.Left.StandardOutput);
        Assert.AreEqual("workload error\n", restored.Left.StandardError);
        Assert.AreSequenceEqual(reply.Left.InstructionSets, restored.Left.InstructionSets);
        Assert.AreEqual("1", restored.Left.Settings["DOTNET_TieredCompilation"]);
        var compiled = Assert.ContainsSingle(restored.Left.Compilations);
        Assert.AreEqual(compilation.Method, compiled.Method);
        Assert.AreEqual("Instrumented Tier0", compiled.Tier);
        Assert.AreEqual("Dynamic", compiled.Pgo);
        Assert.AreEqual(23, compiled.CodeSize);
        Assert.AreEqual(compilation.MethodId, compiled.MethodId);
        Assert.AreEqual(7UL, compiled.CodeVersion);
        Assert.AreEqual(address, compiled.Address);
        Assert.AreEqual(compilation.Listing, compiled.Listing);
        Assert.AreSequenceEqual(compilation.Normalized, compiled.Normalized);
        Assert.AreSequenceEqual(compilation.Inlinees, compiled.Inlinees);
        Assert.AreSequenceEqual(reply.Left.UnattributedListings, restored.Left.UnattributedListings);
        var fact = Assert.ContainsSingle(restored.Left.Addresses);
        Assert.AreEqual(address, fact.Address);
        Assert.AreEqual(23UL, fact.Length);
        Assert.AreEqual("code", fact.Kind);
        Assert.AreEqual("Owner::Value", fact.Symbol);
        Assert.AreEqual("int32 Owner::Value()", fact.DisplaySymbol);
        Assert.AreEqual("EventPipe MethodLoad", fact.Evidence);
        Assert.AreSequenceEqual(reply.Left.NormalizationProblems, restored.Left.NormalizationProblems);
        Assert.AreSequenceEqual(reply.Left.Roles, restored.Left.Roles);
        Assert.IsNotNull(restored.Right);
        Assert.AreEqual("incomplete", restored.Right.Outcome);
        Assert.IsTrue(restored.Right.IsCapability);
        Assert.IsFalse(restored.Right.Raw);
        Assert.AreEqual("requested tier not observed", restored.Right.Detail);
        Assert.AreSequenceEqual(reply.Difference, restored.Difference);
    }

    private static void AssertIdentity(NativeMethodIdentity expected, NativeMethodIdentity actual)
    {
        Assert.AreEqual(expected.Assembly, actual.Assembly);
        Assert.AreEqual(expected.Type, actual.Type);
        Assert.AreEqual(expected.Token, actual.Token);
        Assert.AreEqual(expected.DisplayName, actual.DisplayName);
        Assert.AreEqual(expected.ReturnsPointer, actual.ReturnsPointer);
        Assert.AreSequenceEqual(expected.TypeArguments, actual.TypeArguments);
        Assert.AreSequenceEqual(expected.MethodArguments, actual.MethodArguments);
        Assert.AreSequenceEqual(expected.JitNames, actual.JitNames);
    }
}
