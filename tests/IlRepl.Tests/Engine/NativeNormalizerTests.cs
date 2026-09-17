using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Verifies address normalization uses runtime evidence while preserving program constants and instruction effects.
/// </summary>
[TestClass]
public sealed class NativeNormalizerTests
{
    /// <summary>
    /// Proven code ranges normalize addresses and offsets without changing unrelated immediates or displacements.
    /// </summary>
    [TestMethod]
    public void Normalize_ProvenCodeRangePreservesConstantsAndDisplacements()
    {
        var listing = Listing("Owner:Value", "    mov rax, 0x12345688\n    call rax\n"
            + "    mov rcx, 0x400000\n    mov rax, qword ptr [rcx+0x12345678]\n    ret");
        NativeAddressFact[] facts = [Fact(0x12345678, "code", "Owner:Called", 32)];

        var normalized = NativeNormalizer.Normalize(listing, facts, [], [], "X64", [0x400000]);

        Assert.IsEmpty(normalized.Problems);
        Assert.Contains("    mov rax, <code:Owner:Called+0x10>", normalized.Lines);
        Assert.Contains("    call rax", normalized.Lines);
        Assert.Contains("    mov rcx, 0x400000", normalized.Lines);
        Assert.Contains("    mov rax, qword ptr [rcx+0x12345678]", normalized.Lines);
        Assert.Contains("0x12345688", listing.Listing, "The original listing must remain available for raw inspection.");
    }

    /// <summary>
    /// A real numeric constant equal to an observed address retains its exact bits instead of becoming a symbol.
    /// </summary>
    [TestMethod]
    public void Normalize_NumericLiteralEqualToKnownAddressIsPreserved()
    {
        var listing = Listing("Owner:Value", "    mov rax, 0x12345678\n    ret");
        NativeAddressFact[] facts = [Fact(0x12345678, "type", "Owner")];

        var normalized = NativeNormalizer.Normalize(listing, facts, [], [], "X64", [0x12345678]);

        Assert.Contains("    mov rax, 0x12345678", normalized.Lines);
        Assert.DoesNotContain(line => line.Contains("<type:", StringComparison.Ordinal), normalized.Lines);
        Assert.IsEmpty(normalized.Problems);
    }

    /// <summary>
    /// Scalar return metadata preserves coincident address bits even when they were not captured as an original IL literal.
    /// </summary>
    /// <param name="architecture">The worker architecture.</param>
    /// <param name="move">The instruction placing the numeric result in its return register.</param>
    [TestMethod]
    [DataRow("X64", "    mov rax, 0x12345678")]
    [DataRow("Arm64", "    mov x0, #0x12345678")]
    public void Normalize_ScalarReturnPreservesCoincidentKnownAddress(string architecture, string move)
    {
        var listing = Listing("Owner:Value", move + "\n    ret");

        var normal = NativeNormalizer.Normalize(listing, [Fact(0x12345678, "type", "Owner")], [], [], architecture, [],
            pointerReturn: false);

        Assert.Contains(move, normal.Lines);
        Assert.Contains("    ret", normal.Lines);
        Assert.DoesNotContain(line => line.Contains('<'), normal.Lines);
        Assert.IsEmpty(normal.Problems);
    }

