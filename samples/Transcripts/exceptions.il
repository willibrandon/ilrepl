// try, catch, finally, and a rethrown exception caught by an outer handler.
.locals init (string message)
.try {
ldstr "boom"
newobj instance void InvalidOperationException::.ctor(string)
throw
} catch InvalidOperationException {
callvirt instance string Exception::get_Message()
stloc message
leave DONE
} finally {
ldstr "finally ran"
call void Console::WriteLine(string)
}
DONE: ldloc message
ret

.locals init (int32 n)
.try {
ldc.i4 1
ldc.i4 0
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
