# Scripts

Repository utilities are .NET file-based apps. They need the .NET 10 SDK and follow the
file-based app guidance at https://learn.microsoft.com/dotnet/core/sdk/file-based-apps.

Run one with `dotnet run --file`:

```
dotnet run --file scripts/Generate-OpcodeReference.cs
dotnet run --file scripts/Record-Demo.cs
dotnet run --file scripts/Record-Demo.cs -- --output docs/public/demo
```

| App | Purpose |
| --- | --- |
| `Generate-OpcodeReference.cs` | Writes `docs/src/content/docs/reference/opcodes.md` from the engine's opcode table. |
| `Record-Demo.cs` | Runs the built front-end in a Hex1b PTY, drives a short session, and writes an asciinema recording and an SVG snapshot for the docs. |

Both apps expect a Debug build of the solution (`dotnet build`) to exist.
