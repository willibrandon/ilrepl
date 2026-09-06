// A counting loop with a local and a backward branch, then a switch table.
.locals init (int32 i)
ldc.i4.0
stloc i
LOOP: ldloc i
ldc.i4.1
add
dup
stloc i
ldc.i4 10
blt LOOP
ldloc i
ret

.locals init (int32 x)
ldc.i4 2
stloc x
ldloc x
switch (A, B, C)
ldstr "default"
ret
A: ldstr "a"
ret
B: ldstr "b"
ret
C: ldc.i4 99
ret
