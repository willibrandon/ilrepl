.help
// <- command

.h
// <- command

.?
// <- command

.quit
// <- command

.exit
// <- command

.q
// <- command

.run
// <- command

.ops
// <- command

.ops ldc
// <- command
//   ^^^ default

.show
// <- command

.list
// <- command

.ls
// <- command

.dis Fib
// <- command
//   ^^^ member

.dis int32 Fib(int32)
// <- command
//   ^^^^^ type
//         ^^^ member
//            ^ punctuation
//             ^^^^^ type
//                  ^ punctuation

.dis instance string String::Trim()
// <- command
//   ^^^^^^^^ keyword
//            ^^^^^^ type
//                   ^^^^^^ type
//                         ^^ punctuation
//                           ^^^^ member
//                               ^ punctuation
//                                ^ punctuation

.disassemble instance void class List`1<int32>::Add(!0)
// <- command
//           ^^^^^^^^ keyword
//                    ^^^^ type
//                         ^^^^^ keyword
//                               ^^^^^^ type
//                                     ^ punctuation
//                                      ^^^^^ type
//                                           ^ punctuation
//                                            ^^ punctuation
//                                              ^^^ member
//                                                 ^ punctuation
//                                                  ^^ type
//                                                    ^ punctuation

.undo
// <- command

.u
// <- command

.clear
// <- command

.reset
// <- command

.il
// <- command

.save out/cell.dll
// <- command
//    ^^^^^^^^^^^^ default

.load samples/Greeter.dll
// <- command
//    ^^^^^^^^^^^^^^^^^^^ default

.load System.Runtime
// <- command
//    ^^^^^^^^^^^^^^ default

.assemblies
// <- command

.methods
// <- command

.types
// <- command

.stack
// <- command

.time on
// <- command
//    ^^ default

.quiet off
// <- command
//     ^^^ default
