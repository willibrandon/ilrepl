---
title: Types
description: Declare a class in one cell and use it from the next.
---

`.class` opens a block the way `.method` does. Inside it, `.field` declares fields and
`.method` declares members; `}` closes each member and then the class. The type is written
with the metadata you declared and loaded once, so later cells make instances of it, read its
statics, and call its members, and a value of the type is shown by its fields.

```
il[1]> .class public sequential ansi sealed Point extends [System.Runtime]System.ValueType {
  struct Point
il[1]> .field public int32 X
  field public int32 X
il[1]> .field public int32 Y
  field public int32 Y
il[1]> .method public instance void .ctor(int32 x, int32 y) {
  method instance void .ctor(int32, int32)
il[1]> ldarg.0
  ┊ [Point&]
il[1]> ldarg x
  ┊ [Point&, int32] ◂ top
il[1]> stfld int32 Point::X
  ┊ []
il[1]> ldarg.0
il[1]> ldarg y
il[1]> stfld int32 Point::Y
il[1]> ret
il[1]> }
  end of method .ctor
il[1]> .method public instance int32 Sum() {
  method instance int32 Sum()
il[1]> ldarg.0
  ┊ [Point&]
il[1]> ldfld int32 Point::X
  ┊ [int32]
il[1]> ldarg.0
il[1]> ldfld int32 Point::Y
  ┊ [int32, int32] ◂ top
il[1]> add
il[1]> ret
il[1]> }
  end of method Sum
il[1]> }
  end of struct Point
il[2]> ldc.i4 3
il[2]> ldc.i4 4
il[2]> newobj instance void Point::.ctor(int32, int32)
  ┊ [Point]
il[2]> box Point
il[2]> ret
  = Point { X = 3, Y = 4 } : Point
```

The header takes the words ILAsm takes: `public` or `private`, `abstract`, `sealed`,
`interface`, `sequential` or `explicit` layout, `ansi`, `beforefieldinit`, and the rest. A type
that `extends System.ValueType` is a struct, one that `extends System.Enum` is an enum, and
`interface` needs no base. Inside a member, `ldarg.0` is `this`, a reference to the struct or
the object, and the stack echo names it. Members follow ILAsm too: a method is an instance
method unless it says `static`, and a member with no access word is `privatescope`, which only
its own class can reach, so write `public` when a cell needs it.

Nothing runs when the class closes. The family is written, loaded, and every body is prepared
on the JIT, so a body the runtime would refuse is reported at `}` and the block stays open.
The note says `end of struct Point` and the cell number advances: a class completes the cell
the way a method does.

## One type, many cells

The type is one runtime type for the whole session. A static keeps its value from cell to cell,
an instance made in one cell is the same object in the next, and a type initializer runs once,
when the type is first touched.

```
il[3]> .locals init (valuetype Point p)
il[3]> ldloca p
il[3]> ldc.i4 5
il[3]> ldc.i4 6
il[3]> call instance void Point::.ctor(int32, int32)
il[3]> ldloca p
il[3]> call instance int32 Point::Sum()
  ┊ [int32]
il[3]> ret
  = 11 : int32
```

A value of a session type is displayed by its fields, base fields first, private ones included.
The display reads the fields directly and runs none of the type's code, unless the type
overrides `ToString`, in which case that override is what you see. Nested values are shown to
three levels, a cycle is marked with `↺`, and a long display is cut.

## Interfaces and dispatch

An interface member is implemented by a `virtual` method of the same name and signature, or by
a method of any name that says which slot it fills with `.override`. Abstract members, `newslot`,
default interface bodies, and `static abstract` members all work as they do in ILAsm, and the
close checks that every slot is filled.

```
il[4]> .class interface public abstract IArea {
  interface IArea
il[4]> .method public abstract virtual instance int32 Area() { }
  method instance int32 Area(); end of method Area
il[4]> }
  end of interface IArea
il[4]> .class public Square implements IArea {
  class Square
il[4]> .field public int32 Side
  field public int32 Side
il[4]> .method public instance void .ctor(int32 side) {
  method instance void .ctor(int32)
il[4]> ldarg.0
il[4]> call instance void Object::.ctor()
il[4]> ldarg.0
il[4]> ldarg side
il[4]> stfld int32 Square::Side
il[4]> ret
il[4]> }
  end of method .ctor
il[4]> .method public virtual instance int32 Area() {
  method instance int32 Area()
il[4]> ldarg.0
il[4]> ldfld int32 Square::Side
il[4]> dup
il[4]> mul
il[4]> ret
il[4]> }
  end of method Area
il[4]> }
  end of class Square
il[5]> ldc.i4 7
il[5]> newobj instance void Square::.ctor(int32)
il[5]> callvirt instance int32 IArea::Area()
il[5]> ret
  = 49 : int32
```

