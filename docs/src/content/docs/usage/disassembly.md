---
title: Disassembly
description: Read the body of any method back, with the stack after each instruction.
---

`.dis` takes a member reference, spelled the way `call` takes one, and prints the method's body:
the header, `.maxstack`, the locals, every instruction with its byte offset, a label at each branch
target, exception clauses as the blocks `.show` draws, and the stack after each instruction. It
reads framework methods, methods from assemblies brought in with `.load`, members of a closed
`.class`, and methods defined with `.method`. The last case is the interesting one: it shows the
bytes the REPL compiled for what you typed.

## A method you wrote

Define this method first:

<!-- replay-setup -->
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

```ilrepl
il[2]> .dis int32 Fib(int32)
  .method public hidebysig static int32 Fib(int32 n) cil managed {
  .maxstack 3
  0000 ldarg 0                                  [int32]
  0004 ldc.i4 2                                 [int32, int32]
  0009 blt IL_002e                              []
  000e ldarg 0                                  [int32]
  0012 ldc.i4 1                                 [int32, int32]
  0017 sub                                      [int32]
  0018 call int32 Fib(int32)                    [int32]
  001d ldarg 0                                  [int32, int32]
  0021 ldc.i4 2                                 [int32, int32, int32]
  0026 sub                                      [int32, int32]
  0027 call int32 Fib(int32)                    [int32, int32]
  002c add                                      [int32]
  002d ret                                      []
IL_002e:
  002e ldarg 0                                  [int32]
  0032 ret                                      []
  }
  code size 51 (0x33)
```

A session method can be named bare, `.dis Fib`, or with its signature. The left column is the
byte offset of each instruction, which is what a branch operand refers to: `blt IL_002e` jumps to
the instruction at offset 0x2e, and the label above it says so. Arguments print by slot; their
names are in the header. A call to another session method prints the way you would type it,
`call int32 Fib(int32)`, and the operand of a framework member is fully qualified, the way `.il`
prints it.

## What the emitter added

Define a method with a filter:

<!-- replay-setup -->
```cil
.method int32 Safe(int32 d) {
  .locals init (int32 n)
  .try {
    ldc.i4 1
    ldarg d
    div
    stloc n
    leave END
  } filter {
    isinst DivideByZeroException
    ldnull
    cgt.un
    endfilter
  } handler {
    pop
    ldc.i4 42
    stloc n
    leave END
  }
END: ldloc n
  ret
}
```

```ilrepl
il[3]> .dis Safe
  .method public hidebysig static int32 Safe(int32 d) cil managed {
  .maxstack 2
  .locals init (int32 V_0)
       .try {
  0000   ldc.i4 1                               [int32]
  0005   ldarg 0                                [int32, int32]
  0009   div                                    [int32]
  000a   stloc V_0                              []
  000e   leave IL_0036                          []
  0013   leave IL_0036                          unreachable
       } filter {
  0018   isinst [System.Runtime]System.DivideByZeroException [DivideByZeroException]
  001d   ldnull                                 [DivideByZeroException, null]
  001e   cgt.un                                 [int32]
  0020   endfilter                              []
       } handler {
  0022   pop                                    []
  0023   ldc.i4 42                              [int32]
  0028   stloc V_0                              []
  002c   leave IL_0036                          []
  0031   leave IL_0036                          unreachable
       }
IL_0036:
  0036 ldloc V_0                                [int32]
  003a ret                                      []
  }
  code size 59 (0x3b)
```

This uses the filter pattern from [Exception blocks](/usage/exception-blocks/). The listing shows
two things the typed lines did not. The locals are `init`, because the emitter always asks for
zeroed locals. And each `leave END` is followed by a second `leave` to the same place: the emitter
closes every try and handler with a `leave` of its own, whether or not one was typed, and the
column marks the copy `unreachable`. A filter's block opens at the filter code, and its handler
where the handler starts.

## A member of a class

<!-- replay-setup -->
```cil
.class public Point {
  .field public int32 X
  .method public instance int32 Twice() {
    ldarg.0
    ldfld int32 Point::X
    ldc.i4 2
    mul
    ret
  }
}
```

```ilrepl
il[4]> .dis instance int32 Point::Twice()
  .method public instance int32 Twice() cil managed {
  .maxstack 2
  0000 ldarg.0                                  [Point]
  0001 ldfld int32 Point::X                     [int32]
  0006 ldc.i4 2                                 [int32, int32]
  000b mul                                      [int32]
  000c ret                                      []
  }
  code size 13 (0xd)
```

A class must be closed before its members can be listed: until then there is no compiled body,
and the lookup declares nothing on your behalf.

## Framework methods

```ilrepl
il[4]> .dis instance string String::Trim()
  .method public hidebysig instance string Trim() cil managed {
  .maxstack 8
  0000 ldarg.0                                  [string]
  0001 call instance int32 string::get_Length() [int32]
  0006 brfalse.s IL_002a                        []
  0008 ldarg.0                                  [string]
  0009 ldfld char string::_firstChar            [char]
  000e call bool char::IsWhiteSpace(char)       [bool]
  0013 brtrue.s IL_002c                         []
  0015 ldarg.0                                  [string]
  0016 dup                                      [string, string]
  0017 callvirt instance int32 string::get_Length() [string, int32]
  001c ldc.i4.1                                 [string, int32, int32]
  001d sub                                      [string, int32]
  001e callvirt instance char string::get_Chars(int32) [char]
  0023 call bool char::IsWhiteSpace(char)       [bool]
  0028 brtrue.s IL_002c                         []
IL_002a:
  002a ldarg.0                                  [string]
  002b ret                                      []
IL_002c:
  002c ldarg.0                                  [string]
  002d ldc.i4.3                                 [string, int32]
  002e call instance string string::TrimWhiteSpaceHelper(valuetype [System.Private.CoreLib]System.Text.TrimType) [string]
  0033 ret                                      []
  }
  code size 52 (0x34)
```

