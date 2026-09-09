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
generic parameters with `!!T`, and `ldtoken` for types, methods, and fields. Methods defined with
`.method` persist across cells and are called by name, so recursion, `ldftn` over your own code,
and delegates over it all work. Types defined with `.class` persist too: structs and classes with
constructors, virtual and abstract members, interfaces, enums, layout, generics, and nested types,
each one runtime type across cells, shown by its fields when a cell returns one. Member references
use ILAsm syntax, and the return type and `[assembly]` prefix are optional. Types, methods, and
cells can be saved to disk as real assemblies with `.save`, or shown as ILAsm with `.il`. `.dis`
reads any method back, the framework's or your own, with the same stack column beside each
instruction.

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

A method stays for the rest of the session:

```
il[3]> .method int32 Fib(int32 n) {
  method int32 Fib(int32 n)
il[3]> ldarg n
il[3]> ldc.i4 2
il[3]> blt BASE
il[3]> ldarg n
il[3]> ldc.i4 1
il[3]> sub
il[3]> call int32 Fib(int32)
il[3]> ldarg n
il[3]> ldc.i4 2
il[3]> sub
il[3]> call int32 Fib(int32)
il[3]> add
il[3]> ret
il[3]> BASE: ldarg n
il[3]> ret
il[3]> }
  end of method Fib
il[4]> ldc.i4 10
il[4]> call int32 Fib(int32)
il[4]> ret
  = 55 : int32
```

A type stays too, and a value of it is shown by its fields:

```
il[5]> .class public sequential ansi sealed Point extends [System.Runtime]System.ValueType {
  struct Point
il[5]> .field public int32 X
il[5]> .field public int32 Y
il[5]> .method public instance void .ctor(int32 x, int32 y) {
il[5]> ldarg.0
il[5]> ldarg x
il[5]> stfld int32 Point::X
il[5]> ldarg.0
il[5]> ldarg y
il[5]> stfld int32 Point::Y
il[5]> ret
il[5]> }
  end of method .ctor
il[5]> }
  end of struct Point
il[6]> ldc.i4 3
il[6]> ldc.i4 4
il[6]> newobj instance void Point::.ctor(int32, int32)
il[6]> box Point
il[6]> ret
  = Point { X = 3, Y = 4 } : Point
```

Enter continues a block while its braces are open and sends it, line by line, once they balance;
a pasted block waits for Enter, and a line the engine refuses brings the whole block back with
that line selected. Tab completes opcodes, commands, types, and members using the lines already
written in the buffer. The palette shows stack effects and a pane with the complete signature.
Up and Down walk history, which keeps a block as one entry in
`~/.config/ilrepl/history` between runs. `.help` lists the commands and `.ops` lists the opcodes.

## Batch mode

```
ilrepl -e 'ldc.i4 6; ldc.i4 7; mul; ret'
ilrepl samples/Transcripts/exceptions.il
printf 'ldstr "piped"\nret\n' | ilrepl --no-color
```

## How it works

Reflection.Emit needs a JIT, and the Native AOT front-end has none, so ilrepl is two processes:
the front-end owns the terminal UI, the transcript, and the opcode catalog; the host owns the
session and completes operands from the unsent buffer over JSON-RPC. The same engine runs in the browser on the docs
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
dotnet run --file scripts/Publish-NativeAot.cs -- --rid osx-arm64 --package-version 0.3.0
```

## License

MIT
