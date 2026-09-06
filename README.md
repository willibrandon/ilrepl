# ilrepl

An interactive CIL REPL. Type IL one instruction at a time, watch the evaluation stack change
under your fingers, and run the cell on the real JIT.

```
il[1]> ldc.i4 6
  ┊ [int32]
il[1]> ldc.i4 7
  ┊ [int32, int32] ◂ top
il[1]> mul
  ┊ [int32]
il[1]> ret
  = 42 : int32
```

Documentation and a live session in your browser: https://ilrepl.dev/

## Install

```
dotnet tool install -g ilrepl
ilrepl
```

ilrepl needs the .NET 10 SDK or runtime. The front-end is a Native AOT executable on Windows,
Linux, and macOS; the part that compiles and runs IL is a small framework-dependent host that
ships in the package and runs on your `dotnet`.

## What it does

Every line you type is checked against a typed model of the evaluation stack before it is
accepted, so underflows and arity mistakes show up at the prompt. `ret`, or an empty line,
compiles the cell with Reflection.Emit, runs it, and prints whatever single value was left on the
stack with its runtime type.

It takes all of IL: every opcode, exception blocks (`.try {`, `} catch T {`, `} filter {`,
`} finally {`, `} fault {`), `calli`, varargs and `arglist`, pinned locals, cell arguments,
generic parameters with `!!T`, and `ldtoken` for types, methods, and fields. Member references use
ILAsm syntax, and the return type and `[assembly]` prefix are optional. Cells can be saved to disk
as real assemblies with `.save`, or shown as ILAsm with `.il`.

```
il[2]> .locals init (string m)
il[2]> .try {
il[2]> ldstr "boom"
il[2]> newobj instance void InvalidOperationException::.ctor(string)
il[2]> throw
il[2]> } catch InvalidOperationException {
  ┊ [InvalidOperationException]
il[2]> callvirt instance string Exception::get_Message()
il[2]> stloc m
il[2]> leave DONE
il[2]> }
il[2]> DONE: ldloc m
il[2]> ret
  = "boom" : string
```

Tab completes opcodes and commands, with a palette that shows each candidate's stack transition.
Up and Down walk history. `.help` lists the commands and `.ops` lists the opcodes.

## Batch mode

```
ilrepl -e 'ldc.i4 6; ldc.i4 7; mul; ret'
ilrepl samples/Transcripts/exceptions.il
printf 'ldstr "piped"\nret\n' | ilrepl --no-color
```

## How it works

Reflection.Emit needs a JIT, and the Native AOT front-end has none, so ilrepl is two processes:
the front-end owns the terminal UI, the transcript, and completion; the host owns the session and
answers over JSON-RPC on its standard streams. The same engine runs in the browser on the docs
site, where the .NET runtime is compiled to WebAssembly.

## Building

Requires the .NET 10 SDK. The browser build also needs the `wasm-tools` workload, and the docs
need Node 22 with pnpm.

```
dotnet build
dotnet test
dotnet run --project src/IlRepl
```

`dotnet build` publishes the host into `host/` beside the front-end. The tests cover the engine
in-process on the real JIT, the sample library in `samples/Greeter`, every transcript in
`samples/Transcripts`, the terminal UI on a headless terminal emulator, the front-end as a process
in a PTY, and the docs site's live session in a real browser.

Repository utilities are file-based apps under `scripts/`:

```
dotnet run --file scripts/Generate-OpcodeReference.cs
dotnet run --file scripts/Publish-Wasm.cs
dotnet run --file scripts/Publish-NativeAot.cs -- --rid osx-arm64 --package-version 0.1.0
```

## License

MIT
