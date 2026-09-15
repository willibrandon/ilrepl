# AGENTS.md

These instructions apply to the entire repository.

## Product

- ilrepl is a terminal application. Treat terminal behavior as the primary product behavior.
- The WebAssembly project provides the live documentation demo. Do not redesign terminal features around browser assumptions.
- Follow established .NET conventions and choose the behavior most .NET developers and ilrepl users would expect.
- Do not reduce requested scope or leave known failures for later.

## Code

- Keep one type per file.
- Keep changed lines at 140 characters or fewer.
- Prefer `using` directives and short type names over repeated fully qualified `System.*` names.
- Add triple slash XML documentation to every changed public or internal type and member.
- Write every `<summary>` as exactly three physical lines: the opening tag, one text line, and the closing tag.
- Add no dependency unless Microsoft or the .NET Foundation owns it. Prefer a small local implementation when practical.

## Tests and CI

- Never use `DoNotParallelize`, including the `DoNotParallelizeAttribute` form.
- Fix shared state, isolation, and synchronization so every test can run safely in parallel.
- A plain `dotnet test` must work without manually configured environment variables.
- Keep real coverage. Do not delete tests to improve runtime, and do not use mocks.
- Use condition based synchronization instead of fixed sleeps.
- Run the complete suite on every supported operating system and architecture. Do not shard the suite.
- Do not add browser or documentation site tests to the regular test suite.
- Do not publish test counts in documentation or pull request descriptions.

## Documentation

- Write public documentation for ilrepl users in concise, natural language.
- Keep implementation and test commands out of the public documentation site.
- Avoid em dashes, canned summaries, excessive formatting, and repetitive explanation.

## Git and GitHub

- Keep pull request descriptions as short prose without headings, lists, manual line wrapping, or em dashes.