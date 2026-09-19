# Method editing validation

Issue #15 is covered by actual CLR execution, independently assembled IL fixtures, real host processes,
and a headless terminal. Tests use the existing Microsoft testing tools and the repository's existing
dependencies. No mock engines or simulated comparison results are used.

## Requirement evidence

Suite names below refer to files under `tests/IlRepl.Tests`. Each suite asserts concrete values, metadata,
observations, or state transitions.

| Requirement | Evidence suites |
| --- | --- |
| Exact locals, initialization, stack limits, all exception layouts | `EditableBodyTests`, `NoPrefixTests` |
| CLR pointer/byref rules and pinned cleanup | `NativeByRefCallTests`, `PinnedLocalResetTests` |
| Metadata, constraints, native helpers, property signatures, return defaults | `ImportedMetadataTests`, `ExtendedImportedMetadataTests` |
| RVA bytes without executing source initialization | `ImportedMetadataTests` |
| Instance and struct owners, private helpers, dispatch, recursive calls, delegates | `MethodEditTests`, `MethodEditPrivateTests` |
| Dependency closure independent of discovery order | `MethodEditDependencyDiscoveryTests`, `MethodEditGenericBoundaryTests` |
| Private nested generic aliases and revision binding | `MethodEditAccessibilityTests` |
| Direct closed generic calls and captured session type arguments | `DirectGenericComparisonTests` |
| Constraint-only dependencies and invalid generic closures | `MethodEditConstraintClosureTests` |
| Pinned originals, dependent rebuilds, atomic failed commits | `MethodEditRebuildTests`, `EditPreviewTests` |
| Independent saved images and Microsoft ILAsm execution | `MethodEditExportTests`, metadata and accessibility suites |
| Normalized instructions, branch targets, raw bytes and independent stacks | `MethodEditDiffTests`, `MethodEditAlignmentTests` |
| Typed literal limits and unchanged inputs after errors | `NumericArgumentTests` |
| Returns, console output, exceptions, ref/out, mutable receivers and async | `MethodComparisonTests` |
| Object graphs, cycles, scalar bits, aliases and unavailable values | `MethodComparisonObservationTests`, `ScalarAliasComparisonTests` |
| Managed storage aliases and byref return identity | `MethodComparisonAliasTests` |
| Static state, culture, environment, stdin and filesystem isolation | `MethodComparisonIsolationTests`, `ComparisonFixtureTests` |
| Timeout, crash, excessive output, cancellation and parent survival | `MethodComparisonIsolationTests` |
| Corrected framework copies compared with actual original execution | `ExternalOriginalScenarioTests`, `MethodComparisonObservationTests` |
| Source documents, dependency provenance, one-use tickets, stale replies, batch exits | `MethodEditingProcessTests` |
| Draft completion, exact replacement ranges, preview and recovery | `EditCompletionTests`, `EditPreviewTests`, `EditCommandTests` |
| Keyboard editing, full source history, diff and comparison | `MethodEditingTerminalTests` |
| Published guide and sample transcript execute exactly through RPC | `EditDocumentationTests` |

`[TestMethod]` cases use the actual Microsoft ILAsm and ILVerification oracles where applicable. The
`no.` prefix tests preserve its bytes and explicitly assert the real CLR refusal.

The replayable sample is [`editing-methods.il`](../samples/Transcripts/editing-methods.il). Its comparison
outcomes are `different` and then `match`; its cell results are exactly `42`, `41`, and `41`.

## Reproduce validation

Run from the repository root with the SDK selected by `global.json`:

```sh
dotnet test
dotnet scripts/Highlight-Cil.cs --verify
dotnet scripts/Publish-Wasm.cs --configuration Debug
pnpm --dir docs build:site
dotnet scripts/Publish-NativeAot.cs --rid linux-x64
```

The test project automatically restores Microsoft ILAsm and ILDAsm 10.0.11 for the current platform.
Missing assembler or disassembler tools fail the relevant tests. No environment variables are required.
Native AOT packaging includes the repository smoke test; the
published executable can also run the sample transcript directly.

Collect production coverage with the same settings used for this change:

```sh
dotnet test \
  --coverage --coverage-settings tests/coverage.config.xml \
  --coverage-output-format cobertura --coverage-output artifacts/coverage/method-editing.cobertura.xml \
  --results-directory artifacts/coverage --report-trx
```

## Coverage interpretation

Coverage is collected with the existing Microsoft coverage extension. Line hits establish execution;
they do not replace assertions about metadata or observed behavior. The requirement matrix above is
the acceptance evidence for those behaviors.

The reviewed gaps include startup and diagnostic paths, malformed metadata guards, uncommon marshalling
descriptors, and alternative ILAsm metadata representations. These are distinct from the worker collection
boundary below. Coverage percentages do not establish that every branch or combination has been tested.

Fresh comparison workers intentionally receive the captured environment. Coverage profiler settings
are not a reason to change that environment. Consequently, collection does not cover every worker-only
branch even when process tests execute and assert that branch.

## Export conformance

`ControlFlowExamples` is also the export corpus. Each accepted example runs through a real host, an
independently authored ILAsm image, `.save`, assembled `.il`, and an ILDAsm-to-ILAsm round trip. Both
`.il` renderers run for each example, with a committed method copy selecting the Cecil renderer.
`ExportConformanceTests` adds atomic publication, input isolation, and deliberate corruption controls.

Run the export checks with:

```sh
dotnet test --project tests/IlRepl.Tests/IlRepl.Tests.csproj --filter TestCategory=ExportConformance
```

The independent metadata reader compares authored signatures, constraints, layouts, attributes,
accessors, overrides, complete assembly binding identities, local signatures, instructions, branch
destinations, and exception boundaries.
Tokens, instruction offsets, timestamps, and MVIDs do not identify semantic differences. ILDAsm drops
an empty local declaration, so its initialization flag is compared only when locals or `localloc`
make it meaningful. Authored names and required or optional modifiers remain exact.

Every execution starts a fresh process and requests an 8 MiB execution-thread stack. Deterministic
and tiered profiles explicitly set tiering, tiered PGO, Quick JIT, loop Quick JIT, and ReadyToRun before
runtime startup. The runner scrubs inherited `DOTNET_*` and `COMPlus_*` settings, supplies the complete
child environment, fixes cultures and input EOF, and restores the same working directory before each
artifact. Nonempty-input checks also call unchanged independent and exported images through the
existing comparison worker, covering raw and managed readers, Unicode, mixed line endings, and EOF.
These settings apply only to fixture children.

Failed fixtures retain source, generated images, tool package identities, requests, process outputs,
and observations below the test executable's `artifacts/export-conformance` directory. CI uploads
these records alongside TRX results. Missing tools, deadline expiration, unexpected verifier failures,
or differences in exact expected verification codes fail the check.

The separate browser workflow generates its test-only image bundle from this same corpus and runs it
in fresh Mono WebAssembly workers. Browser tests remain outside `dotnet test`.

Native AOT validation runs the interruption, explicit replacement, retained-source, and offline-save
checks against both published files and the extracted runtime package. Unix checks also replace the
lifetime supervisor while an invocation runs and verify that the host and its static values survive. Matching Alpine containers run
musl publication and validation with a musl .NET host. Successful validation writes
`artifacts/native-aot/<rid>/smoke-results.json`, including package and executable hashes and the actual
runtime identity. `--build-only` deliberately produces no distributable package; it is useful when
cross-publishing on a machine that cannot run the target runtime.
