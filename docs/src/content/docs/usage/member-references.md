---
title: Member references
description: How to write calls, fields, and tokens.
---

The full ILAsm form always works:

```
call void [System.Console]System.Console::WriteLine(string)
callvirt instance string [System.Runtime]System.Object::ToString()
newobj instance void [System.Runtime]System.Text.StringBuilder::.ctor()
ldsfld string [System.Runtime]System.String::Empty
```

Three shortcuts make the prompt friendlier:

- The `[assembly]` prefix is optional. Types are searched across every loaded assembly.
- A short name resolves through the common `System` namespaces, so `Console`, `Math`,
  `StringBuilder`, and `List<int32>` work as written.
- The return type is optional. It is only used to break ties between overloads.

```
call Console::WriteLine(string)
call Math::Max(int32, int32)
callvirt instance int32 List<int32>::get_Count()
```

Overloads are matched by exact parameter types. When several remain the error lists them:

```
il[1]> call Console::WriteLine
  error: ambiguous: Console::WriteLine; give parameter types. candidates:
    void Console::WriteLine()
    void Console::WriteLine(bool)
    ...
```

## Generics

Generic instantiations use ILAsm syntax with or without the arity suffix. Inside a member
reference, `!0` is the declaring type's first type argument and `!!0` is the method's.

```
newobj instance void class [System.Collections]System.Collections.Generic.List`1<int32>::.ctor()
callvirt instance void class List`1<int32>::Add(!0)
call !!0 [System.Linq]System.Linq.Enumerable::First<int32>(class IEnumerable`1<!!0>)
```

## Fields and tokens

```
ldsfld string String::Empty
ldfld int32 Greeter.Counter::Count
ldtoken int32
ldtoken method void Console::WriteLine()
ldtoken field string String::Empty
```

## Function pointers and varargs

```
ldftn int32 Math::Max(int32, int32)
calli int32(int32, int32)
calli unmanaged cdecl int32(int32)
call vararg int32 Greeter.Hello::CountArgs(..., int32, string)
```

The types after `...` are the call site's extra arguments. The runtime only supports the vararg
calling convention on Windows; elsewhere the cell is refused with a message that says so.

## Your own assemblies

`.load` takes a path or an assembly name. After that its types resolve like any other, with or
without the `[assembly]` prefix.

```
il[1]> .load samples/Greeter/bin/Debug/net10.0/Greeter.dll
  loaded Greeter 1.0.0.0 (11 public types)
il[1]> ldstr "IL"
il[1]> call string Greeter.Hello::Say(string)
il[1]> ret
  = "Hello, IL!" : string
```
