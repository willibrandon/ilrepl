// Methods that persist across cells: recursion, a void helper, then calli and a delegate over one of them.
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
ldc.i4 10
call int32 Fib(int32)
ret

.method void Greet(string name) {
ldstr "hello, "
ldarg name
call string String::Concat(string, string)
call void Console::WriteLine(string)
ret
}
ldstr "methods"
call void Greet(string)
ret

ldc.i4 20
ldftn int32 Fib(int32)
calli int32(int32)
ret

ldnull
ldftn int32 Fib(int32)
newobj instance void class Func`2<int32, int32>::.ctor(object, native int)
ldc.i4 15
callvirt instance !1 class Func`2<int32, int32>::Invoke(!0)
ret
