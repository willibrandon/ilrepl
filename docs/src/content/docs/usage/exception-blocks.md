---
title: Exception blocks
description: try, catch, filter, finally, and fault inside a cell.
---

Protected regions use the ILAsm block form. Each boundary is its own line.

```ilrepl
il[1]> .locals init (string m)
il[1]> .try {
il[1]> ldstr "boom"
il[1]> newobj instance void InvalidOperationException::.ctor(string)
il[1]> throw
il[1]> } catch InvalidOperationException {
  ┊ [InvalidOperationException]
il[1]> callvirt instance string Exception::get_Message()
il[1]> stloc m
il[1]> leave DONE
il[1]> } finally {
il[1]> ldstr "finally ran"
il[1]> call void Console::WriteLine(string)
il[1]> }
il[1]> DONE: ldloc m
il[1]> ret
finally ran
  = "boom" : string
```

The boundaries are:

| Line | Meaning |
| --- | --- |
| `.try {` | Opens a protected region. |
| `} catch T {` | A handler for exceptions of type `T`. The exception is on the stack when it starts. |
| `} filter {` | A filter expression. It sees the exception and must end with `endfilter`, leaving an `int32`. |
| `} handler {` | The handler that runs when the filter returned non-zero. |
| `} finally {` | Runs on every path. Must be the last handler. |
| `} fault {` | Runs only on the exception path. Must be the last handler. |
| `}` | Closes the region. |

Leave a region with `leave`, not `ret` or `br`. The stack is empty at every boundary except the
start of a catch, filter, or filter handler, where it holds the exception.

## A filter

```ilrepl
il[2]> .try {
il[2]> ldc.i4 1
il[2]> ldc.i4 0
il[2]> div
il[2]> stloc n
il[2]> leave END
il[2]> } filter {
il[2]> isinst DivideByZeroException
il[2]> ldnull
il[2]> cgt.un
il[2]> endfilter
il[2]> } handler {
il[2]> pop
il[2]> ldc.i4 42
il[2]> stloc n
il[2]> leave END
il[2]> }
il[2]> END: ldloc n
il[2]> ret
  = 42 : int32
```

Regions nest: a `.try {` inside a handler opens an inner region. The label form of `.try` from
ILAsm is not offered because `ILGenerator` only exposes structured blocks; the block form
expresses the same programs.
