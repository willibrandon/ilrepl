# ilrepl design

ilrepl is an interactive CIL REPL. You type IL one instruction at a time, the simulated
evaluation stack is shown after each line, and `ret` compiles the cell with Reflection.Emit and
runs it on the real JIT. This document is the plan of record and the progress tracker.

## Goals

- Accept any IL that `System.Reflection.Emit` can express: every opcode, exception blocks,
  `calli`, varargs and `arglist`, pinned locals, cell arguments, generic parameters, and
  metadata tokens.
- Show the stack after every instruction and catch stack mistakes before the JIT sees them.
- Ship as a `dotnet tool` published Native AOT, like dotsider.
- Run the terminal UI on Hex1b and test it through the Hex1b emulator, with no mocks.
- Keep the code conventional for .NET developers: one type per file, XML docs on every public
  member, central package management, MSTest on Microsoft.Testing.Platform, file-based apps for
  repository utilities.

## Architecture

Reflection.Emit needs a JIT, and a Native AOT process has none. The tool is therefore two
processes, the same shape dotsider uses for its trace host:

```
ilrepl (Native AOT)                          ilrepl-host (framework-dependent)
┌──────────────────────────────┐   stdio     ┌──────────────────────────────┐
│ System.CommandLine           │  JSON lines │ IlRepl.Engine                │
│ Hex1b terminal UI            │ ──────────▶ │   TypeParser, MemberResolver │
│ batch mode (-e, script, pipe)│ ◀────────── │   CellState, CellCompiler    │
│ HostProcessEngine            │             │   Session, ReplCore          │
└──────────────────────────────┘             └──────────────────────────────┘
```

The front-end owns the transcript, completion, and rendering. The host owns the session and
produces transcript lines for each handled input. The host is published into a `host`
directory beside the tool and started with the `dotnet` muxer, resolved from
`DOTNET_HOST_PATH` and then `PATH`. The two talk JSON-RPC with StreamJsonRpc over the host's
standard streams, using System.Text.Json source generation on both sides.

The docs site runs the same engine in the browser. `IlRepl.Wasm` is a `Microsoft.NET.Sdk.WebAssembly`
app: a Web Worker boots the .NET runtime, an in-process engine wraps `ReplCore`, and a
presentation adapter bridges the Hex1b terminal to xterm.js on the page, following Hex1b's
WasmDemo sample. Reflection.Emit runs on the Mono interpreter, so cells compile there too.
Nothing talks to a server, which is what a GitHub Pages site can host.

### Projects

| Project | Kind | Purpose |
| --- | --- | --- |
| `src/IlRepl.Protocol` | library, AOT-safe | Transcript model, completion catalog, request and response records, JSON source generation. |
| `src/IlRepl.Engine` | library | IL parsing, member resolution, stack simulation, compilation, the session, and `ReplCore`. |
| `src/IlRepl.Host` | console app | Serves `ReplCore` over stdin/stdout. Published into `host/` next to the tool. |
| `src/IlRepl.Tui` | library, AOT-safe | The Hex1b widgets: transcript, prompt with completion palette, status bar. |
| `src/IlRepl` | tool, Native AOT | CLI, batch mode, host process client. |
| `src/IlRepl.Wasm` | browser app | The engine and the UI on the .NET WebAssembly runtime, for the docs site. |
| `samples/Greeter` | library | A real assembly the tests load and call into. |
| `samples/Transcripts` | IL scripts | Real sessions run by tests and shown in the docs. |
| `tests/IlRepl.Tests` | MSTest | Engine tests in-process, UI tests on the Hex1b emulator, end-to-end tests over a PTY. |
| `tests/IlRepl.Docs.Tests` | MSTest, Playwright | Serves the built site and drives the live session in Chromium. |
| `scripts/` | file-based apps | Demo recorder and opcode reference generator. |
| `docs/` | Astro Starlight | Public site on GitHub Pages. |

### Protocol

`IReplHost` is a JSON-RPC contract with two methods. `HelloAsync` returns the opcode and
command catalog with the initial status, so completion runs locally in the front-end.
`HandleAsync` takes one line and returns the transcript lines it produced, whether it
succeeded, whether the user asked to quit, and the session status. Cell console output is
captured inside the host through async-local routing of the console streams and returned as
transcript lines, so the channel is never mixed with what a cell prints.

### The cell model

A cell is a static method being written. Declarations (`.locals`, `.args`, `.typeparams`,
`.vararg`) persist across cells; instructions, labels, and blocks belong to the current cell.
Every line is validated on arrival against a replayable `CellState`, and `ret` compiles the
whole cell by replaying the accepted lines against a fresh `MethodBuilder`, so generic
parameters bind to the method that is emitted. `ret` inside a cell (after a forward branch
or inside a block) is emitted; `ret` at the top level runs.

