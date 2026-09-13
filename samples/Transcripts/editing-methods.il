// Replayable method editing: the original remains callable throughout both revisions.
.method int32 Identity(int32 value) {
ldarg.0
ret
}
.dis Identity
.edit Identity as Incremented {
.method public static int32 Identity(int32 value) cil managed {
ldarg.0
ldc.i4.1
add
ret
}
}
.diff Incremented
// Expected: different, original 41, edited 42.
.compare Incremented (41)
// Expected cell result: 42.
ldc.i4.s 41
call Incremented
ret
// Expected cell result: 41; the original has not changed.
ldc.i4.s 41
call Identity
ret

.edit Incremented {
.method public static int32 Identity(int32 value) cil managed {
ldarg.0
ret
}
}
.diff Incremented
// Expected: match, original 41, revision 2 also 41; assertion succeeds.
.compare Incremented (41) --assert
// Expected cell result: 41.
ldc.i4.s 41
call Incremented
ret
