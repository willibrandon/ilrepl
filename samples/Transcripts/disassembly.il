// Reading compiled bodies back with .dis: a session method, a filter, a class member, a calli, and the framework.
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
.dis int32 Fib(int32)

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
.dis Safe

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
.dis instance int32 Point::Twice()

.method int32 Indirect() {
ldc.i4 3
ldc.i4 9
ldftn int32 Math::Max(int32, int32)
calli int32(int32, int32)
ret
}
.dis Indirect

.dis instance string String::Trim()
.dis instance void class List`1<int32>::Add(!0)

ldc.i4 0
call int32 Safe(int32)
ldc.i4 10
call int32 Fib(int32)
add
ret
