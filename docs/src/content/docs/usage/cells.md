---
title: Cells and the stack
description: What a cell is, how the stack model works, and what ret does.
---

A cell is the method you are writing. Every accepted line becomes part of it until you run it.

## The stack model

Before a line is accepted, its effect on the evaluation stack is simulated. The model is linear:
it follows the lines in the order you typed them, not the branches. That is enough to catch
underflows, wrong arities, and most typos, and it is what the echo after each line shows.

```ilrepl
il[1]> newobj instance void StringBuilder::.ctor()
  ┊ [StringBuilder]
il[1]> ldstr "il"
  ┊ [StringBuilder, string] ◂ top
il[1]> callvirt instance StringBuilder StringBuilder::Append(string)
  ┊ [StringBuilder]
```

Types come from the operand where they are known: locals, fields, method returns, `newobj`,
`box`, `newarr`, `castclass`, and the numeric suffixes of `ldind`, `ldelem`, and `conv`. Arithmetic
follows the runtime's widening rules. A `?` means the type could not be inferred.

## Running

`ret` on its own, or an empty line, runs the cell. The stack must hold zero or one value at that
point. One value is boxed and printed with its runtime type; zero values prints `(void)`.

`ret` is emitted inside the cell instead when it cannot be the end: while a forward branch is
waiting for its label, or inside a protected region. Inside a `.method` block it returns from the
method. That is how early returns and `switch` tables work.

```ilrepl
il[2]> ldloc x
il[2]> switch (A, B)
il[2]> ldstr "default"
il[2]> ret
  ┊ ret inside the cell (a forward label or a block is still open)
il[2]> A: ldstr "a"
il[2]> ret
il[2]> B: ldstr "b"
il[2]> ret
  = "b" : string
```

After a run the cell body is cleared and the next cell starts. Declarations (`.locals`, `.args`,
`.typeparams`, `.vararg`) stay, and so do methods defined with `.method`, so the next cell can use
the same locals and call the same methods. Closing a `.method` block also starts a new cell,
without running anything. `.clear` drops the body without running it and `.reset` drops the
declarations and methods as well.

## Output

Anything the cell writes to the console is captured and shown as output lines above the result.
A read from standard input returns end-of-input rather than blocking.

## What the runtime catches

The model cannot see control flow, so a stack that differs between two paths into the same label
is only found when the cell is compiled. The message names the JIT, and `.show` lists the cell with
the stack after each instruction:

```ilrepl
il[3]> .show
  000  ldc.i4 0                                 [int32]
  001  brfalse SKIP                             []
  002  ldc.i4 1                                 [int32]
SKIP:
  003  pop                                      []
```
