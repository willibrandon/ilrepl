# Control-flow rule checks

The analyzer checks stack flow, not the complete ECMA metadata and verification specification.
Each new correctness rejection needs a permitted counterpart. A verification failure alone does
not justify refusing a correct body. The 152 method examples and the paired constructor example
run unchanged on CoreCLR and browser Mono. The independent ILAsm fixtures only substitute
ILAsm's `} {` for the REPL's `} handler {` spelling.

`ControlFlowCorpusTests` checks symbolic preview, live acceptance, ILVerification's exact codes,
execution, `.il` reassembly, and `.save` execution. `LiveSessionTests.ControlFlow` enters the same
source in Chromium and WebKit, checks acceptance or recovery, then executes accepted methods.
`ControlFlowOperandTableTests` adds all 288 operand pairs in eight published numeric tables without
using the analyzer to build its expected results. Incorrect bodies are never executed.
Native ILAsm changes a static `callvirt` reference to an instance signature, so that one verifier
fixture assembles a static `call` and changes only its opcode in metadata before verification.

| Rule | ECMA-335 reference | Accepted reproduction | Rejected reproduction |
| --- | --- | --- | --- |
| Entry, conditional edges and joins | III.1.8.1.1, III.1.8.1.3 | Diamond, MixedFloats | WrongDepth, WrongType |
| Worklist convergence and backward edges | III.1.7.5, III.1.8.1.1 | Loop | BackwardStack, UnreachableForwardBackwardStack |
| Switch and unreachable instructions | III.3.66, III.1.8.1.1 | Switch, DeadCode | Underflow on a reachable path |
| Return shape and parameter assignment | III.3.57, I.8.7.3 | Diamond, NativeAddition | WrongReturn, WrongCall |
| Virtual calls, constructors, and function pointers | III.3.19, III.4.18, III.4.21 | ConstrainedReceiver, VirtualFunctionPointer | WrongStaticConstructorAllocation, WrongStaticVirtualCall, WrongStaticVirtualFunctionPointer |
| Instance receiver representation | I.12.4.1.4, II.13.3 | ValueTypeReceiver, NativeValueTypeReceiver, PointerValueTypeReceiver | WrongManagedReferenceReceiver, WrongPointerValueTypeReceiver, WrongUnboxedValueTypeReceiver |
| Common array reference types | I.8.7.1, III.1.8.1.3 | ArrayJoin | ByrefJoin |
| Reduced pointer elements | I.8.7, III.1.8.1.2.3 | ReducedPointerJoin, EnumPointerJoin | ByrefJoin |
| Managed and unmanaged pointers | III.1.8.1.2.2, III.3.42, III.3.62, III.4.4–III.4.5, III.4.10–III.4.11, III.4.13, III.4.29 | ManagedPointer, NativeFieldAddress, NativeFieldLoad, NativeFieldStore, NativeIndirectLoad, NativeIndirectStore, NativeObjectCopy, NativeObjectInitialize, NativeObjectLoad, NativeObjectStore, PointerFields, IndirectReferenceStore, UnmanagedReferenceStore, GenericIndirectReference | WrongPointerField, WrongIndirectReferenceStore, WrongUnmanagedReferenceStore, WrongGenericIndirectLoad, WrongGenericIndirectStore |
| Field storage form | III.4.10–III.4.12, III.4.24–III.4.31 | PointerFields, StaticField, StaticFieldToken | WrongStaticFieldOpcode, WrongInstanceFieldOpcode |
| Readonly provenance | III.2.3, III.3.62 | ReadOnlyLoad, ReadOnlyFieldWrite | WrongPrefix |
| Correct operations outside verification | III.1.8, III.3.47 | ManagedPointerOverflowAddition, NativeValueTypeReceiver, PointerDifference, ReadOnlyWrite, StackAllocation | WrongArithmetic, WrongAllocationHandler |
| Numeric operand categories | III.1.5 tables III.2–III.8, III.3.27 | ManagedPointerOverflowAddition, ManagedPointerOverflowDifference, ManagedPointerOverflowSubtraction, MixedFloats, NativeAddition, UnsignedIntegerToFloat, 288 raw pairs | BadOverflowFloat, BadNotFloat, BadShift, WrongUnsignedFloatConversion |
| Comparisons | III.1.5 table III.4 | ObjectComparison, GenericReferenceComparison | BadComparison, WrongGenericComparison |
| Reference and float operands | III.3.22, III.3.27, III.4.31 | Catch, MixedFloats | BadThrow, BadFinite, WrongReferenceConversion |
| Exception entry and handler stacks | III.1.7.6, III.1.8.1.1 | Catch, Finally, CatchFinally, EndfinallyClearsStack, Fault, Filter, RethrowPreservesStack | NonemptyTry, WrongFilterStack |
| Protected returns and transfers | III.3.37, III.3.46, III.3.57 | Catch, Finally, Fault, Jump, LeaveWithinTry | JumpFromTry, JumpFromSynchronizedMethod, ReturnInTry, WrongJumpSignature |
| Protected-region entry | III.3.15 | Catch, Finally | BranchIntoTry |
| Prefix boundaries, operands, and applicability | III.2 | TailCall, SynchronizedTailCall, UnalignedLoad, VolatileObjectLoad, VolatileObjectStore, ReadOnlyLoad | WrongPrefix, WrongUnalignedValue, BranchIntoPrefix |
| Generic identity and boxing | III.1.8.1.1–III.1.8.1.3 | GenericBox, GenericReference | GenericNeedsBox, GenericDistinct |
| Generic object references | I.8.7.1, III.1.8.1.1 | GenericReferenceThrow, GenericReferenceBranch | GenericNeedsBoxThrow, GenericNeedsBoxBranch |
| Runtime extensions to constrained calls | .NET ECMA-335 Augments | StaticAbstract_ImplementedAndCalled | Existing member eligibility tests |
| Constrained receiver type | III.2.1 | ConstrainedReceiver | WrongConstrainedReceiver |
| Readonly store receiver across paths | II.16.1.2 | FlowReceiver with this on both paths | FlowReceiver with another receiver |
| Indirect calls | III.3.20 | IndirectCall | WrongIndirectCall, WrongIndirectTarget |
| Block memory operands | III.3.30, III.3.36 | CopyBlock, InitializeBlock | WrongCopyBlock, WrongInitializeBlock |
| Array element, index and pointer operands | I.8.7.1, III.4.7–III.4.9, III.4.26–III.4.27 | ArrayElement, ArrayIndex, ArrayReferenceLoad, ArrayReferenceStore, GenericArrayReferenceLoad, GenericArrayReferenceStore, NullArrayReferenceStore, TypedArrayReferenceStore | WrongArrayElement, WrongArrayIndex, WrongArrayValue, WrongArrayReferenceStore, WrongGenericArrayReferenceLoad, WrongNullArrayReferenceStore, WrongTypedArrayReferenceStore, WrongValueArrayReferenceLoad, WrongValueArrayReferenceStore |
| Object copy operands | III.4.4 | CopyObject, CopyReferenceObject | WrongCopyObjectSource, WrongCopyObjectSourceType, WrongCopyObjectDestinationType |
| Typed references | III.4.19, III.4.22–III.4.23 | TypedReference | WrongMakeTypedReference, WrongTypedReferenceType, WrongTypedReferenceValue |
| Unboxing | III.4.32 | UnboxValue | WrongUnboxType |
| Stack allocation depth | III.3.47 | StackAllocation | WrongAllocationStack |
| Transitive generic constraints | III.1.8.1.2.3 | TransitiveBox | GenericNeedsBox |
| Header stack limit | III.1.7.4, II.25.4.3 | DeepStack, DeadCode through live and both exports | Raw underflow fixture |