A class without a constructor has none: `newobj` on it is refused with a hint, and a struct is
made with `initobj` or a local instead. A `.cctor` is the type initializer, `initonly` fields
can only be stored from the constructors of their own type, and `literal` fields are constants
with no storage, so `ldsfld` on one is refused with the value to load instead.

## Enums, generics, and nested types

An enum declares its `value__` field and `literal` members. A generic type takes its parameters
on the header, `!0` inside its members, and its type arguments from the cell that instantiates
it; each closed type has its own statics. A nested type is declared inside its enclosing block
and named by its path, `Outer/Inner`, and a nested generic type redeclares the enclosing
parameters first, as ECMA-335 has it.

```
il[6]> .class public Box`1<T> {
  class Box`1<T>
il[6]> .field public !0 Value
  field public !T Value
il[6]> .method public instance void .ctor(!0 v) {
  method instance void .ctor(!T)
il[6]> ldarg.0
  ┊ [Box<!T>]
il[6]> call instance void Object::.ctor()
il[6]> ldarg.0
il[6]> ldarg v
il[6]> stfld !0 class Box`1<!0>::Value
il[6]> ret
il[6]> }
  end of method .ctor
il[6]> }
  end of class Box`1
il[7]> ldstr "boxed"
il[7]> newobj instance void class Box`1<string>::.ctor(!0)
  ┊ [Box<string>]
il[7]> ldfld !0 class Box`1<string>::Value
il[7]> ret
  = "boxed" : string
```

Layout words work: `.pack` and `.size` shape a sequential struct, `[N]` before a field's type
gives its offset in an explicit one, and `sizeof` reports the result. `.property` and `.event`
blocks name their accessors with `.get`, `.set`, `.addon`, and `.removeon`. `.custom` attaches
an attribute, in the blob form ildasm writes or the typed form `= { string('text') }`, to the
class, or to the field written just before it, or inside a method to the method or, after
`.param [N]`, to a parameter.

## Access

The session is one assembly, so `assembly` members are open to every cell and class. `family`
members need a derived class, `private` ones the declaring class or a type nested in it, and a
member with no access word is `privatescope`, reachable only from its own class. Nested types
follow the same words. The REPL checks each of these where you type the line, because a cell is
allowed to skip the runtime's own checks for session types; the rules are ECMA-335's, with one
difference that the runtime itself makes: a derived class may use a `family` member through
any receiver, not only through its own type.

```
il[8]> .class public Base {
  class Base
il[8]> .field private int32 Secret
  field private int32 Secret
il[8]> }
  end of class Base
il[9]> ldsfld int32 Base::Secret
  error: int32 Base::Secret is private; only Base and the types nested in it can use it, not the cell
```

## Listing and saving

`.types` lists every type with its members. `.show` inside a class lists the header, the fields,
and the open method. `.il` renders each class before the cell type, and `.save` writes them into
the assembly, so the file carries exactly the metadata you declared.

```
il[9]> .types
  struct Point
      public int32 X
      public int32 Y
      instance void .ctor(int32, int32)
      instance int32 Sum()
  interface IArea
      instance int32 Area()
  class Square
      public int32 Side
      instance void .ctor(int32)
      instance int32 Area()
```

## Closing, undoing, and redefining

`.undo` takes back the last line of the class, and taking back the header abandons it. `.clear`
inside a class abandons the class and leaves the cell alone. `.reset` drops every type along
with the methods; instances you still hold keep working with the old type.

Declaring a class again with the same name replaces it when the block closes. The new class is a
new type: existing instances keep the previous definition, and a static starts over. Anything
that mentions the class, another class, a session method, or the cell, is rebuilt against the
new definition, in the order it was accepted, and the note lists what was rebuilt. If one of
them no longer compiles, the redefinition is refused with that name and nothing changes:
redefine the dependent first, or `.reset`.

```
il[10]> }
  replaced class Point; rebuilt method Make and class Line (existing instances and delegates keep the previous definitions)
```
