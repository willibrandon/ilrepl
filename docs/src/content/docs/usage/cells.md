---
title: Cells and the stack
description: What a cell is, how the stack model works, and what ret does.
---

A cell is the method you are writing. Every accepted line becomes part of it until you run it.

## The stack model

The stack model follows branches, loops, switches, and exception handlers. Where paths meet,
their stack depths and types must agree. A new branch can reveal a problem on an earlier line;
unresolved forward labels stay editable until their definitions arrive. The echo records each
accepted line, while `.show` recomputes the whole body's stack column.

```ilrepl
il[1]> newobj instance void StringBuilder::.ctor()
  ┊ [StringBuilder]
il[1]> ldstr "il"
  ┊ [StringBuilder, string] ◂ top
il[1]> callvirt instance StringBuilder StringBuilder::Append(string)
  ┊ [StringBuilder]
il[1]> callvirt instance string Object::ToString()
  ┊ [string]
il[1]> ret
  = "il" : string
```

Types come from the operand where they are known: locals, fields, method returns, `newobj`,
`box`, `newarr`, `castclass`, and the numeric suffixes of `ldind`, `ldelem`, and `conv`. Arithmetic
uses the CLI's operand-type rules. A `?` means the type could not be inferred.

## Running

`ret` on its own, or an empty line, runs the cell. The stack must hold zero or one value at that
point. One value is boxed and printed with its runtime type; zero values prints `(void)`.

`ret` is emitted inside the cell instead when it cannot be the end: while a forward branch is
waiting for its label. Inside a `.method` block it returns from the method. In a protected region,
use `leave` to reach a return outside the region. That is how early returns and `switch` tables work.

```ilrepl
il[2]> .locals init (int32 x)
  locals: 0:int32 x
il[2]> ldc.i4.1
  ┊ [int32]
il[2]> stloc x
  ┊ []
il[2]> ldloc x
  ┊ [int32]
il[2]> switch (A, B)
  ┊ []
il[2]> ldstr "default"
  ┊ [string]
il[2]> ret
  ret inside the cell (a forward label or a block is still open)
il[2]> A: ldstr "a"
  ┊ [string]
il[2]> ret
  ret inside the cell (a forward label or a block is still open)
il[2]> B: ldstr "b"
  ┊ [string]
il[2]> ret
  = "b" : string
```

After a run the cell body is cleared and the next cell starts. Declarations (`.locals`, `.args`,
`.typeparams`, `.vararg`) stay, and so do methods defined with `.method`, so the next cell can use
the same locals and call the same methods. Closing a `.method` block also starts a new cell,
without running anything. `.clear` drops the body without running it and `.reset` drops the
declarations, methods, and types as well. Types and their static state survive `.clear`.

## Output

Anything the cell writes to the console is captured and shown as output lines above the result.
A read from standard input returns end-of-input rather than blocking.

## When paths disagree

This block brings an empty stack and an `int32` to `SKIP`:

```cil
.method void Bad(int32 n) {
  ldarg n
  brfalse SKIP
  ldc.i4 1
SKIP: pop
  ret
}
```

The diagnostic names both incoming paths. `SKIP: pop` is refused when submitted, and the terminal
returns the block for correction. The same check runs in the browser, before calling the method.

A bracketed stack is known, `unreachable` means no established path reaches the line, and `?`
means analysis lacks information. `invalid` follows a definite stack error. An incomplete target
or operand is shown separately. Correct but unverifiable operations, such as `localloc`, remain
available and are identified as unverifiable. The runtime still checks rules beyond this stack model.
