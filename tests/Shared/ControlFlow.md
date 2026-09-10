# Control-flow rule checks

The analyzer checks stack flow, not the complete ECMA metadata and verification specification.
Each new correctness rejection needs a permitted counterpart. A verification failure alone does
not justify refusing a correct body. The 58 method examples and the paired constructor example
run unchanged on CoreCLR and browser Mono. The independent ILAsm fixtures only substitute
ILAsm's `} {` for the REPL's `} handler {` spelling.

`ControlFlowCorpusTests` checks symbolic preview, live acceptance, ILVerification's exact codes,
execution, `.il` reassembly, and `.save` execution. `LiveSessionTests.ControlFlow` enters the same
source in Chromium and WebKit, checks acceptance or recovery, then executes accepted methods.
`ControlFlowOperandTableTests` adds all 288 operand pairs in eight published numeric tables without
using the analyzer to build its expected results. Incorrect bodies are never executed.

| Rule | ECMA-335 reference | Accepted reproduction | Rejected reproduction |
| --- | --- | --- | --- |
| Entry, conditional edges and joins | III.1.8.1.1, III.1.8.1.3 | Diamond, MixedFloats | WrongDepth, WrongType |
| Worklist convergence and backward edges | III.1.7.5, III.1.8.1.1 | Loop | BackwardStack |
| Switch and unreachable instructions | III.3.66, III.1.8.1.1 | Switch, DeadCode | Underflow on a reachable path |
| Return shape and parameter assignment | III.3.57, I.8.7.3 | Diamond, NativeAddition | WrongReturn, WrongCall |
| Common array reference types | I.8.7.1, III.1.8.1.3 | ArrayJoin | ByrefJoin |
| Reduced pointer elements | I.8.7, III.1.8.1.2.3 | ReducedPointerJoin, EnumPointerJoin | ByrefJoin |
| Managed pointers and readonly provenance | III.1.8.1.2.2, III.2.3, III.3.62 | ManagedPointer, ReadOnlyLoad | WrongPrefix |
| Correct operations outside verification | III.1.8, III.3.47 | StackAllocation, ReadOnlyWrite, PointerDifference | WrongArithmetic |
| Numeric operand categories | III.1.5 tables III.2–III.8 | NativeAddition, MixedFloats, 288 raw pairs | BadOverflowFloat, BadNotFloat, BadShift |
| Comparisons | III.1.5 table III.4 | ObjectComparison | BadComparison |
| Reference and float operands | III.3.22, III.4.31 | Catch, MixedFloats | BadThrow, BadFinite |
| Exception entry and handler stacks | III.1.7.6, III.1.8.1.1 | Catch, Finally, CatchFinally, Fault, Filter | NonemptyTry, WrongFilterStack |
| Protected returns and transfers | III.3.46, III.3.57 | Catch, Finally, Fault | ReturnInTry |
| Protected-region entry | III.3.15 | Catch, Finally | BranchIntoTry |
| Prefix boundaries and applicability | III.2 | TailCall, UnalignedLoad, ReadOnlyLoad | WrongPrefix, BranchIntoPrefix |
| Generic identity and boxing | III.1.8.1.1–III.1.8.1.3 | GenericBox, GenericReference | GenericNeedsBox, GenericDistinct |
| Generic object references | I.8.7.1, III.1.8.1.1 | GenericReferenceThrow | GenericNeedsBoxThrow |
| Runtime extensions to constrained calls | .NET ECMA-335 Augments | StaticAbstract_ImplementedAndCalled | Existing member eligibility tests |
| Constrained receiver type | III.2.1 | ConstrainedReceiver | WrongConstrainedReceiver |
| Readonly store receiver across paths | II.16.1.2 | FlowReceiver with this on both paths | FlowReceiver with another receiver |
| Indirect calls | III.3.20 | IndirectCall | WrongIndirectCall |
| Array index and pointer operands | III.3.42, III.4.7 | ArrayIndex, ManagedPointer | WrongArrayIndex, WrongIndirectLoad |
| Stack allocation depth | III.3.47 | StackAllocation | WrongAllocationStack |
| Transitive generic constraints | III.1.8.1.2.3 | TransitiveBox | GenericNeedsBox |
| Header stack limit | III.1.7.4, II.25.4.3 | DeepStack, DeadCode through live and both exports | Raw underflow fixture |

Forward labels remain incomplete while editing. `ControlFlowSessionTests` and `ControlFlowPreviewTests`
check that a later edge revisits an earlier instruction, identifies the producers, and that correcting
or removing the edge recomputes the stack. `EngineAnalysisTests`, `HostServerRpcTests`, and
`AnalysisRequesterTests` cover source positions, stale replies, cancellation, disposal, navigation,
and withdrawal of an entire refused block. Existing viewport tests check actual terminal frames.

## Disagreements with Microsoft.ILVerification 10.0.11

The numeric fixtures separately assert ECMA correctness and the library result. These are pinned
observations, not skipped assertions. Review them when changing the verifier package.

`ILImporter.Verify.cs` selects the larger `StackValueKind` in `ImportBinaryOperation` and permits a
mixed pair whenever that kind is native integer. It consequently accepts int64/native-integer
pairs for add, sub, mul, and, and add.ovf that the corresponding ECMA operand tables exclude.

`ILImporter.StackValue.cs` returns immediately for equal stack kinds and types in `IsBinaryComparable`.
It accepts `cgt` on two null references although table III.4 limits reference comparisons. Its Int32
case also accepts an Int64 counterpart in one operand order. Byref/native-integer equality succeeds
without a warning although table III.4 marks that combination unverifiable.

A `class T` constraint lets CoreCLR pass an unboxed generic reference to an object parameter, but
ILVerification reports `StackUnexpected`. `GenericReference` stays executable and carries an
unverifiable diagnostic; `GenericBox` explicitly boxes the parameter and verifies successfully.
`GenericReferenceThrow` likewise runs with its reference constraint while the library reports
`StackObjRef`. An unconstrained parameter cannot be treated as an object without boxing.

ECMA-335 III.3.15 forbids an ordinary branch across a protected-region boundary. The library's
`IsValidBranchTarget` instead accepts a branch to the first instruction of a directly nested try.
`BranchIntoTry` pins that false negative while the analyzer refuses the transfer.

The library reports `PathStackUnexpected` when merging int32 and uint32 managed pointers, or an
enum pointer and its underlying integer pointer. ECMA I.8.7 gives these the same verification
type. `ReducedPointerJoin` and `EnumPointerJoin` check that they remain accepted and executable.

The library reports `ImportCalli not implemented` for both indirect-call fixtures. The tests
assert that exact unsupported-operation failure separately; it is not treated as verification
success or as evidence that the invalid argument is rejected. ECMA III.3.20 supplies the argument
rule, and the accepted body runs through desktop, both exports, and browser Mono.

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
