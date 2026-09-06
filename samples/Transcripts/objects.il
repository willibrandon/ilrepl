// Arrays, generic collections, boxing, and tokens.
ldc.i4 3
newarr int32
dup
ldc.i4 0
ldc.i4 7
stelem.i4
dup
ldc.i4 2
ldc.i4 9
stelem.i4
ret

newobj instance void List<int32>::.ctor()
dup
ldc.i4 5
callvirt List<int32>::Add(int32)
dup
ldc.i4 6
callvirt instance void class [System.Collections]System.Collections.Generic.List`1<int32>::Add(!0)
call int32 Enumerable::Sum(IEnumerable<int32>)
ret

ldc.i4 42
box int32
unbox.any int32
ret

ldtoken int32
call Type::GetTypeFromHandle(RuntimeTypeHandle)
callvirt instance string Type::get_FullName()
ret
