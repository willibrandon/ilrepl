.locals init (native int)
// <- directive
//      ^^^^ keyword
//           ^ punctuation
//            ^^^^^^ type
//                   ^^^ type
//                      ^ punctuation

.locals init (native int p)
// <- directive
//      ^^^^ keyword
//           ^ punctuation
//            ^^^^^^ type
//                   ^^^ type
//                       ^ default
//                        ^ punctuation

.locals init (native unsigned int u)
// <- directive
//           ^ punctuation
//            ^^^^^^ type
//                   ^^^^^^^^ type
//                            ^^^ type
//                                ^ default
//                                 ^ punctuation

.locals init (unsigned int8 b)
// <- directive
//           ^ punctuation
//            ^^^^^^^^ type
//                     ^^^^ type
//                          ^ default
//                           ^ punctuation

.locals init (class [System.Runtime]System.Object)
// <- directive
//      ^^^^ keyword
//           ^ punctuation
//            ^^^^^ keyword
//                  ^^^^^^^^^^^^^^^^ dim
//                                  ^^^^^^^^^^^^^ type
//                                               ^ punctuation

.locals init (class [System.Runtime]System.Object o)
// <- directive
//           ^ punctuation
//            ^^^^^ keyword
//                  ^^^^^^^^^^^^^^^^ dim
//                                  ^^^^^^^^^^^^^ type
//                                                ^ default
//                                                 ^ punctuation

.locals init (int32&)
// <- directive
//           ^ punctuation
//            ^^^^^ type
//                 ^ punctuation
//                  ^ punctuation

.locals init (int32& r)
// <- directive
//           ^ punctuation
//            ^^^^^ type
//                 ^ punctuation
//                   ^ default
//                    ^ punctuation

.locals init (int32* p)
// <- directive
//           ^ punctuation
//            ^^^^^ type
//                 ^ punctuation
//                   ^ default
//                    ^ punctuation

.locals init (int32[] a, int32[,] m, int32[0...,0...] b)
// <- directive
//           ^ punctuation
//            ^^^^^ type
//                 ^ punctuation
//                  ^ punctuation
//                    ^ default
//                     ^ punctuation
//                       ^^^^^ type
//                            ^ punctuation
//                             ^ punctuation
//                              ^ punctuation
//                                ^ default
//                                 ^ punctuation
//                                   ^^^^^ type
//                                        ^ punctuation
//                                         ^ number
//                                          ^^^ punctuation
//                                             ^ punctuation
//                                              ^ number
//                                               ^^^ punctuation
//                                                  ^ punctuation
//                                                    ^ default
//                                                     ^ punctuation

.locals init (int32 pinned x)
// <- directive
//           ^ punctuation
//            ^^^^^ type
//                  ^^^^^^ keyword
//                         ^ default
//                          ^ punctuation

.locals init (valuetype [System.Runtime]System.ValueTuple`2<int32, string> t)
// <- directive
//           ^ punctuation
//            ^^^^^^^^^ keyword
//                      ^^^^^^^^^^^^^^^^ dim
//                                      ^^^^^^^^^^^^^^^^^^^ type
//                                                         ^ punctuation
//                                                          ^^^^^ type
//                                                               ^ punctuation
//                                                                 ^^^^^^ type
//                                                                       ^ punctuation
//                                                                         ^ default
//                                                                          ^ punctuation

.locals init (class [System.Runtime]System.Collections.Generic.Dictionary`2<string, class [System.Runtime]System.Collections.Generic.List`1<int32>> map)
// <- directive
//           ^ punctuation
//            ^^^^^ keyword
//                  ^^^^^^^^^^^^^^^^ dim
//                                  ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ type
//                                                                         ^ punctuation
//                                                                          ^^^^^^ type
//                                                                                ^ punctuation
//                                                                                  ^^^^^ keyword
//                                                                                        ^^^^^^^^^^^^^^^^ dim
//                                                                                                        ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ type
//                                                                                                                                         ^ punctuation
//                                                                                                                                          ^^^^^ type
//                                                                                                                                               ^ punctuation
//                                                                                                                                                ^ punctuation
//                                                                                                                                                  ^^^ default
//                                                                                                                                                     ^ punctuation

.locals init (int32 modreq([System.Runtime]System.Runtime.CompilerServices.IsConst) x)
// <- directive
//           ^ punctuation
//            ^^^^^ type
//                  ^^^^^^ keyword
//                        ^ punctuation
//                         ^^^^^^^^^^^^^^^^ dim
//                                         ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ type
//                                                                                ^ punctuation
//                                                                                  ^ default
//                                                                                   ^ punctuation

.locals init (int32 modopt(Marker) y)
// <- directive
//           ^ punctuation
//            ^^^^^ type
//                  ^^^^^^ keyword
//                        ^ punctuation
//                         ^^^^^^ type
//                               ^ punctuation
//                                 ^ default
//                                  ^ punctuation

.locals init (method int32 *(int32) fp)
// <- directive
//           ^ punctuation
//            ^^^^^^ keyword
//                   ^^^^^ type
//                         ^ punctuation
//                          ^ punctuation
//                           ^^^^^ type
//                                ^ punctuation
//                                  ^^ default
//                                    ^ punctuation

.locals init (method unmanaged cdecl int32 *(int32, int32))
// <- directive
//           ^ punctuation
//            ^^^^^^ keyword
//                   ^^^^^^^^^ keyword
//                             ^^^^^ keyword
//                                   ^^^^^ type
//                                         ^ punctuation
//                                          ^ punctuation
//                                           ^^^^^ type
//                                                ^ punctuation
//                                                  ^^^^^ type
//                                                       ^ punctuation
//                                                        ^ punctuation

.locals init (!!T item, !0 other)
// <- directive
//           ^ punctuation
//            ^^^ type
//                ^^^^ default
//                    ^ punctuation
//                      ^^ type
//                         ^^^^^ default
//                              ^ punctuation

.locals init (Outer/Inner nested)
// <- directive
//           ^ punctuation
//            ^^^^^^^^^^^ type
//                        ^^^^^^ default
//                              ^ punctuation

.locals init (int32 handler, int32 filter, int32 value)
// <- directive
//           ^ punctuation
//            ^^^^^ type
//                  ^^^^^^^ default
//                         ^ punctuation
//                           ^^^^^ type
//                                 ^^^^^^ default
//                                       ^ punctuation
//                                         ^^^^^ type
//                                               ^^^^^ default
//                                                    ^ punctuation

.method void F([out] int32& x, [in] int32 y) {
// <- directive
//      ^^^^ type
//           ^ member
//            ^ punctuation
//             ^^^^^ keyword
//                   ^^^^^ type
//                        ^ punctuation
//                          ^ default
//                           ^ punctuation
//                             ^^^^ keyword
//                                  ^^^^^ type
//                                        ^ default
//                                         ^ punctuation
//                                           ^ punctuation

ldobj valuetype Point
// <- opcode
//    ^^^^^^^^^ keyword
//              ^^^^^ type

newarr int32
// <- opcode
//     ^^^^^ type

newarr [System.Runtime]System.String
// <- opcode
//     ^^^^^^^^^^^^^^^^ dim
//                     ^^^^^^^^^^^^^ type

ldelema int32[]
// <- opcode
//      ^^^^^ type
//           ^ punctuation
//            ^ punctuation

ldobj typedref
// <- opcode
//    ^^^^^^^^ type

ldobj object
// <- opcode
//    ^^^^^^ type
