call void [System.Console]System.Console::WriteLine(string)
// <- opcode
//   ^^^^ type
//        ^^^^^^^^^^^^^^^^ dim
//                        ^^^^^^^^^^^^^^ type
//                                      ^^ punctuation
//                                        ^^^^^^^^^ member
//                                                 ^ punctuation
//                                                  ^^^^^^ type
//                                                        ^ punctuation

call void Console::WriteLine(string)
// <- opcode
//   ^^^^ type
//        ^^^^^^^ type
//               ^^ punctuation
//                 ^^^^^^^^^ member
//                          ^ punctuation
//                           ^^^^^^ type
//                                 ^ punctuation

callvirt instance int32 class [System.Runtime]System.Collections.Generic.List`1<!0>::get_Count()
// <- opcode
//       ^^^^^^^^ keyword
//                ^^^^^ type
//                      ^^^^^ keyword
//                            ^^^^^^^^^^^^^^^^ dim
//                                            ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ type
//                                                                             ^ punctuation
//                                                                              ^^ type
//                                                                                ^ punctuation
//                                                                                 ^^ punctuation
//                                                                                   ^^^^^^^^^ member
//                                                                                            ^ punctuation
//                                                                                             ^ punctuation

call instance void class [System.Runtime]System.Collections.Generic.List`1<!0>::AddWithResize(!0)
// <- opcode
//   ^^^^^^^^ keyword
//            ^^^^ type
//                 ^^^^^ keyword
//                       ^^^^^^^^^^^^^^^^ dim
//                                       ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ type
//                                                                        ^ punctuation
//                                                                         ^^ type
//                                                                           ^ punctuation
//                                                                            ^^ punctuation
//                                                                              ^^^^^^^^^^^^^ member
//                                                                                           ^ punctuation
//                                                                                            ^^ type
//                                                                                              ^ punctuation

call !!0 [System.Runtime]System.Math::Max<int32>(!!0, !!0)
// <- opcode
//   ^^^ type
//       ^^^^^^^^^^^^^^^^ dim
//                       ^^^^^^^^^^^ type
//                                  ^^ punctuation
//                                    ^^^ member
//                                       ^ punctuation
//                                        ^^^^^ type
//                                             ^ punctuation
//                                              ^ punctuation
//                                               ^^^ type
//                                                  ^ punctuation
//                                                    ^^^ type
//                                                       ^ punctuation

call vararg int32 Hello::Count(..., int32)
// <- opcode
//   ^^^^^^ keyword
//          ^^^^^ type
//                ^^^^^ type
//                     ^^ punctuation
//                       ^^^^^ member
//                            ^ punctuation
//                             ^^^ punctuation
//                                ^ punctuation
//                                  ^^^^^ type
//                                       ^ punctuation

call int32 IlRepl.Cell::'add'(int32)
// <- opcode
//   ^^^^^ type
//         ^^^^^^^^^^^ type
//                    ^^ punctuation
//                      ^^^^^ member
//                           ^ punctuation
//                            ^^^^^ type
//                                 ^ punctuation

ldftn void [Fixtures]N.Fixture/'<>c'::'<Main>b__0_0'()
// <- opcode
//    ^^^^ type
//         ^^^^^^^^^^ dim
//                   ^^^^^^^^^^^^^^^ type
//                                  ^^ punctuation
//                                    ^^^^^^^^^^^^^^ member
//                                                  ^ punctuation
//                                                   ^ punctuation

newobj instance void Point::.ctor(int32, int32)
// <- opcode
//     ^^^^^^^^ keyword
//              ^^^^ type
//                   ^^^^^ type
//                        ^^ punctuation
//                          ^^^^^ member
//                               ^ punctuation
//                                ^^^^^ type
//                                     ^ punctuation
//                                       ^^^^^ type
//                                            ^ punctuation

call void Outer/Inner::Go()
// <- opcode
//   ^^^^ type
//        ^^^^^^^^^^^ type
//                   ^^ punctuation
//                     ^^ member
//                       ^ punctuation
//                        ^ punctuation

ldsfld string [System.Runtime]System.String::Empty
// <- opcode
//     ^^^^^^ type
//            ^^^^^^^^^^^^^^^^ dim
//                            ^^^^^^^^^^^^^ type
//                                         ^^ punctuation
//                                           ^^^^^ member

stfld int32 valuetype Point::X
// <- opcode
//    ^^^^^ type
//          ^^^^^^^^^ keyword
//                    ^^^^^ type
//                         ^^ punctuation
//                           ^ member

call instance void Point::'.ctor'(int32)
// <- opcode
//   ^^^^^^^^ keyword
//            ^^^^ type
//                 ^^^^^ type
//                      ^^ punctuation
//                        ^^^^^^^ member

call int32 [X]N.T::Method<class [System.Runtime]System.Object>(!!0)
// <- opcode
//   ^^^^^ type
//         ^^^ dim
//            ^^^ type
//               ^^ punctuation
//                 ^^^^^^ member
//                       ^ punctuation
//                        ^^^^^ keyword
//                              ^^^^^^^^^^^^^^^^ dim
//                                              ^^^^^^^^^^^^^ type
//                                                           ^ punctuation
//                                                            ^ punctuation
//                                                             ^^^ type
//                                                                ^ punctuation
