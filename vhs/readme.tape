# Capture the running terminal for the README.
# Build first: dotnet build src/IlRepl/IlRepl.csproj

Require dotnet

Set Shell bash
Set FontSize 14
Set Width 1200
Set Height 600
Set Padding 20
Set Theme "Catppuccin Mocha"
Set WindowBar Colorful
Set TypingSpeed 0ms
Set Framerate 1
Set WaitTimeout 90s

Hide
Type "src/IlRepl/bin/Debug/net10.0/ilrepl --no-history"
Enter
Wait+Screen /il\[1\]>/
Show

Type "ldc.i4 6"
Enter
Wait+Screen /\[int32\]/
Type "ldc.i4 7"
Enter
Wait+Screen /\[int32, int32\]/
Type "mul"
Enter
Type "ret"
Enter
Wait+Screen /= 42 : int32/

Type "ldc.i4 10"
Enter
Type "ldc.i4 20"
Enter
Type "call Math::Ma"
Wait+Screen /Max/
Down 4
Wait+Screen /static int32 Math::Max/
Sleep 2s
Screenshot assets/ilrepl.png
Sleep 4s

Ctrl+q
