// Cell arguments declared with .args and their values.
.args (int32 x = 5, string s = "ab")
ldarg x
ldarg.0
mul
ret

ldarg s
callvirt instance int32 String::get_Length()
ldarg x
add
ret

ldarga s
ldind.ref
ret