    /// <summary>
    /// Known pointer returns require proof for nonzero addresses while null, original literals, and unknown return kinds stay numeric.
    /// </summary>
    /// <param name="architecture">The worker architecture.</param>
    /// <param name="register">The architecture's return register.</param>
    /// <param name="value">The result loaded before returning.</param>
    /// <param name="pointerReturn">The available closed return-type classification.</param>
    /// <param name="literal">Whether the value was captured as an original numeric literal.</param>
    /// <param name="expectProblem">Whether the return requires missing pointer evidence.</param>
    [TestMethod]
    [DataRow("X64", "rax", 0x12345678UL, true, false, true)]
    [DataRow("Arm64", "x0", 0x12345678UL, true, false, true)]
    [DataRow("X64", "rax", 0UL, true, false, false)]
    [DataRow("Arm64", "x0", 0UL, true, false, false)]
    [DataRow("X64", "rax", 0x12345678UL, true, true, false)]
    [DataRow("X64", "rax", 0x12345678UL, null, false, false)]
    public void Normalize_PointerReturnDistinguishesUnknownAddressFromNumericEvidence(string architecture, string register,
        ulong value, bool? pointerReturn, bool literal, bool expectProblem)
    {
        var move = $"    mov {register}, 0x{value:X}";
        var listing = Listing("Owner:Value", move + "\n    ret");

        var normal = NativeNormalizer.Normalize(listing, [], [], [], architecture, literal ? [value] : [], pointerReturn);

        Assert.Contains(move, normal.Lines);
        Assert.DoesNotContain(line => line.Contains('<'), normal.Lines);
        if (expectProblem) Assert.AreEqual("unproven pointer use through " + register, Assert.ContainsSingle(normal.Problems));
        else Assert.IsEmpty(normal.Problems);
    }

    /// <summary>
    /// Unknown absolute memory and contradictory observations make normal comparison incomplete rather than guessing.
    /// </summary>
    /// <param name="conflicting">Whether two different identities claim the same observed address.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Normalize_UnprovenAbsoluteMemoryReportsMissingEvidence(bool conflicting)
    {
        var listing = Listing("Owner:Value", "    mov rax, qword ptr [0x12345678]\n    ret");
        NativeAddressFact[] facts = conflicting
            ? [Fact(0x12345678, "static-field", "Owner::First"), Fact(0x12345678, "static-field", "Owner::Second")]
            : [];

        var normalized = NativeNormalizer.Normalize(listing, facts, [], [], "X64", []);

        Assert.Contains("    mov rax, qword ptr [0x12345678]", normalized.Lines);
        Assert.Contains("unproven absolute memory operand 0x12345678", normalized.Problems);
    }

    /// <summary>
    /// Known compilation-only probes establish static field and guard identities without requiring execution.
    /// </summary>
    [TestMethod]
    public void Normalize_StaticProbeProvesReturnedFieldAddressAndInitializationGuard()
    {
        var listing = Listing("Owner:Value", "    test byte ptr [0x12345000], 1\n"
            + "    mov rax, qword ptr [0x12345678]\n    ret");
        var probeListing = Listing("Probe:Address", "    test byte ptr [0x12345000], 1\n    mov rax, 0x12345678\n    ret");
        NativeProbe[] probes = [new() { Method = "Probe:Address", Kind = "static-field", Symbol = "Owner::Value" }];

        var normalized = NativeNormalizer.Normalize(listing, [], probes, [listing, probeListing], "X64", []);

        Assert.IsEmpty(normalized.Problems);
        Assert.Contains("    test byte ptr [<initialization-guard:Owner>], 1", normalized.Lines);
        Assert.Contains("    mov rax, qword ptr [<static-field:Owner::Value>]", normalized.Lines);
        var field = Assert.ContainsSingle(normalized.Addresses.Where(fact => fact.Kind == "static-field"));
        Assert.AreEqual(0x12345678UL, field.Address);
        Assert.Contains("never invoked", field.Evidence);
    }

    /// <summary>
    /// Independently located Arm64 address chunks reconstruct one identity before they are normalized.
    /// </summary>
    [TestMethod]
    public void Normalize_Arm64MovzMovkSequencesProduceEquivalentSymbolsAcrossProcesses()
    {
        var left = Listing("Owner:Value", "    movz x0, #0x5678\n    movk x0, #0x1234, lsl #16\n"
            + "    movk x0, #0x7FFF, lsl #32\n    ret");
        var right = Listing("Owner:Value", "    movz x0, #0x2468\n    movk x0, #0x1357, lsl #16\n"
            + "    movk x0, #0x7FFE, lsl #32\n    ret");

        var normalLeft = NativeNormalizer.Normalize(left, [Fact(0x7FFF12345678, "string", "hello")], [], [], "Arm64", []);
        var normalRight = NativeNormalizer.Normalize(right, [Fact(0x7FFE13572468, "string", "hello")], [], [], "Arm64", []);

        Assert.AreSequenceEqual(normalLeft.Lines, normalRight.Lines);
        Assert.Contains("    movz x0, bits0:15(<string:hello>)", normalLeft.Lines);
        Assert.Contains("    movk x0, bits16:31(<string:hello>), lsl #16", normalLeft.Lines);
        Assert.Contains("    movk x0, bits32:47(<string:hello>), lsl #32", normalLeft.Lines);
        Assert.IsEmpty(normalLeft.Problems);
        Assert.IsEmpty(normalRight.Problems);
    }

