---
title: Methods
description: Define a method in one cell and call it from the next.
---

`.method` opens a block the way `.try` does, and `}` closes it. The method is compiled once
and kept in the session, so later cells and other methods call it by name. This is the source of a method; copy it, paste it into a session, and press Enter.

```cil
.method int32 Fib(int32 n) {
  ldarg n
  ldc.i4 2
  blt BASE
  ldarg n
  ldc.i4 1
  sub
  call int32 Fib(int32)
  ldarg n
  ldc.i4 2
  sub
  call int32 Fib(int32)
  add
  ret
BASE: ldarg n
  ret
}
```

Enter continues the block while its braces are open and sends it once they balance, one line at
a time, so the transcript reads the same whether the block was typed or pasted. See
[Editing blocks](/usage/editing/).

```ilrepl
il[1]> .method int32 Fib(int32 n) {
  method int32 Fib(int32 n)
il[1]> ldarg n
  ┊ [int32]
il[1]> ldc.i4 2
  ┊ [int32, int32] ◂ top
il[1]> blt BASE
  ┊ []
il[1]> ldarg n
  ┊ [int32]
il[1]> ldc.i4 1
  ┊ [int32, int32] ◂ top
il[1]> sub
  ┊ [int32]
il[1]> call int32 Fib(int32)
  ┊ [int32]
il[1]> ldarg n
  ┊ [int32, int32] ◂ top
il[1]> ldc.i4 2
  ┊ [int32, int32, int32] ◂ top
il[1]> sub
  ┊ [int32, int32] ◂ top
il[1]> call int32 Fib(int32)
  ┊ [int32, int32] ◂ top
il[1]> add
  ┊ [int32]
il[1]> ret
  ┊ []
il[1]> BASE: ldarg n
  ┊ [int32]
il[1]> ret
  ┊ []
il[1]> }
  end of method Fib
il[2]> ldc.i4 10
  ┊ [int32]
il[2]> call int32 Fib(int32)
  ┊ [int32]
il[2]> ret
  = 55 : int32
```

Inside the block, `ldarg` loads the parameters by name, `.locals` declares the method's own
locals, and labels and `.try` blocks work as they do in a cell. `ret` returns from the method and
never runs the cell. The stack echo, `.show`, and the status bar describe the method while it is
open.

Closing the block completes the cell, so the next line starts a new one. Nothing runs. Stack
analysis checks every established path before the method is kept, on desktop and in the browser.
Desktop runtime preparation adds a check for methods it can prepare; remaining runtime checks
happen when called. Anything already typed into the cell stays there.

A call names the method with nothing in front of it: `call int32 Fib(int32)`. The return type is
optional, as it is for any member reference. The method is static, so `ldftn`, `calli`, and
delegates over it work too.

## A void helper

```ilrepl
il[3]> .method void Greet(string name) {
  method void Greet(string name)
il[3]> ldstr "hello, "
  ┊ [string]
il[3]> ldarg name
  ┊ [string, string] ◂ top
il[3]> call string String::Concat(string, string)
  ┊ [string]
il[3]> call void Console::WriteLine(string)
  ┊ []
il[3]> ret
  ┊ []
il[3]> }
  end of method Greet
il[4]> ldstr "methods"
  ┊ [string]
il[4]> call void Greet(string)
  ┊ []
il[4]> ret
hello, methods
  = (void)
```

## Listing them

`.methods` lists what the session has defined. `.show` inside an open block lists the method with
the stack after each instruction, and `.il` renders every method beside `Run`. Once the block is
closed, `.dis Fib` reads the compiled body back, byte offsets and all, so what the emitter produced
can be compared with what was typed. See [Disassembly](/usage/disassembly/).

```ilrepl
il[5]> .methods
  int32 Fib(int32 n)
  void Greet(string name)
```

## Closing the block

`}` closes the method. When the stack holds what the return type needs, one value for `int32` or
nothing for `void`, the final `ret` is implied. A mismatched return is refused at the offending
line. In the terminal editor the submission is withdrawn and the block comes back for correction:

```ilrepl
il[5]> .method int32 Answer() {
  method int32 Answer()
il[5]> ldstr "42"
  ┊ [string]
il[5]> ret
  error: ret needs int32 on the stack but found string
  method Answer abandoned; the block is back in the editor
```

Replace the string load with `ldc.i4 42` and submit the corrected block:

```ilrepl
il[5]> .method int32 Answer() {
  method int32 Answer()
il[5]> ldc.i4 42
  ┊ [int32]
il[5]> }
  end of method Answer
il[6]> call Answer
  ┊ [int32]
il[6]> ret
  = 42 : int32
```

Branches are checked together. A label receiving `[]` on one path and `[int32]` on another
produces a diagnostic naming both paths. A forward target remains incomplete while it is being
written; it must be defined before the method closes. The editor and `.show` use the same analysis,
including changes caused by a later backward branch. See [Cells and the stack](/usage/cells/).

`.undo` takes back the last line of the block, and taking back the header abandons it. `.clear`
inside a block abandons the method and leaves the cell alone. Neither advances the cell number.
`.reset` drops every method along with the declarations. `.save` writes the methods into the
assembly beside `Run`.

## Redefinition

Defining a method again with the same name replaces it when the block closes, and the note says
`replaced method Fib`. The other methods and the cell body are checked against the new signature
when you type the header, so a change that would break a caller is refused before you write the
body; `.clear` the cell or keep the signature. Calls resolve as you type them, so two methods that
call each other take three steps: define the second with a placeholder body, define the first
with its call, then define the second again with its real body.
