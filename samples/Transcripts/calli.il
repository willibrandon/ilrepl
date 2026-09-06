// Calling through a function pointer.
ldc.i4 3
ldc.i4 9
ldftn int32 Math::Max(int32, int32)
calli int32(int32, int32)
ret

ldstr "via calli"
ldftn void Console::WriteLine(string)
calli void(string)
ret