    /// <summary>
    /// Arm64 constant construction remains numeric even when its reassembled value equals a runtime handle.
    /// </summary>
    [TestMethod]
    public void Normalize_Arm64LiteralChunksRemainDistinct()
    {
        var listing = Listing("Owner:Value", "    movz x0, #0x5678\n    movk x0, #0x1234, lsl #16\n    ret");

        var normal = NativeNormalizer.Normalize(listing, [Fact(0x12345678, "type", "Owner")], [], [], "Arm64", [0x12345678]);

        Assert.Contains("    movz x0, #0x5678", normal.Lines);
        Assert.Contains("    movk x0, #0x1234, lsl #16", normal.Lines);
        Assert.DoesNotContain(line => line.Contains("<type:", StringComparison.Ordinal), normal.Lines);
    }

    /// <summary>
    /// Arm64 move-not and move-keep obey 32-bit zero extension before matching an observed handle.
    /// </summary>
    [TestMethod]
    public void Normalize_Arm64MovnAndMovkRespectRegisterWidth()
    {
        var listing = Listing("Owner:Value", "    movn w0, #0xA987\n    movk w0, #0x1234, lsl #16\n    ret");

        var normal = NativeNormalizer.Normalize(listing, [Fact(0x12345678, "type", "Owner")], [], [], "Arm64", []);

        Assert.Contains("    movn w0, ~bits0:15(<type:Owner>)", normal.Lines);
        Assert.Contains("    movk w0, bits16:31(<type:Owner>), lsl #16", normal.Lines);
        Assert.IsEmpty(normal.Problems);
    }

    /// <summary>
    /// Overwriting a pointer register through its 32-bit alias removes the prior address proof.
    /// </summary>
    /// <param name="architecture">The runtime architecture.</param>
    /// <param name="instructions">A pointer value whose register is overwritten before its use.</param>
    [TestMethod]
    [DataRow("Arm64", "    movz x0, #0x5678\n    movk x0, #0x1234, lsl #16\n    mov w0, #42\n    ret")]
    [DataRow("X64", "    mov rax, 0x12345678\n    mov eax, 42\n    ret")]
    [DataRow("X64", "    mov r8, 0x12345678\n    xor r8d, r8d\n    call r8")]
    [DataRow("X64", "    mov r8, 0x12345678\n    xor r8w, r8w\n    call r8")]
    [DataRow("X64", "    mov r8, 0x12345678\n    xor r8b, r8b\n    call r8")]
    [DataRow("X64", "    mov rax, 0x12345678\n    mov al, 42\n    ret")]
    [DataRow("X64", "    mov rax, 0x12345678\n    xor ax, ax\n    ret")]
    public void Normalize_OverwrittenRegisterDoesNotRetainProof(string architecture, string instructions)
    {
        var listing = Listing("Owner:Value", instructions);

        var normal = NativeNormalizer.Normalize(listing, [Fact(0x12345678, "code", "Owner:Called")], [], [], architecture, []);

        Assert.DoesNotContain(line => line.Contains("<code:", StringComparison.Ordinal), normal.Lines);
        Assert.IsEmpty(normal.Problems);
    }

