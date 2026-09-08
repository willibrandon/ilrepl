ldarg 'lpvoid'
// <- opcode
//    ^^^^^^^^ default

ldloc 'class'
// <- opcode
//    ^^^^^^^ default

ldarg 'noplatform'
// <- opcode
//    ^^^^^^^^^^^^ default

.method public static int32 'float'(int32 'lpvoid', int32 'wchar') cil managed
// <- directive
//      ^^^^^^ keyword
//             ^^^^^^ keyword
//                    ^^^^^ type
//                          ^^^^^^^ member
//                                 ^ punctuation
//                                  ^^^^^ type
//                                        ^^^^^^^^ default
//                                                ^ punctuation
//                                                  ^^^^^ type
//                                                        ^^^^^^^ default
//                                                               ^ punctuation
//                                                                 ^^^ keyword
//                                                                     ^^^^^^^ keyword

.method public static int32 'brnull'(int32 'refany') cil managed
// <- directive
//                          ^^^^^^^^ member
//                                         ^^^^^^^^ default

call int32 [Fixtures]N.Fixture::'add'(int32)
// <- opcode
//   ^^^^^ type
//         ^^^^^^^^^^ dim
//                   ^^^^^^^^^ type
//                            ^^ punctuation
//                              ^^^^^ member
//                                   ^ punctuation
//                                    ^^^^^ type
//                                         ^ punctuation

newobj instance void class [Fixtures]N.'<>c__DisplayClass1_0`1'<int32>::.ctor()
// <- opcode
//     ^^^^^^^^ keyword
//              ^^^^ type
//                   ^^^^^ keyword
//                         ^^^^^^^^^^ dim
//                                   ^^^^^^^^^^^^^^^^^^^^^^^^^^ type
//                                                             ^ punctuation
//                                                              ^^^^^ type
//                                                                   ^ punctuation
//                                                                    ^^ punctuation
//                                                                      ^^^^^ member
//                                                                           ^ punctuation
//                                                                            ^ punctuation

box [X]'a b'
// <- opcode
//  ^^^ dim
//     ^^^^^ type

ldarg 'a\'b'
// <- opcode
//    ^^^^^^ default

ldarg 'back\\slash'
// <- opcode
//    ^^^^^^^^^^^^^ default

call int32 'My.Type'::'my method'()
// <- opcode
//   ^^^^^ type
//         ^^^^^^^^^ type
//                  ^^ punctuation
//                    ^^^^^^^^^^^ member
//                               ^ punctuation
//                                ^ punctuation

call void [X]N.'T`1'<int32>::M()
// <- opcode
//   ^^^^ type
//        ^^^ dim
//           ^^^^^^^ type
//                  ^ punctuation
//                   ^^^^^ type
//                        ^ punctuation
//                         ^^ punctuation
//                           ^ member

ldarg 'a//b'
// <- opcode
//    ^^^^^^ default