Forward labels remain incomplete while editing. `ControlFlowSessionTests` and `ControlFlowPreviewTests`
check that a later edge revisits an earlier instruction, identifies the producers, and that correcting
or removing the edge recomputes the stack. `EngineAnalysisTests`, `HostServerRpcTests`, and
`AnalysisRequesterTests` cover source positions, stale replies, cancellation, disposal, navigation,
and withdrawal of an entire refused block. Existing viewport tests check actual terminal frames.

ECMA III.2.4 says a synchronized method ignores `tail.` so its lock remains held until the call
returns. `SynchronizedTailCall` verifies and returns 42 through CoreCLR, browser Mono, ILAsm, and
the saved assembly.

ECMA I.12.4.1.4 gives a value-type method a pointer to its unboxed instance. A managed pointer is
verifiable; an unmanaged pointer or native integer is correct but unverifiable. A class method
instead requires an object reference. The receiver fixtures preserve that distinction and reject
an unboxed value or managed pointer to a reference variable before either can reach the runtime.

## Disagreements with Microsoft.ILVerification 10.0.11

The numeric fixtures separately assert ECMA correctness and the library result. These are pinned
observations, not skipped assertions. Review them when changing the verifier package.

`ILImporter.Verify.cs` selects the larger `StackValueKind` in `ImportBinaryOperation` and permits a
mixed pair whenever that kind is native integer. It consequently accepts int64/native-integer
pairs for add, sub, mul, and, and add.ovf that the corresponding ECMA operand tables exclude.

