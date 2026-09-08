ldc.i4 42
// <- opcode
//     ^^ number

ldc.i4 -7
// <- opcode
//     ^^ number

ldc.i4 0x2A
// <- opcode
//     ^^^^ number

ldc.i4 0b1010
// <- opcode
//     ^^^^^^ number

ldc.i8 1_000_000
// <- opcode
//     ^^^^^^^^^ number

ldc.r8 3.25
// <- opcode
//     ^^^^ number

ldc.r8 -2.5e10
// <- opcode
//     ^^^^^^^ number

ldc.r4 nan
// <- opcode
//     ^^^ number

ldc.r8 inf
// <- opcode
//     ^^^ number

ldc.r8 float64(0x7ff8000000000000)
// <- opcode
//     ^^^^^^^^^^^^^^^^^^^^^^^^^^^ number

ldc.i4.s 'a'
// <- opcode
//       ^^^ number

ldstr "hello"
// <- opcode
//    ^^^^^^^ string

ldstr "tab\there"
// <- opcode
//    ^^^^^^^^^^^ string

ldstr "quote \" inside"
// <- opcode
//    ^^^^^^^^^^^^^^^^^ string

ldstr ""
// <- opcode
//    ^^ string

ldstr "http://x"
// <- opcode
//    ^^^^^^^^^^ string

ldstr "a /* not a comment */ b"
// <- opcode
//    ^^^^^^^^^^^^^^^^^^^^^^^^^ string

.field public static literal bool Yes = bool(true)
// <- directive
//                           ^^^^ type
//                                ^^^ member
//                                    ^ punctuation
//                                      ^^^^ type
//                                          ^ punctuation
//                                           ^^^^ keyword
//                                               ^ punctuation

.field public static literal string Name = "x"
// <- directive
//                           ^^^^^^ type
//                                  ^^^^ member
//                                       ^ punctuation
//                                         ^^^ string

.field public static literal float64 Pi = float64(3.14)
// <- directive
//                           ^^^^^^^ type
//                                   ^^ member
//                                      ^ punctuation
//                                        ^^^^^^^ type
//                                               ^ punctuation
//                                                ^^^^ number
//                                                    ^ punctuation

.field public static int32[] Table at Data
// <- directive
//     ^^^^^^ keyword
//            ^^^^^^ keyword
//                   ^^^^^ type
//                        ^ punctuation
//                         ^ punctuation
//                           ^^^^^ member
//                                 ^^ keyword
//                                    ^^^^ default

.custom instance void A::.ctor() = ( 01 00 03 6F 6B 00 )
// <- directive
//                               ^ punctuation
//                                 ^ punctuation
//                                   ^^ number
//                                      ^^ number
//                                         ^^ number
//                                            ^^ number
//                                               ^^ number
//                                                  ^^ number
//                                                     ^ punctuation

.custom instance void A::.ctor(uint8[]) = { bytearray (01 02) }
// <- directive
//                                      ^ punctuation
//                                        ^ punctuation
//                                          ^^^^^^^^^ keyword
//                                                    ^ punctuation
//                                                     ^^ number
//                                                        ^^ number
//                                                          ^ punctuation
//                                                            ^ punctuation

.args (object o = null)
// <- directive
//    ^ punctuation
//     ^^^^^^ type
//            ^ default
//              ^ punctuation
//                ^^^^ keyword
//                    ^ punctuation
