# AGENTS.md

These instructions apply to the entire repository.

## Product

- ilrepl is a terminal application. Treat terminal behavior as the primary product behavior.
- The WebAssembly project provides the live documentation demo. Do not redesign terminal features around browser assumptions.
- Follow established .NET conventions and choose the behavior most .NET developers and ilrepl users would expect.
- Do not reduce requested scope or leave known failures for later.

## Code

- Keep one type per file.
- Keep C# lines at 140 characters or fewer. In other files, keep changed lines within the same limit.
- Give every body braces, with each brace on its own line. This includes single statements, `try`, `catch`, `finally`, `else`, and `lock`.
- Braces that hold no statements, as in `{ get; set; }`, an initializer, a pattern, or a `switch` expression, may share one line.
  Once they take more than one line, lay them out like a body.
- Directly after a closing brace, only closing punctuation or a property's initializer may follow on its line, as in `});`,
  `}, token).ConfigureAwait(false);`, or `} = value;`. The statement may finish there, but no second statement may start.
- Put the `while` that ends a `do`, or a member access written straight after the brace as in `}.ToList()`, on the next line.
- Leave a blank line after a line that begins with a closing brace, before the next statement, comment, member, accessor, or `case`.
- When a parameter list does not fit on one line, put every parameter on its own line.
- Prefer `using` directives and short type names over repeated fully qualified `System.*` names.
- The build enforces these rules through `.editorconfig` and the analyzers in `src/IlRepl.SourceGen`. Fix what they report.
- Do not suppress a rule.
- Add triple slash XML documentation to every changed public or internal type and member.
- Write every `<summary>` as exactly three physical lines: the opening tag, one text line, and the closing tag.
- Add no dependency unless Microsoft or the .NET Foundation owns it. Prefer a small local implementation when practical.
- When adding or changing a file-based app script, update its directory's README in the same change.

## Tests and CI

- Never use `DoNotParallelize`, including the `DoNotParallelizeAttribute` form.
- Fix shared state, isolation, and synchronization so every test can run safely in parallel.
- A plain `dotnet test` must work without manually configured environment variables.
- Keep real coverage. Do not delete tests to improve runtime, and do not use mocks.
- Use condition based synchronization instead of fixed sleeps.
- Run the complete suite on each configured CI target. Do not shard the suite.
- Do not suggest or add macOS x64 jobs to CI.
- Do not add browser tests to any CI workflow, including separate workflows or packaging gates.
- Keep browser tests optional and local. Do not make terminal validation wait for them.
- Do not add browser or documentation site tests to the regular test suite.
- Do not publish test counts in documentation or pull request descriptions.

## Documentation

- Write public documentation for ilrepl users in concise, natural language.
- Keep implementation and test commands out of the public documentation site.
- Avoid em dashes, canned summaries, excessive formatting, and repetitive explanation.

## Git and GitHub

- Keep pull request descriptions as short prose without headings, lists, manual line wrapping, or em dashes.
