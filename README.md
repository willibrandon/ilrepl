# ilrepl

An interactive CIL REPL. Type IL one instruction at a time, watch the evaluation stack change
under your fingers, and run the cell on the real JIT.

![ilrepl showing stack changes, a result, and Math.Max completion](https://raw.githubusercontent.com/willibrandon/ilrepl/main/assets/ilrepl.png)

Documentation and a live session in your browser: https://ilrepl.dev/

## Install

```sh
dotnet tool install -g ilrepl
ilrepl
```

Installing with `dotnet tool` requires the .NET 10 SDK. Running ilrepl requires the .NET 10 runtime.
The front-end is a Native AOT executable on Windows, Linux, and macOS; the part that compiles and runs IL is a small framework-dependent host that
ships in the package and runs on your `dotnet`.

## What it does

Every line you type is checked against a typed model of the evaluation stack before it is
accepted. The model follows branches and reports incompatible stacks where paths meet; the
editor shows the incoming stack at the caret. `ret`, or an empty line,
compiles the cell with Reflection.Emit, runs it, and prints whatever single value was left on the
stack with its runtime type.

It supports CIL opcodes, exception blocks (`.try {`, `} catch T {`, `} filter {`,
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

```ilrepl
il[1]> .locals init (string m)
  locals: 0:string m
il[1]> .try {
  try
  ┊ []
il[1]> ldstr "boom"
  ┊ [string]
il[1]> newobj instance void InvalidOperationException::.ctor(string)
  ┊ [InvalidOperationException]
il[1]> throw
  ┊ []
il[1]> } catch InvalidOperationException {
  catch InvalidOperationException
  ┊ [InvalidOperationException]
il[1]> callvirt instance string Exception::get_Message()
  ┊ [string]
il[1]> stloc m
  ┊ []
il[1]> leave DONE
  ┊ []
il[1]> }
  end of protected region
  ┊ unreachable
il[1]> DONE: ldloc m
  ┊ [string]
il[1]> ret
  = "boom" : string
```

A method stays for the rest of the session:

```ilrepl
il[2]> .method int32 Fib(int32 n) {
  method int32 Fib(int32 n)
il[2]> ldarg n
  ┊ [int32]
il[2]> ldc.i4 2
  ┊ [int32, int32] ◂ top
il[2]> blt BASE
  ┊ []
il[2]> ldarg n
  ┊ [int32]
il[2]> ldc.i4 1
  ┊ [int32, int32] ◂ top
il[2]> sub
  ┊ [int32]
il[2]> call int32 Fib(int32)
  ┊ [int32]
il[2]> ldarg n
  ┊ [int32, int32] ◂ top
il[2]> ldc.i4 2
  ┊ [int32, int32, int32] ◂ top
il[2]> sub
  ┊ [int32, int32] ◂ top
il[2]> call int32 Fib(int32)
  ┊ [int32, int32] ◂ top
il[2]> add
  ┊ [int32]
il[2]> ret
  ┊ []
il[2]> BASE: ldarg n
  ┊ [int32]
il[2]> ret
  ┊ []
il[2]> }
  end of method Fib
il[3]> ldc.i4 10
  ┊ [int32]
il[3]> call int32 Fib(int32)
  ┊ [int32]
il[3]> ret
  = 55 : int32
```

A type stays too, and a value of it is shown by its fields:

```ilrepl
il[4]> .class public sequential ansi sealed Point extends [System.Runtime]System.ValueType {
  struct Point
il[4]> .field public int32 X
  field public int32 X
il[4]> .field public int32 Y
  field public int32 Y
il[4]> .method public instance void .ctor(int32 x, int32 y) {
  method instance void .ctor(int32, int32)
il[4]> ldarg.0
  ┊ [Point&]
il[4]> ldarg x
  ┊ [Point&, int32] ◂ top
il[4]> stfld int32 Point::X
  ┊ []
il[4]> ldarg.0
  ┊ [Point&]
il[4]> ldarg y
  ┊ [Point&, int32] ◂ top
il[4]> stfld int32 Point::Y
  ┊ []
il[4]> ret
  ┊ []
il[4]> }
  end of method .ctor
il[4]> }
  end of struct Point
il[5]> ldc.i4 3
  ┊ [int32]
il[5]> ldc.i4 4
  ┊ [int32, int32] ◂ top
il[5]> newobj instance void Point::.ctor(int32, int32)
  ┊ [Point]
il[5]> box Point
  ┊ [object]
il[5]> ret
  = Point { X = 3, Y = 4 } : Point
```

Enter continues a block while its braces are open and sends it, line by line, once they balance;
a pasted block waits for Enter, and a line the engine refuses brings the whole block back with
that line selected. Tab completes opcodes, commands, types, and members using the lines already
written in the buffer. The palette shows stack effects and a pane with the complete signature.
With the palette closed, Up and Down reach history from the first and last editor lines.
History keeps a block as one entry in `~/.config/ilrepl/history` between runs. `.help` lists the commands and `.ops` lists the opcodes.

## Batch mode

```sh
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

```sh
dotnet build
dotnet test tests/IlRepl.Tests
dotnet run --project src/IlRepl
```

`dotnet build` publishes the host into `host/` beside the front-end. The tests cover the engine
in-process on the real JIT, the sample library in `samples/Greeter`, every transcript in
`samples/Transcripts`, the terminal UI on a headless terminal emulator, the front-end as a process
in a PTY. To run the docs site's tests against a rebuilt browser runtime:

```sh
dotnet workload install wasm-tools
dotnet run --file scripts/Publish-Wasm.cs
pnpm --dir docs install --frozen-lockfile
pnpm --dir docs build
pwsh tests/IlRepl.Docs.Tests/bin/Debug/net10.0/playwright.ps1 install chromium webkit
dotnet test tests/IlRepl.Docs.Tests
```

The browser tests run headlessly in Chromium and WebKit. Installing their dependencies also
requires PowerShell (`pwsh`); on Linux, use `install --with-deps chromium webkit` when needed.

Repository utilities are file-based apps under `scripts/`:

```sh
dotnet run --file scripts/Generate-OpcodeReference.cs
dotnet run --file scripts/Publish-Wasm.cs
dotnet run --file scripts/Publish-NativeAot.cs -- --rid osx-arm64 --package-version 0.4.1
```

## License

MIT