    /// <summary>
    /// Register values do not flow across unknown control-flow joins or volatile call clobbers.
    /// </summary>
    /// <param name="interruption">The instruction sequence that invalidates a previously observed register value.</param>
    [TestMethod]
    [DataRow("    call Owner:Unrelated()")]
    [DataRow("    jne G_M137_IG02\nG_M137_IG02:")]
    public void Normalize_InterruptedValueFlowDoesNotInventProof(string interruption)
    {
        var listing = Listing("Owner:Value", "    mov rax, 0x12345678\n" + interruption + "\n    ret");

        var normal = NativeNormalizer.Normalize(listing, [Fact(0x12345678, "type", "Owner")], [], [], "X64", []);

        Assert.Contains("    mov rax, 0x12345678", normal.Lines);
        Assert.DoesNotContain(line => line.Contains("<type:", StringComparison.Ordinal), normal.Lines);
    }

    /// <summary>
    /// ADR normalizes a proven absolute target without discarding the distinct address-generation instruction.
    /// </summary>
    [TestMethod]
    public void Normalize_Arm64AdrPreservesInstructionAndUsesProvenAddress()
    {
        var listing = Listing("Owner:Value", "    adr x0, #0x12345678\n    ret");

        var normal = NativeNormalizer.Normalize(listing, [Fact(0x12345678, "type", "Owner")], [], [], "Arm64", []);

        Assert.Contains("    adr x0, <type:Owner>", normal.Lines);
        Assert.IsEmpty(normal.Problems);
    }

    /// <summary>
    /// ADRP and ADD preserve page and low-bit roles while eliminating both workers' independently observed address pieces.
    /// </summary>
    [TestMethod]
    public void Normalize_Arm64PageAndAddPreserveDistinctOperandRoles()
    {
        var left = Listing("Owner:Value", "    adrp x0, #0x12345000\n    add x0, x0, #0x678\n    ret");
        var right = Listing("Owner:Value", "    adrp x0, #0x56789000\n    add x0, x0, #0xABC\n    ret");

        var normalLeft = NativeNormalizer.Normalize(left, [Fact(0x12345678, "type", "Owner")], [], [], "Arm64", []);
        var normalRight = NativeNormalizer.Normalize(right, [Fact(0x56789ABC, "type", "Owner")], [], [], "Arm64", []);

        Assert.AreSequenceEqual(normalLeft.Lines, normalRight.Lines);
        Assert.Contains("    adrp x0, page(<type:Owner>)", normalLeft.Lines);
        Assert.Contains("    add x0, x0, lo12(<type:Owner>)", normalLeft.Lines);
        Assert.IsEmpty(normalLeft.Problems);
        Assert.IsEmpty(normalRight.Problems);
    }

    /// <summary>
    /// An Arm64 load's offset contributes to the proven field address without being mistaken for a loaded pointer value.
    /// </summary>
    [TestMethod]
    public void Normalize_Arm64PageAndLoadProveEffectiveFieldAddress()
    {
        var listing = Listing("Owner:Value", "    adrp x1, #0x12345000\n    ldr x0, [x1, #0x678]\n    ret");

        var normal = NativeNormalizer.Normalize(listing, [Fact(0x12345678, "static-field", "Owner::Value", 8)], [], [], "Arm64", []);

        Assert.Contains("    adrp x1, page(<static-field:Owner::Value>)", normal.Lines);
        Assert.Contains("    ldr x0, [x1, lo12(<static-field:Owner::Value>)]", normal.Lines);
        Assert.IsEmpty(normal.Problems);
    }

    /// <summary>
    /// Release CoreCLR's explicit high and low relocation annotations provide distinct symbolic address components.
    /// </summary>
    [TestMethod]
    public void Normalize_Arm64AnnotatedRelocationsUseObservedAddress()
    {
        var listing = Listing("Owner:Value", "    adrp x1, [HIGH RELOC #0x12345678]\n"
            + "    ldr x0, [x1, [LOW RELOC #0x12345678]]\n    ret");

        var normal = NativeNormalizer.Normalize(listing, [Fact(0x12345678, "static-field", "Owner::Value", 8)], [], [], "Arm64", []);

        Assert.Contains(line => line.Contains("page(<static-field:Owner::Value>)", StringComparison.Ordinal), normal.Lines);
        Assert.Contains(line => line.Contains("lo12(<static-field:Owner::Value>)", StringComparison.Ordinal), normal.Lines);
        Assert.DoesNotContain(line => line.Contains("0x12345678", StringComparison.Ordinal), normal.Lines);
        Assert.IsEmpty(normal.Problems);
    }