The same importer reports `ExpectedIntegerType` for the managed-pointer forms of `add.ovf.un` and
`sub.ovf.un`. ECMA table III.7 explicitly permits pointer/integer addition, pointer/integer
subtraction, and pointer/pointer subtraction for these unsigned overflow instructions as correct
but unverifiable IL. All three forms execute through CoreCLR, browser Mono, ILAsm, and `.save`.

`ILImporter.StackValue.cs` returns immediately for equal stack kinds and types in `IsBinaryComparable`.
It accepts `cgt` on two null references although table III.4 limits reference comparisons. Its Int32
case also accepts an Int64 counterpart in one operand order. Byref/native-integer equality succeeds
without a warning although table III.4 marks that combination unverifiable.

A `class T` constraint lets CoreCLR pass an unboxed generic reference to an object parameter, but
ILVerification reports `StackUnexpected`. `GenericReference` stays executable and carries an
unverifiable diagnostic; `GenericBox` explicitly boxes the parameter and verifies successfully.
`GenericReferenceThrow` likewise runs with its reference constraint while the library reports
`StackObjRef`, and `GenericReferenceBranch` reports `StackUnexpected`. An unconstrained parameter
cannot be treated as an object reference without boxing.

CoreCLR and Mono also execute `ldind.ref` and `stind.ref` through a managed pointer to a `class T`
parameter. ECMA III.3.42 and III.3.62 exclude generic parameters from correct use of those short
forms, and ILVerification reports `StackUnexpected`. `GenericIndirectReference` records the runtime
extension while an unconstrained parameter remains rejected.

ILVerification accepts `ceq` over an unconstrained generic parameter even though the parameter can
be an arbitrary value type. `WrongGenericComparison` closes that gap while the class-constrained
`GenericReferenceComparison` remains accepted.

ILVerification accepts a floating-point input to `conv.r.un`, although ECMA III.3.27 requires an
integer, and an integer value passed to `stelem.ref` when the tracked array is null, although ECMA
III.4.27 requires a reference. The paired integer conversion and null-reference store remain valid.

ILVerification ignores the operand of `unaligned.`. ECMA III.2.5 permits only 1, 2, or 4, so
`WrongUnalignedValue` pins the analyzer's rejection while `UnalignedLoad` uses a permitted value.

ECMA-335 III.3.15 forbids an ordinary branch across a protected-region boundary. The library's
`IsValidBranchTarget` instead accepts a branch to the first instruction of a directly nested try.
`BranchIntoTry` pins that false negative while the analyzer refuses the transfer.

ECMA-335 specifies `rethrow` with an unchanged stack transition and says `endfinally` and `leave`
empty the stack as side effects. It does not require `leave` to cross a region boundary. The
library and CoreCLR agree; RethrowPreservesStack, EndfinallyClearsStack, and LeaveWithinTry keep
those correct bodies accepted.