The bytes come from the assembly file on disk, checked against the loaded module's version id,
so a listing never mixes a newer file with an older loaded assembly. In the browser the framework
has no files to read, so a framework body comes through reflection and a note after the listing
says so. Framework bodies differ between runtime versions, and between the desktop and the
browser, so expect the shape to match and the details to move.

## Generic definitions

```ilrepl
il[4]> .dis instance void class List`1<int32>::Add(!0)
  .method public hidebysig newslot virtual final instance void Add(!T item) cil managed aggressiveinlining {
  .maxstack 3
  .locals (!T[] V_0, int32 V_1)
  0000 ldarg.0                                  [List<!T>]
  0001 ldarg.0                                  [List<!T>, List<!T>]
  0002 ldfld int32 class [System.Collections]System.Collections.Generic.List`1<!0>::_version [List<!T>, int32]
  0007 ldc.i4.1                                 [List<!T>, int32, int32]
  0008 add                                      [List<!T>, int32]
  0009 stfld int32 class [System.Collections]System.Collections.Generic.List`1<!0>::_version []
  000e ldarg.0                                  [List<!T>]
  000f ldfld !0[] class [System.Collections]System.Collections.Generic.List`1<!0>::_items [!T[]]
  0014 stloc.0                                  []
  0015 ldarg.0                                  [List<!T>]
  0016 ldfld int32 class [System.Collections]System.Collections.Generic.List`1<!0>::_size [int32]
  001b stloc.1                                  []
  001c ldloc.1                                  [int32]
  001d ldloc.0                                  [int32, !T[]]
  001e ldlen                                    [int32, native int]
  001f conv.i4                                  [int32, int32]
  0020 bge.un.s IL_0034                         []
  0022 ldarg.0                                  [List<!T>]
  0023 ldloc.1                                  [List<!T>, int32]
  0024 ldc.i4.1                                 [List<!T>, int32, int32]
  0025 add                                      [List<!T>, int32]
  0026 stfld int32 class [System.Collections]System.Collections.Generic.List`1<!0>::_size []
  002b ldloc.0                                  [!T[]]
  002c ldloc.1                                  [!T[], int32]
  002d ldarg.1                                  [!T[], int32, !T]
  002e stelem !0                                []
  0033 ret                                      []
IL_0034:
  0034 ldarg.0                                  [List<!T>]
  0035 ldarg.1                                  [List<!T>, !T]
  0036 call instance void class [System.Collections]System.Collections.Generic.List`1<!0>::AddWithResize(!0) []
  003b ret                                      []
  }
  code size 60 (0x3c)
  showing the definition instance void List<!T>::Add(!T); the instantiation shares its body
```

An instantiation has no body of its own, so `.dis` lists the definition and says so in a note
after the listing. Member references inside it name generic parameters by position, `!0`, as ildasm
does; the header and the stack column name them, `!T`. This body was compiled with
`SkipLocalsInit`, and the `.locals` line has no `init` to show for it. The list type is spelled with
`[System.Collections]`, the facade that exports it: a reference through `[System.Runtime]` would
assemble and then fail to bind, so every core type is named by the assembly a reference must go
through.

## Loaded assemblies

```ilrepl
il[4]> .load samples/Greeter/bin/Debug/net10.0/Greeter.dll
  loaded Greeter 1.0.0.0 (22 public types)
il[4]> .dis int32 Greeter.Hello::CallCountArgs()
  .method public hidebysig static int32 CallCountArgs() cil managed {
  .maxstack 8
  0000 ldc.i4.s 123                             [int32]
  0002 call vararg int32 [Greeter]Greeter.Hello::CountArgs(..., int32) [int32]
  0007 ret                                      []
  }
  code size 8 (0x8)
```

The image is the one read when the assembly was loaded, so rebuilding the file afterwards changes
nothing until it is loaded again. A vararg call site prints the types after the sentinel that its
own reference carries, which is what the stack column needs to pop the right number of values.

## The stack column

The column is the stack after each instruction, from the same model `.show` uses, run over the
whole body: where two paths meet, the states merge the way the runtime merges them, so a path that
pushed `string` and one that pushed `object` meet as `object`, and a byte and an `int32` meet as
`int32`. Three answers are kept apart. A stack in brackets is known. `?` means the model lost the
stack, because an operand did not resolve or the IL does something it cannot follow, and it stays
lost until a handler starts or a `leave` empties it. `unreachable` marks a line no path reaches.

## What it cannot show

An abstract method, a method implemented by the runtime, and a dynamic method have no IL to list,
and the message says which it is. A member of the class still being written has no compiled body
yet. Exception clauses laid out in a way braces cannot draw stay in ildasm's offset form, printed as
notes after the listing, and their handlers still seed the column. A `no.` prefix prints with its
mask, `no. 1`, and is not accepted as REPL input. Instruction offsets and stack columns are annotations: copy
the IL without them into a `.method` block. Offset-form exception clauses need structured blocks,
and references to private loaded members remain subject to runtime access checks.