    /// <summary>
    /// Data-pool values remain exact scalar bits so equal load instructions cannot hide a changed constant.
    /// </summary>
    [TestMethod]
    public void Normalize_Arm64LiteralPoolPreservesNumericPayload()
    {
        var left = Listing("Owner:Value", "    ldr d0, [@RWD00]\n    ret\nRWD00  dq 1234567812345678h");
        var right = Listing("Owner:Value", "    ldr d0, [@RWD00]\n    ret\nRWD00  dq 1234567812345679h");
        NativeAddressFact[] facts = [Fact(0x1234567812345678, "type", "Owner")];

        var normalLeft = NativeNormalizer.Normalize(left, facts, [], [], "Arm64", [0x1234567812345678]);
        var normalRight = NativeNormalizer.Normalize(right, facts, [], [], "Arm64", [0x1234567812345679]);

        Assert.Contains("RWD00  dq 1234567812345678h", normalLeft.Lines);
        Assert.Contains("RWD00  dq 1234567812345679h", normalRight.Lines);
        Assert.AreNotEqual(string.Join('\n', normalLeft.Lines), string.Join('\n', normalRight.Lines));
        Assert.DoesNotContain(line => line.Contains("<type:", StringComparison.Ordinal), normalLeft.Lines);
        Assert.IsEmpty(normalLeft.Problems);
        Assert.IsEmpty(normalRight.Problems);
    }

    /// <summary>
    /// Returning or dereferencing an observed pointer loaded from a literal pool normalizes the pool payload itself.
    /// </summary>
    /// <param name="use">The instructions consuming the pool value.</param>
    [TestMethod]
    [DataRow("    ret")]
    [DataRow("    ldr x0, [x0]\n    ret")]
    public void Normalize_Arm64LiteralPoolPointerUsesObservedIdentity(string use)
    {
        var left = Listing("Owner:Value", "    ldr x0, [@RWD00]\n" + use + "\nRWD00  dq 00007FFF12345678h");
        var right = Listing("Owner:Value", "    ldr x0, [@RWD00]\n" + use + "\nRWD00  dq 00007FFE87654321h");

        var normalLeft = NativeNormalizer.Normalize(left, [Fact(0x7FFF12345678, "type", "Owner")], [], [], "Arm64", []);
        var normalRight = NativeNormalizer.Normalize(right, [Fact(0x7FFE87654321, "type", "Owner")], [], [], "Arm64", []);

        Assert.AreSequenceEqual(normalLeft.Lines, normalRight.Lines);
        Assert.Contains("    ldr x0, [@RWD00]", normalLeft.Lines);
        Assert.Contains("RWD00  dq <type:Owner>", normalLeft.Lines);
        Assert.IsEmpty(normalLeft.Problems);
        Assert.IsEmpty(normalRight.Problems);
        Assert.Contains("00007FFF12345678h", left.Listing);
    }

    /// <summary>
    /// Dereferencing an unproven literal-pool value retains its bits and reports the missing pointer evidence.
    /// </summary>
    [TestMethod]
    public void Normalize_Arm64UnprovenLiteralPoolPointerReportsMissingEvidence()
    {
        var listing = Listing("Owner:Value", "    ldr x0, [@RWD00]\n    ldr x0, [x0]\n    ret\nRWD00  dq 00007FFF12345678h");

        var normal = NativeNormalizer.Normalize(listing, [], [], [], "Arm64", []);

        Assert.Contains("RWD00  dq 00007FFF12345678h", normal.Lines);
        Assert.Contains("unproven pointer use through x0", normal.Problems);
        Assert.DoesNotContain(line => line.Contains('<'), normal.Lines);
    }

