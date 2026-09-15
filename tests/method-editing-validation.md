# Method editing validation

Issue #15 is covered by actual CLR execution, independently assembled IL fixtures, real host processes,
a headless terminal, and Chromium and WebKit. Tests use the existing Microsoft testing tools and the
repository's existing dependencies. No mock engines or simulated comparison results are used.

## Results

Validated on Linux x64 with .NET SDK 10.0.400, selected by the repository's SDK roll-forward setting.

| Check | Result |
| --- | --- |
| Complete desktop suite | 3,019 passed, 0 failed, 3 existing skips |
| Complete browser suite | 200 passed in Chromium and WebKit, 0 failed, 0 skipped |
| Additional browser generic constructor cases | 2 passed, 0 failed, 0 skipped |
| Revised public docs | 3 RPC example tests and 12 site checks passed |
| Documentation transcript replay | 44 transcripts, 0 differences |
| Documentation site | 20 pages built |
| WebAssembly publish | Passed |
| Linux x64 Native AOT | Published, smoke tested, and packed; editing transcript passed |

Production coverage is 45,239 of 50,680 lines (89.26%) and 25,504 of 30,604 branches (83.34%).
The three desktop skips are the Windows-only history path and two child-process entry methods whose
parent tests execute them. No feature case is skipped.

## Requirement evidence

Suite names below refer to files under `tests/IlRepl.Tests`, except the browser suite in
`tests/IlRepl.Docs.Tests`. Each suite asserts concrete values, metadata, observations, or state transitions.

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
| Browser generic argument capture and constructor execution | `LiveSessionTests.DirectGeneric.cs` |
| Constraint-only dependencies and invalid generic closures | `MethodEditConstraintClosureTests` |
| Pinned originals, dependent rebuilds, atomic failed commits | `MethodEditRebuildTests`, `EditPreviewTests` |
| Independent saved images and Microsoft ILAsm execution | `MethodEditExportTests`, metadata and accessibility suites |
| Normalized instructions, branch targets, raw bytes and independent stacks | `MethodEditDiffTests`, `MethodEditAlignmentTests` |
| Typed literal limits and unchanged inputs after errors | `NumericArgumentTests` |
| Returns, console output, exceptions, ref/out, mutable receivers and async | `MethodComparisonTests` |
| Object graphs, cycles, scalar bits, aliases and unavailable values | `MethodComparisonObservationTests`, `ScalarAliasComparisonTests` |
| Managed storage aliases and byref return identity | `MethodComparisonAliasTests` |
| Static state, culture, environment, stdin and filesystem isolation | `MethodComparisonIsolationTests`, `ComparisonFixtureTests` |
| Timeout, crash, excessive output, cancellation and parent survival | `MethodComparisonIsolationTests`, browser method-editing suite |
| Corrected framework copies compared with actual original execution | `ExternalOriginalScenarioTests`, `MethodComparisonObservationTests` |
| Source documents, dependency provenance, one-use tickets, stale replies, batch exits | `MethodEditingProcessTests` |
| Draft completion, exact replacement ranges, preview and recovery | `EditCompletionTests`, `EditPreviewTests`, `EditCommandTests` |
| Keyboard editing, full source history, diff and comparison | `MethodEditingTerminalTests`, `LiveSessionTests.MethodEditing.cs` |
| Published guide and sample transcript execute exactly through RPC | `EditDocumentationTests` |

`[TestMethod]` cases use the actual Microsoft ILAsm and ILVerification oracles where applicable. The
`no.` prefix tests preserve its bytes and explicitly assert the real CLR refusal. Browser native-interop
restrictions and unavailable structural observations remain visible outcomes rather than silent matches.

The replayable sample is [`editing-methods.il`](../samples/Transcripts/editing-methods.il). Its comparison
outcomes are `different` and then `match`; its cell results are exactly `42`, `41`, and `41`.

## Reproduce validation

Run from the repository root with the SDK selected by `global.json` and the browser workload available:

```sh
ILREPL_REQUIRE_ILASM=1 dotnet test --project tests/IlRepl.Tests/IlRepl.Tests.csproj
dotnet scripts/Highlight-Cil.cs --verify
dotnet scripts/Publish-Wasm.cs --configuration Debug
pnpm --dir docs build
dotnet test --project tests/IlRepl.Docs.Tests/IlRepl.Docs.Tests.csproj
dotnet scripts/Publish-NativeAot.cs --rid linux-x64
```

Validation explicitly selected Microsoft ILAsm and ILDAsm 10.0.11. Set `ILREPL_ILASM` and `ILREPL_ILDASM`
to those executables if `PATH` selects another implementation. `ILREPL_REQUIRE_ILASM=1` makes missing
assembler support fail the relevant tests. The docs suite requires installed Playwright Chromium and WebKit binaries.
Native AOT packaging includes the repository smoke test; the published executable can also run the
sample transcript directly.

Collect production coverage with the same settings used for this change:

```sh
ILREPL_REQUIRE_ILASM=1 dotnet test --project tests/IlRepl.Tests/IlRepl.Tests.csproj \
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
branch even when process tests execute and assert that branch. Browser WebAssembly execution is checked
by the Playwright suite and is outside the desktop coverage report.
