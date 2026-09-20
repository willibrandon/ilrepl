# Contributing

Install the .NET 10 SDK that `global.json` names, or a newer 10.0 feature band. Then build and test:

```console
dotnet build
dotnet test
```

The tests use MSTest on Microsoft.Testing.Platform and need no environment variables or other setup.
They start real hosts, terminals, workers, `ilasm`, and `ildasm`, so they take a few minutes.
Tests that cannot run on your platform report themselves as skipped.

## Code

The build enforces the layout rules through `.editorconfig` and the analyzers in `src/IlRepl.SourceGen`,
and warnings are errors, so fix what it reports. Do not suppress a rule. In short: one type per file,
lines of at most 140 characters, braces on their own lines around every body, a blank line after a closing
brace, and triple slash documentation on every public or internal type and member with a three-line
`<summary>`. `AGENTS.md` has the full list.

New dependencies must come from Microsoft or the .NET Foundation. A small local implementation is
preferred when it is practical.

## Tests

Tests exercise the real thing. Mocks are not used, and no test is removed to save time.
Every test must be safe to run in parallel with every other, so isolate shared state and do not use `DoNotParallelize`.
Wait for a condition, never for a fixed time.

## Scripts and documentation

Repository utilities are .NET file-based apps under `scripts/`. When you add or change one, update `scripts/README.md`.

After editing a page under `docs/src/content/docs`, regenerate the recorded positions of its CIL samples:

```console
dotnet build samples/Greeter
dotnet run --file scripts/Highlight-Cil.cs
```

## Pull requests

Describe the change in a few plain sentences. CI runs the complete suite on Linux, Alpine, Windows, and macOS,
on x64 and Arm64 where a runner exists, and publishes the Native AOT frontend for each platform.
CodeQL scans the C# source, and any finding fails its check.
