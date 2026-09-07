// Types that persist across cells: a struct with a constructor and a method, an interface, a class that implements it, and a generic class.
.class public sequential ansi sealed Point extends [System.Runtime]System.ValueType {
.field public int32 X
.field public int32 Y
.field public static int32 Made
.method public instance void .ctor(int32 x, int32 y) {
ldarg.0
ldarg x
stfld int32 Point::X
ldarg.0
ldarg y
stfld int32 Point::Y
ldsfld int32 Point::Made
ldc.i4 1
add
stsfld int32 Point::Made
ret
}
.method public instance int32 Sum() {
ldarg.0
ldfld int32 Point::X
ldarg.0
ldfld int32 Point::Y
add
ret
}
}
ldc.i4 3
ldc.i4 4
newobj instance void Point::.ctor(int32, int32)
box Point
ret

.locals init (valuetype Point p)
ldloca p
ldc.i4 5
ldc.i4 6
call instance void Point::.ctor(int32, int32)
ldloca p
call instance int32 Point::Sum()
ret

ldsfld int32 Point::Made
ret

.class interface public abstract IArea {
.method public abstract virtual instance int32 Area() { }
}
.class public Square implements IArea {
.field public int32 Side
.method public instance void .ctor(int32 side) {
ldarg.0
call instance void Object::.ctor()
ldarg.0
ldarg side
stfld int32 Square::Side
ret
}
.method public virtual instance int32 Area() {
ldarg.0
ldfld int32 Square::Side
dup
mul
ret
}
}
ldc.i4 7
newobj instance void Square::.ctor(int32)
callvirt instance int32 IArea::Area()
ret

.class public Box`1<T> {
.field public !0 Value
.method public instance void .ctor(!0 v) {
ldarg.0
call instance void Object::.ctor()
ldarg.0
ldarg v
stfld !0 class Box`1<!0>::Value
ret
}
}
ldstr "boxed"
newobj instance void class Box`1<string>::.ctor(!0)
ldfld !0 class Box`1<string>::Value
ret
