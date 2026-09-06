// Stack allocation and raw pointer access.
ldc.i4 16
localloc
dup
ldc.i4 9
stind.i4
ldind.i4
ret

.locals init (int32 v, int32& r)
ldc.i4 7
stloc v
ldloca v
stloc r
ldloc r
ldc.i4 8
stind.i4
ldloc v
ret
