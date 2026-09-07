---
title: Methods
description: Define a method in one cell and call it from the next.
---

`.method` opens a block the way `.try` does, and `}` closes it. The method is emitted onto the
cell's type and stays for the rest of the session, so later cells and other methods call it by
name.

```
il[1]> .method int32 Fib(int32 n) {
  method int32 Fib(int32 n)
il[1]> ldarg n
  ┊ [int32]
il[1]> ldc.i4 2
  ┊ [int32, int32] ◂ top
il[1]> blt BASE
  ┊ []
il[1]> ldarg n
il[1]> ldc.i4 1
il[1]> sub
il[1]> call int32 Fib(int32)
  ┊ [int32]
il[1]> ldarg n
il[1]> ldc.i4 2
il[1]> sub
il[1]> call int32 Fib(int32)
  ┊ [int32, int32] ◂ top
il[1]> add
il[1]> ret
  ┊ []
il[1]> BASE: ldarg n
il[1]> ret
il[1]> }
  end of method Fib
il[2]> ldc.i4 10
il[2]> call int32 Fib(int32)
  ┊ [int32]
il[2]> ret
  = 55 : int32
```

Inside the block, `ldarg` loads the parameters by name, `.locals` declares the method's own
locals, and labels and `.try` blocks work as they do in a cell. `ret` returns from the method and
never runs the cell. The stack echo, `.show`, and the status bar describe the method while it is
open.

Closing the block completes the cell, so the next line starts a new one. Nothing runs: the method
is emitted and prepared on the JIT before it is kept, which is how a branch that leaves the stack
uneven is caught at `}` rather than at the first call. Anything already typed into the cell stays
there. In the browser the runtime cannot prepare a method ahead of a call, so that check waits
for the first call.

A call names the method with nothing in front of it: `call int32 Fib(int32)`. The return type is
optional, as it is for any member reference. The method is static, so `ldftn`, `calli`, and
delegates over it work too.

## A void helper

```
il[3]> .method void Greet(string name) {
  method void Greet(string name)
il[3]> ldstr "hello, "
il[3]> ldarg name
il[3]> call string String::Concat(string, string)
il[3]> call void Console::WriteLine(string)
  ┊ []
il[3]> ret
il[3]> }
  end of method Greet
il[4]> ldstr "methods"
il[4]> call void Greet(string)
il[4]> ret
hello, methods
  = (void)
```

## Listing them

`.methods` lists what the session has defined. `.show` inside an open block lists the method with
the stack after each instruction, and `.il` renders every method beside `Run`. Once the block is
closed, `.dis Fib` reads the compiled body back, byte offsets and all, so what the emitter produced
can be compared with what was typed. See [Disassembly](/usage/disassembly/).

```
il[5]> .methods
  int32 Fib(int32 n)
  void Greet(string name)
```

## Closing the block

`}` closes the method. When the stack holds what the return type needs, one value for `int32` or
nothing for `void`, the `ret` is implied. A `ret` that does not match the declared type is refused
where you typed it, so the block stays open and you can fix the stack:

```
il[5]> .method int32 Answer() {
  method int32 Answer()
il[5]> ldstr "42"
  ┊ [string]
il[5]> ret
  error: ret needs int32 on the stack but found string
il[5]> pop
  ┊ []
il[5]> ldc.i4 42
  ┊ [int32]
il[5]> }
  end of method Answer
il[6]>
```

A close the JIT refuses works the same way: the error names the method, the block stays open, and
`.undo` takes back the lines that need to change.

```
il[6]> }
  error: the JIT rejected method Bad: Common Language Runtime detected an invalid program. (check .show for a stack mismatch between branches; the block is still open)
```

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
