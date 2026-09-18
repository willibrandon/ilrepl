# Scripts

Repository utilities are .NET file-based apps. They need the .NET 10 SDK and follow the
file-based app guidance at https://learn.microsoft.com/dotnet/core/sdk/file-based-apps.

Run one with `dotnet run --file`:

```
dotnet run --file scripts/Generate-BootstrapCatalog.cs
dotnet run --file scripts/Generate-BootstrapCatalog.cs -- --check
dotnet run --file scripts/Generate-OpcodeReference.cs
dotnet run --file scripts/Publish-Wasm.cs
dotnet run --file scripts/Publish-NativeAot.cs -- --rid osx-arm64 --package-version 0.5.0
```

| App | Purpose |
| --- | --- |
| `Generate-BootstrapCatalog.cs` | Generates the startup command catalog and IL vocabulary; `--check` detects stale generated output. |
| `Highlight-Cil.cs` | Writes the terminal colours used by the site; `--verify` checks README and docs transcripts locally. |
| `Generate-OpcodeReference.cs` | Writes `docs/src/content/docs/reference/opcodes.md` from the engine's opcode table. |
| `Publish-Wasm.cs` | Publishes the browser build and copies it into `docs/public/try` without the pre-compressed variants. |
| `Publish-NativeAot.cs` | Publishes and checks a Native AOT frontend, then packs and checks its runtime-specific tool package. |

The bootstrap catalog lets the terminal offer editing and command help before the execution host connects.
After changing the engine's command catalog or IL vocabulary, run `Generate-BootstrapCatalog.cs` from inside the repository.
Review and include the updated `src/IlRepl.Protocol/BootstrapCatalog.Generated.cs` with the change.
`--check` compares that file with the engine's current tables without writing it and returns a nonzero exit code if it is stale.

Native AOT publishing writes to `artifacts/native-aot/<rid>/publish` by default; `--output` changes the base directory.
The framework-dependent execution host contains ReadyToRun code for the requested RID. Its build cache separates target RIDs
and portable builds. Publishing checks the host dependency target, then the host, engine, and protocol PE headers and ReadyToRun signatures.
The default command checks both the published frontend and the executable extracted from its tool package, including
interruption, restart, session saving, and process ownership. Validation requires a matching OS, architecture, and libc.
Run musl checks inside Alpine with the .NET runtime available for the execution host.
Musl publishing also compiles the pinned terminal helper against musl. Use the matching Alpine SDK and architecture with
`build-base` installed, including for `--build-only`; the same helper is included in the tool package.
`--build-only` publishes and inspects the host images without executing checks or creating a package.
`--smoke-only` checks an existing publish directory without republishing the frontend or packing; the checks can build
their test driver. These options cannot be combined.
The default command writes packages under `artifacts/native-aot/<rid>/packages`. Successful checks write `smoke-results.json`
beside the publish directory, recording the validated RID, ReadyToRun machine, and frontend, host, and package hashes.
Smoke-only results contain no package entries. Extracted packages undergo the same host image and behavior checks.
Linux evidence also records the terminal helper hash and requires the packaged helper to match the published copy.
Windows smoke process waits require the kernel termination signal before deleting runtime-only fixtures.
Cleanup failures retain the error, file attributes, and observable process paths still using the fixture.
CI also supplies Microsoft's [Handle](https://learn.microsoft.com/sysinternals/downloads/handle) through `ILREPL_SMOKE_HANDLE_PATH`
to report matching open handles when cleanup fails. This diagnostic never closes handles or changes the validation result.

The opcode reference generator builds the engine itself. The browser publish needs the `wasm-tools` workload.
Its scripts, runtime, and samples share a content-hashed directory. The site reads the generated asset manifest at build time
so a deployment uses one matching set of browser assets, including for visitors with an older runtime cached.
`Publish-Wasm.cs --conformance` includes the test-only entry point for optional local checks described in
[docs/browser-tests/README.md](../docs/browser-tests/README.md). Browser tests are excluded from CI.

After changing an example, run `dotnet run --file scripts/Highlight-Cil.cs -- --update`, review
the changed transcripts, then run it with `--verify` to check them locally. Prompt numbers and
output are checked. A `<!-- replay-setup -->` line immediately before a visible `cil` fence
executes that source to establish the context for the following transcript. Only the absolute
scratch directory used by `.save` is normalized to the documented `/path/to` placeholder.
Editor views show unsent text; complete transcripts run through the terminal's submission
and recovery path. Build `samples/Greeter` before regenerating. The default command generates
colours for the documented text and reports replay differences or failures as warnings. Updates
stop if an input produces an error that its documented response does not contain. Verification
is an explicit local command.

Terminal capture instructions are in [vhs/README.md](../vhs/README.md).