Compiled cells are `AssemblyBuilder` types with a single static `Run` method that returns
`object`. Vararg cells get a standard-convention `Invoke` wrapper because reflection cannot
call a vararg method. `.save` writes the same cell with `PersistedAssemblyBuilder`.

### IL support

| Feature | How |
| --- | --- |
| All opcodes in `OpCodes` | Operand parsing by `OperandType`; `no.` has no emit API and is reported. |
| Locals, pinned locals, `[N]` prefixes | `.locals init (...)` |
| Arguments | `.args (T name = literal)`; values passed on `Invoke`. |
| Exception blocks | `.try {`, `} catch T {`, `} filter {`, `} handler {`, `} finally {`, `} fault {`, `}` |
| `calli` | Managed and unmanaged signatures via `EmitCalli`. |
| Varargs | `vararg` and `...` in member references, `.vararg` cells, `arglist`. |
| Generics | `!N`, `!!N`, `!Name`, generic instantiations, generic methods, `.typeparams` and `.typeargs`. |
| Tokens | `ldtoken` for types, `method`, and `field`. |
| Types | Primitives, `[assembly]`, nested, arrays of any rank, byref, pointer, `modreq`/`modopt`, function pointers. |

### Terminal UI

Hex1b widgets only: a following `VScrollPanel` of transcript lines, a composite prompt widget
with a completion palette and inline prediction, an `InfoBar` with the live stack. Global
bindings: Ctrl+Q quits, Ctrl+L clears.

### Testing

- Engine and REPL tests run in-process against the real JIT.
- Sample tests build `samples/Greeter` with `dotnet build` and load the real assembly.
- Transcript tests run every file in `samples/Transcripts` through the batch runner.
- UI tests run the real app on `Hex1bTerminal` headless with the input sequence builder and
  the automator, including a real host process behind the UI.
- End-to-end tests run the built front-end as a process, in batch mode over pipes and in a PTY
  through the Hex1b emulator.
- Docs tests serve the built site from Kestrel and drive the live session in Chromium with
  Playwright.

## Progress

- [x] Engine: type parser, member resolver, literals, opcode table
- [x] Engine: instruction parser, stack simulator, replayable cell state
- [x] Engine: compiler on `AssemblyBuilder`, vararg wrapper, persisted save, ILAsm renderer
- [x] Engine: session, `ReplCore`, help, value formatting, completion catalog
- [x] Protocol library: transcript model, JSON-RPC contract, JSON source generation
- [x] Host process on StreamJsonRpc
- [x] Front-end: host client, batch mode, System.CommandLine entry point
- [x] Front-end: Hex1b UI (prompt widget with palette and prediction, transcript, status bar)
- [x] Native AOT tool with the host bundled beside it; smoke-tested and packed per runtime
- [x] Samples: Greeter library and ten transcripts
- [x] Tests: engine, samples, transcripts, UI on the emulator, end-to-end over pipes and a PTY
- [x] Browser build and a Playwright test of the live session
- [x] Scripts: demo recorder, opcode reference generator, browser publish, Native AOT publish
- [x] Docs site on Astro Starlight with the live session
- [x] CI, docs deploy, release workflows
- [x] README
- [ ] First release tag and nuget.org trusted publishing (needs the nuget.org side configured)

## Decisions

- Two processes rather than one. A Native AOT binary cannot host Reflection.Emit, and
  full IL support is a goal. dotsider's trace host is the precedent.
- `AssemblyBuilder` rather than `DynamicMethod`. Dynamic methods cannot be vararg, cannot
  declare generic parameters, and cannot be persisted.
- Lines are re-parsed at compile time rather than cached as builders. Generic parameter
  builders belong to one method, and a cell compiles more than once (`ret`, `.save`).
- The label form of `.try` is not offered. `ILGenerator` only exposes structured blocks, and
  the block form expresses the same programs.
- Function pointer types in locals and parameters are represented as `native int`.
  Reflection has no public API to construct a function pointer `Type`; the evaluation stack
  treats them the same way.
- StreamJsonRpc rather than a hand-rolled line protocol. It is the standard JSON-RPC library
  for .NET, it supports Native AOT with source-generated proxies, and it carries errors and
  cancellation for free.
- The browser build keeps the framework whole. Cells resolve members by name, which trimming
  cannot follow, so the engine and the framework assemblies cells reach for are rooted and only
  unreferenced assemblies are dropped. The compressed variants are not shipped because GitHub
  Pages serves files as they are.
- The vararg calling convention is refused outside Windows with a message that says so. That
  is a runtime limitation, not a parser one; the tests that cover it run on Windows.
- `endfilter` is accepted from the user for fidelity with ILAsm but emitted by `ILGenerator`
  at the filter boundary, which is the only place it can go.
