# Scripts

Repository utilities are .NET file-based apps. They need the .NET 10 SDK and follow the
file-based app guidance at https://learn.microsoft.com/dotnet/core/sdk/file-based-apps.

Run one with `dotnet run --file`:

```
dotnet run --file scripts/Generate-OpcodeReference.cs
dotnet run --file scripts/Publish-Wasm.cs
dotnet run --file scripts/Publish-NativeAot.cs -- --rid osx-arm64 --package-version 0.1.0
```

| App | Purpose |
| --- | --- |
| `Generate-OpcodeReference.cs` | Writes `docs/src/content/docs/reference/opcodes.md` from the engine's opcode table. |
| `Publish-Wasm.cs` | Publishes the browser build and copies it into `docs/public/try` without the pre-compressed variants. |
| `Publish-NativeAot.cs` | Publishes the Native AOT front-end for one runtime identifier, smoke-tests it, and packs the runtime-specific tool package. |

The opcode reference generator builds the engine itself. The browser publish needs the `wasm-tools` workload.