    /// <summary>
    /// Signed RIP displacements use the published code address and every preceding instruction's real encoding length.
    /// </summary>
    /// <param name="displacement">The signed displacement printed in the address operand.</param>
    /// <param name="bytes">The real little-endian encoding of that displacement.</param>
    /// <param name="targetRange">The start of the observed target code range.</param>
    [TestMethod]
    [DataRow("+0x20", "20000000", 0x100020UL)]
    [DataRow("-0x20", "E0FFFFFF", 0xFFFE0UL)]
    public void Normalize_X64RipRelativeAddressUsesEncodedInstructionLocations(string displacement, string bytes, ulong targetRange)
    {
        var listing = Listing("Owner:Value", "    90 nop\n    488D05" + bytes + " lea rax, [rip" + displacement + "]\n    C3 ret")
            with { Address = 0x100000, CodeSize = 9 };

        var normal = NativeNormalizer.Normalize(listing, [Fact(targetRange, "code", "Owner:Called", 16)], [], [], "X64", []);

        Assert.Contains("    nop", normal.Lines);
        Assert.Contains("    lea rax, [rip+rel32(<code:Owner:Called+0x8>)]", normal.Lines);
        Assert.Contains("    ret", normal.Lines);
        Assert.IsEmpty(normal.Problems);
        Assert.Contains("488D05" + bytes, listing.Listing);
    }

    /// <summary>
    /// Missing published addresses or incomplete encoding coverage cannot prove a RIP-relative target.
    /// </summary>
    /// <param name="hasAddress">Whether the compilation carries a published code address.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Normalize_X64RipRelativeAddressRequiresCompleteLocationEvidence(bool hasAddress)
    {
        var listing = Listing("Owner:Value", "    488D0520000000 lea rax, [rip+0x20]\n    C3 ret")
            with { Address = hasAddress ? 0x100000UL : 0UL, CodeSize = hasAddress ? 9 : 8 };

        var normal = NativeNormalizer.Normalize(listing, [Fact(0x100027, "code", "Owner:Called")], [], [], "X64", []);

        Assert.Contains("    lea rax, [rip+0x20]", normal.Lines);
        Assert.Contains("missing instruction location for [rip+0x20]", normal.Problems);
        Assert.DoesNotContain(line => line.Contains('<'), normal.Lines);
    }

    /// <summary>
    /// Explicit x64 relocation annotations resolve only with matching runtime observations.
    /// </summary>
    /// <param name="hasEvidence">Whether the relocated field address has a runtime observation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Normalize_X64ExplicitRelocationRequiresObservedIdentity(bool hasEvidence)
    {
        var listing = Listing("Owner:Value", "    mov rax, qword ptr [reloc 0x12345678]\n    ret");
        NativeAddressFact[] facts = hasEvidence ? [Fact(0x12345678, "static-field", "Owner::Value", 8)] : [];

        var normal = NativeNormalizer.Normalize(listing, facts, [], [], "X64", []);

        if (hasEvidence)
        {
            Assert.Contains("    mov rax, qword ptr [reloc <static-field:Owner::Value>]", normal.Lines);
            Assert.IsEmpty(normal.Problems);
        }
        else
        {
            Assert.Contains("    mov rax, qword ptr [reloc 0x12345678]", normal.Lines);
            Assert.Contains("unproven relocation 0x12345678", normal.Problems);
        }
    }

    /// <summary>
    /// CoreCLR's parenthesized x64 relocation spelling retains its operand structure while resolving the address.
    /// </summary>
    /// <param name="operand">The immediate or memory operand containing a relocation.</param>
    /// <param name="expected">The same operand with its observed address symbolized.</param>
    [TestMethod]
    [DataRow("(reloc 0x12345678)", "(reloc <static-field:Owner::Value>)")]
    [DataRow("qword ptr [(reloc 0x12345678)]", "qword ptr [(reloc <static-field:Owner::Value>)]")]
    public void Normalize_X64ParenthesizedRelocationPreservesOperandStructure(string operand, string expected)
    {
        var listing = Listing("Owner:Value", "    mov rax, " + operand + "\n    ret");

        var normal = NativeNormalizer.Normalize(listing, [Fact(0x12345678, "static-field", "Owner::Value", 8)], [], [], "X64", []);

        Assert.Contains("    mov rax, " + expected, normal.Lines);
        Assert.IsEmpty(normal.Problems);
        Assert.DoesNotContain(line => line.Contains("0x12345678", StringComparison.Ordinal), normal.Lines);
    }

