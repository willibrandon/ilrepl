// Integer, float, and overflow arithmetic.
ldc.i4 6
ldc.i4 7
mul
ret

ldc.r8 2.5
ldc.r8 4
mul
conv.i8
ret

ldc.i4 -5
neg
ldc.i4.s 3
shl
ret

ldc.i8 0x7ffffffffffffffe
ldc.i8 1
add.ovf
ret
