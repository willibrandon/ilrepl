# Run from the repository root after dotnet build.
Output docs/public/quick-start.gif

Require dotnet

Set Shell bash
Set FontSize 14
Set Width 1200
Set Height 600
Set Padding 20
Set Theme "Catppuccin Mocha"
Set WindowBar Colorful
Set TypingSpeed 100ms
Set Framerate 20
Set WaitTimeout 90s

Hide
Type@0ms "src/IlRepl/bin/Debug/net10.0/ilrepl --no-history"
Enter
Wait+Screen /il\[1\]>/
Show
Sleep 1s

Type "ldc.i4 6"
Sleep 300ms
Enter
Sleep 1s
Type "ldc.i4 7"
Sleep 300ms
Enter
Sleep 1s
Type "mul"
Sleep 300ms
Enter
Sleep 1s
Type "ret"
Sleep 300ms
Enter
Wait+Screen /= 42 : int32/
Sleep 3s

Type 'ldstr "hello"'
Sleep 300ms
Enter
Sleep 1s
Type "callvirt "
Sleep 200ms
Type "String::"
Sleep 200ms
Type "get_Len"
Wait+Screen /get_Length/
Sleep 2s
Tab
Sleep 1s
Enter
Wait+Screen /\[int32\]/
Sleep 1s
Type "ret"
Sleep 300ms
Enter
Wait+Screen /= 5 : int32/
Sleep 3s

Hide
Ctrl+q
