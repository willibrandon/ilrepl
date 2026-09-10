---
title: Arguments and generics
description: Give the cell parameters and type parameters.
---

## Arguments

`.args` declares parameters and the values passed on every run. `ldarg`, `ldarga`, and `starg`
then work by name or by index.

```ilrepl
il[1]> .args (int32 x = 5, string s = "ab")
  args: 0:int32 x = 5, 1:string s = "ab"
il[1]> ldarg x
  ┊ [int32]
il[1]> ldarg.0
  ┊ [int32, int32] ◂ top
il[1]> mul
  ┊ [int32]
il[1]> ret
  = 25 : int32
```

Literals follow the declared type: integers in decimal, hex, or binary, floats, `true` and
`false`, characters and strings with the usual escapes, enum names, and `null`. A parameter
without a literal gets the default value for its type. Arguments persist across cells like locals.

## Type parameters

`.typeparams` makes the cell a generic method. Its parameters are written `!!T` or `!!0` in any
type position, including locals and member references.

```ilrepl
il[2]> .typeparams (T)
  type parameters: !!T
il[2]> .typeargs (int32)
  type arguments: int32
il[2]> ldc.i4 7
  ┊ [int32]
il[2]> box int32
  ┊ [object]
il[2]> unbox.any !!T
  ┊ [!!T]
il[2]> box !!T
  ┊ [object]
il[2]> ret
  = 7 : int32
```

`.typeargs` binds the parameters for the next run and stays bound until you change it. A generic
cell without bound type arguments is refused with a reminder.

Members on types instantiated over a cell parameter resolve through the generic definition, so
``List`1<!!T>`` behaves as you would expect:

```cil
newobj instance void class List`1<!!T>::.ctor()
callvirt instance int32 class List`1<!!T>::get_Count()
```

Declare `.typeparams` before the first instruction of a cell, or `.clear` first. Arguments cannot
use the cell's type parameters because reflection needs concrete values to pass.

## Varargs

`.vararg` gives the cell the vararg calling convention so `arglist` is valid. The runtime only
supports that convention on Windows; on other platforms the cell is refused with a message that
says so.
