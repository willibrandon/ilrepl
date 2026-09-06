// Strings, calls, and a StringBuilder.
ldstr "hello"
callvirt instance string Object::ToString()
ret

ldstr "a\tb\n"
callvirt instance int32 String::get_Length()
ret

newobj instance void StringBuilder::.ctor()
ldstr "il"
callvirt instance StringBuilder StringBuilder::Append(string)
ldstr "repl"
callvirt instance StringBuilder StringBuilder::Append(string)
callvirt instance string Object::ToString()
ret

ldstr "printed from the cell"
call void Console::WriteLine(string)
ret