    /// <summary>
    /// Segment-relative TLS offsets remain numeric even when the same number matches an unrelated observed address.
    /// </summary>
    /// <param name="segment">The x64 TLS segment printed by CoreCLR.</param>
    [TestMethod]
    [DataRow("FS")]
    [DataRow("GS")]
    public void Normalize_X64SegmentOffsetsDoNotBecomeAbsoluteAddressSymbols(string segment)
    {
        var instruction = "    mov rax, qword ptr " + segment + ":[0x0058]";
        var listing = Listing("Owner:Value", instruction + "\n    ret");

        var normal = NativeNormalizer.Normalize(listing, [Fact(0x58, "static-field", "Unrelated::Value", 8)], [], [], "X64", []);

        Assert.Contains(instruction, normal.Lines);
        Assert.IsEmpty(normal.Problems);
        Assert.DoesNotContain(line => line.Contains('<'), normal.Lines);
    }

    /// <summary>
    /// A paired Arm64 load invalidates both destination registers before either can be treated as a proven call argument.
    /// </summary>
    [TestMethod]
    public void Normalize_Arm64PairedLoadInvalidatesBothRegisters()
    {
        var listing = Listing("Owner:Value", "    mov x1, #0x12345678\n    ldp x0, x1, [sp]\n    bl Owner:Call()\n    ret");

        var normal = NativeNormalizer.Normalize(listing, [Fact(0x12345678, "type", "Owner")], [], [], "Arm64", []);

        Assert.Contains("    mov x1, #0x12345678", normal.Lines);
        Assert.DoesNotContain(line => line.Contains("<type:", StringComparison.Ordinal), normal.Lines);
    }

    /// <summary>
    /// CoreCLR's tail-call spelling consumes the same proven function pointer as an ordinary indirect call.
    /// </summary>
    [TestMethod]
    public void Normalize_TailJumpUsesProvenEntryPoint()
    {
        var listing = Listing("Owner:Value", "    mov rax, 0x12345678\n    tail.jmp rax");

        var normal = NativeNormalizer.Normalize(listing, [Fact(0x12345678, "entry-point", "Owner:Target")], [], [], "X64", []);

        Assert.Contains("    mov rax, <entry-point:Owner:Target>", normal.Lines);
        Assert.Contains("    tail.jmp rax", normal.Lines);
        Assert.IsEmpty(normal.Problems);
    }

    /// <summary>
    /// Pointer use requires evidence regardless of numeric magnitude and retains unresolved operands in the normal view.
    /// </summary>
    /// <param name="immediate">The unproven scalar value subsequently used as a memory address.</param>
    [TestMethod]
    [DataRow("42")]
    [DataRow("0x400000")]
    public void Normalize_UnprovenRegisterDereferenceReportsMissingEvidence(string immediate)
    {
        var listing = Listing("Owner:Value", "    mov rcx, " + immediate + "\n    mov rax, qword ptr [rcx+0x12345678]\n    ret");

        var normal = NativeNormalizer.Normalize(listing, [], [], [], "X64", []);

        Assert.Contains("    mov rcx, " + immediate, normal.Lines);
        Assert.Contains("    mov rax, qword ptr [rcx+0x12345678]", normal.Lines);
        Assert.Contains(problem => problem.Contains("unproven", StringComparison.Ordinal), normal.Problems);
        Assert.DoesNotContain(line => line.Contains('<'), normal.Lines);
    }

    private static NativeAddressFact Fact(ulong address, string kind, string symbol, ulong length = 1) => new()
    {
        Address = address, Length = length, Kind = kind, Symbol = symbol, Evidence = "observed in worker",
    };

    private static NativeCompilation Listing(string method, string instructions) => new()
    {
        Method = method + "():long", Tier = "FullOpts", CodeSize = 16,
        Listing = "; Assembly listing for method " + method + "():long (FullOpts)\n; BEGIN METHOD " + method
            + "\n" + instructions + "\n; END METHOD " + method + "\n; Total bytes of code 16\n",
    };
}
