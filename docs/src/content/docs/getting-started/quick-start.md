---
title: Quick start
description: A first session, one instruction at a time.
---

Start `ilrepl` and type an instruction. The line under it is the simulated evaluation stack,
bottom to top.

```ilrepl
il[1]> ldc.i4 6
  ┊ [int32]
il[1]> ldc.i4 7
  ┊ [int32, int32] ◂ top
il[1]> mul
  ┊ [int32]
il[1]> ret
  = 42 : int32
```

`ret`, or an empty line, compiles everything you typed since the last run into a method, runs it,
and prints whatever single value was left on the stack. An empty stack means the cell was void.

[![Typing IL, watching the stack, and completing a string method in ilrepl](/quick-start.gif)](/quick-start.gif)

## Calls

Member references use ILAsm syntax, with two conveniences: the return type and the `[assembly]`
prefix are optional, and short type names resolve through the common `System` namespaces.

```ilrepl
il[2]> ldstr "hello"
  ┊ [string]
il[2]> callvirt instance int32 String::get_Length()
  ┊ [int32]
il[2]> ret
  = 5 : int32
```

When a name is ambiguous the error lists the overloads so you can pick one.

## Locals and loops

Locals are declared with `.locals` and persist across cells. Their values do not; each run starts
fresh.

```ilrepl
il[3]> .locals init (int32 i)
  locals: 0:int32 i
il[3]> ldc.i4.0
  ┊ [int32]
il[3]> stloc i
  ┊ []
il[3]> LOOP: ldloc i
  ┊ [int32]
il[3]> ldc.i4.1
  ┊ [int32, int32] ◂ top
il[3]> add
  ┊ [int32]
il[3]> dup
  ┊ [int32, int32] ◂ top
il[3]> stloc i
  ┊ [int32]
il[3]> ldc.i4 10
  ┊ [int32, int32] ◂ top
il[3]> blt LOOP
  ┊ []
il[3]> ldloc i
  ┊ [int32]
il[3]> ret
  = 10 : int32
```

A label is a name followed by a colon, on its own line or before an instruction. A branch to a
label that has not been defined yet is fine; the cell will not run until it is.

Methods persist the same way: `.method int32 Twice(int32 n) {` opens one, `}` closes it, and later
cells call it with `call int32 Twice(int32)`. Enter continues the block while its braces are open
and sends it, line by line, once they balance; a pasted block waits for Enter the same way. See
[Methods](/usage/methods/) and [Editing blocks](/usage/editing/).

## Mistakes

The stack model catches the common ones before the runtime sees them.

```ilrepl
il[4]> add
  error: stack underflow: 'add' pops 2 values but the stack has 0: []
il[4]> lcd.i4 1
  error: unknown opcode 'lcd.i4' (did you mean 'ldc.i4'?)
```

Anything the model cannot catch, such as a stack that differs between two branches into the same
label, is reported when the JIT rejects the cell. `.show` lists the cell with the stack after each
instruction, which is usually enough to find it.

## Getting around

Tab completes opcodes, commands, types, and members, with a palette that shows each candidate's stack effect.
Up and Down choose palette rows; with the palette closed, they reach history from the first and
last editor lines. History keeps a block as one entry and lasts between runs. `.help` prints
the full command list, `.ops` lists opcodes, and Ctrl+Q leaves.