The library reports `PathStackUnexpected` when merging int32 and uint32 managed pointers, or an
enum pointer and its underlying integer pointer. ECMA I.8.7 gives these the same verification
type. `ReducedPointerJoin` and `EnumPointerJoin` check that they remain accepted and executable.

The library accepts `ldelem.i1` over a `bool[]`, using their common verification type. Array
instructions use the narrower array-element compatibility relation in ECMA I.8.7.1, whose reduced
types keep `bool` distinct from `int8`, so `WrongBooleanArrayLoad` remains a correctness error.

The library reports `ImportCalli not implemented` for both indirect-call fixtures. The tests
assert that exact unsupported-operation failure separately; it is not treated as verification
success or as evidence that the invalid argument is rejected. ECMA III.3.20 supplies the argument
rule, and the accepted body runs through desktop, both exports, and browser Mono.

The library reports only `Unverifiable` for `jmp` inside a try. ECMA III.3.37 makes that transfer
incorrect as well as unverifiable, so `JumpFromTry` pins the analyzer's stricter rejection.

The library counts an unreachable forward branch as the lower-offset predecessor required by
ECMA III.1.7.5, so it accepts a later backward branch carrying a stack into that target.
`UnreachableForwardBackwardStack` keeps the unreachable edge from hiding the correctness error.

The library records `CallVirtOnStatic` for a static target and then dereferences its absent
instance type, ending verification with `NullReferenceException`. `WrongStaticVirtualCall`
pins that exact unsupported failure while both analyzers reject the source directly.
The same importer failure occurs when raw metadata gives `newobj` a static `.cctor`; the source
binder and decoded-body analyzer both reject `WrongStaticConstructorAllocation`.

The library reports only `Unverifiable` when `cpblk` or `initblk` receives an object reference
where the instruction requires an address. ECMA III.3.30 and III.3.36 make those operand shapes
incorrect. The bad size and initialization-value cases also report `ExpectedIntegerType`.

The library checks that both `cpobj` operands are managed pointers but leaves their element-type
assignment checks as a TODO. ECMA III.4.4 requires the source element to assign to the operand type
and the operand type to assign to the destination element. The analyzer rejects both wrong directions.

The library cannot inspect a fixture containing typed-reference instructions and reports
`TypedReference not supported in .NET Core`. ECMA III.4.19 and III.4.22–III.4.23 define the
accepted and rejected operand shapes, which CoreCLR and browser Mono exercise independently.

The library reports `ExpectedNumericType` when `conv.u` turns a managed address into the unmanaged
pointer used by the field and memory fixtures. ECMA III.3.27 permits that correct but unverifiable
conversion, and the memory instructions accept a native integer address. CoreCLR and browser Mono
execute each field, indirect, and object load, store, address, initialization, copy, and value-type
receiver reproduction.

For a native integer address made from an integer, the library instead reports `StackByRef`; it also
reports `StackUnexpected` for `initobj`. ECMA III.3.42, III.3.62, III.4.4–III.4.5, III.4.13, and
III.4.29 permit the address in correct but unverifiable CIL.

The current runtime augments ECMA's `constrained.` prefix with static interface `call` and `ldftn`.
The parser and analyzer accept those forms; the published callvirt-only rule is insufficient here.

The independent verifier resolves framework metadata without running constructors or fixture bodies.
Missing metadata and uncategorized verifier failures fail the fixture instead of counting as the
expected rejection. Source fixture rejections assert the original verifier codes, including
`PathStackDepth`, `PathStackUnexpected`, `StackUnderflow`, `TryNonEmptyStack`, and `ReadOnly`.

Run these checks with the repository's pinned SDK and verifier package. Browser tests require
publishing the current WASM build and rebuilding the docs before running the headless browser suite.

Reference checkouts used for this audit: dotnet/runtime at
`6b1fb3c43c8a5478c592813ee2e263fe4375af4c` and ECMA-335 at
`f181e4696eebcbbc7c2b1e5d0a2ee289f2884d2d`. The project SDK is 10.0.302.
