---
title: How it works
description: Two processes, one protocol, and a replayable cell.
---

Reflection.Emit needs a JIT, and the Native AOT front-end has none. ilrepl is therefore two
processes.

```
ilrepl (Native AOT)                          ilrepl-host (framework-dependent)
┌──────────────────────────────┐   JSON-RPC  ┌──────────────────────────────┐
│ command line                 │  over stdio │ type and member resolution   │
│ Hex1b terminal UI            │ ──────────▶ │ stack simulation             │
│ batch mode                   │ ◀────────── │ Reflection.Emit compilation  │
│ transcript and completion    │             │ the session                  │
└──────────────────────────────┘             └──────────────────────────────┘
```

The front-end owns the transcript, completion, and rendering. The host owns the session and
returns the transcript lines each input produced. They talk JSON-RPC with StreamJsonRpc over the
host's standard streams. Console output from a cell is captured inside the host, so the channel
is never mixed with what the cell prints.

The same engine runs in the browser on the [live session](/ilrepl/try/) page, where the .NET
runtime is compiled to WebAssembly and the Hex1b UI renders into xterm.js. Nothing there talks
to a server.

## The cell

Every line is validated against a replayable model of the cell: declared locals and arguments,
the instructions, the labels, the open exception blocks, and the simulated stack. When the cell
runs, the accepted lines are replayed against a fresh `MethodBuilder`, so generic parameters bind
to the method that is emitted and the same cell can compile more than once, for `ret` and for
`.save`.

Compiled cells are `AssemblyBuilder` types with one static `Run` method returning `object`. The
epilogue boxes a value type left on the stack. Vararg cells get a standard-convention wrapper,
because reflection cannot invoke a vararg method directly.

## Projects

| Project | Purpose |
| --- | --- |
| `IlRepl.Protocol` | The transcript model, the completion catalog, and the JSON-RPC contract. |
| `IlRepl.Engine` | Parsing, resolution, simulation, compilation, the session, and the REPL core. |
| `IlRepl.Host` | Serves the engine over standard streams. |
| `IlRepl.Tui` | The Hex1b widgets: transcript, prompt with completion palette, status bar. |
| `IlRepl` | The tool: command line, host client, batch mode. |
| `IlRepl.Wasm` | The browser build used by the docs. |
