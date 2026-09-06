// REPL commands in a script.
.ops ldc.i4
ldc.i4 1
ldc.i4 2
.show
.il
.undo
.stack
ret
.time on
ldc.i4 3
ret
.quiet on
ldc.i4 4
ret
