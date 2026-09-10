# Scripts

Repository utilities are .NET file-based apps. They need the .NET 10 SDK and follow the
file-based app guidance at https://learn.microsoft.com/dotnet/core/sdk/file-based-apps.

Run one with `dotnet run --file`:

```
dotnet run --file scripts/Generate-OpcodeReference.cs
dotnet run --file scripts/Publish-Wasm.cs
dotnet run --file scripts/Publish-NativeAot.cs -- --rid osx-arm64 --package-version 0.4.0
```

| App | Purpose |
| --- | --- |
| `Highlight-Cil.cs` | Writes the terminal colours used by the site; `--verify` checks README and docs transcripts locally. |
| `Generate-OpcodeReference.cs` | Writes `docs/src/content/docs/reference/opcodes.md` from the engine's opcode table. |
| `Publish-Wasm.cs` | Publishes the browser build and copies it into `docs/public/try` without the pre-compressed variants. |
| `Publish-NativeAot.cs` | Publishes the Native AOT front-end for one runtime identifier, smoke-tests it, and packs the runtime-specific tool package. |

The opcode reference generator builds the engine itself. The browser publish needs the `wasm-tools` workload.

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
