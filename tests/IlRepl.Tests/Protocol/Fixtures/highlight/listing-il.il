.assembly extern System.Runtime {}
// <- directive
//        ^^^^^^ keyword
//               ^^^^^^^^^^^^^^ type
//                              ^ punctuation
//                               ^ punctuation

.assembly ilrepl_cell {}
// <- directive
//        ^^^^^^^^^^^ default
//                    ^ punctuation
//                     ^ punctuation

.module ilrepl_cell.dll
// <- directive
//      ^^^^^^^^^^^^^^^ type

.class public abstract sealed auto ansi beforefieldinit IlRepl.Cell extends [System.Runtime]System.Object
// <- directive
//     ^^^^^^ keyword
//            ^^^^^^^^ keyword
//                     ^^^^^^ keyword
//                            ^^^^ keyword
//                                 ^^^^ keyword
//                                      ^^^^^^^^^^^^^^^ keyword
//                                                      ^^^^^^^^^^^ type
//                                                                  ^^^^^^^ keyword
//                                                                          ^^^^^^^^^^^^^^^^ dim
//                                                                                          ^^^^^^^^^^^^^ type

{
// <- punctuation

    .method public static int32 Fib(int32 n) cil managed
//  ^^^^^^^ directive
//          ^^^^^^ keyword
//                 ^^^^^^ keyword
//                        ^^^^^ type
//                              ^^^ member
//                                 ^ punctuation
//                                  ^^^^^ type
//                                        ^ default
//                                         ^ punctuation
//                                           ^^^ keyword
//                                               ^^^^^^^ keyword

    {
//  ^ punctuation

        .maxstack 16
//      ^^^^^^^^^ directive
//                ^^ number

        .locals init (int32 V_0)
//      ^^^^^^^ directive
//              ^^^^ keyword
//                   ^ punctuation
//                    ^^^^^ type
//                          ^^^ default
//                             ^ punctuation

        .try
//      ^^^^ directive

        {
//      ^ punctuation

            ldc.i4.s 6
//          ^^^^^^^^ opcode
//                   ^ number

            leave IL_0010
//          ^^^^^ opcode
//                ^^^^^^^ label

        }
//      ^ punctuation

        catch [System.Runtime]System.Exception
//      ^^^^^ keyword
//            ^^^^^^^^^^^^^^^^ dim
//                            ^^^^^^^^^^^^^^^^ type

        {
//      ^ punctuation

        IL_0010: ret
//      ^^^^^^^^ label
//               ^^^ opcode

    }
//  ^ punctuation

}
// <- punctuation

.entrypoint
// <- directive

.ver 1:0:0:0
// <- directive
//   ^ number
//    ^ punctuation
//     ^ number
//      ^ punctuation
//       ^ number
//        ^ punctuation
//         ^ number

.publickeytoken = (B0 3F 5F 7F 11 D5 0A 3A)
// <- directive
//              ^ punctuation
//                ^ punctuation
//                 ^^ number
//                    ^^ number
//                       ^^ number
//                          ^^ number
//                             ^^ number
//                                ^^ number
//                                   ^^ number
//                                      ^^ number
//                                        ^ punctuation

.namespace N {
// <- directive
//         ^ default
//           ^ punctuation

.method public hidebysig newslot virtual final instance void Add(!T item) cil managed aggressiveinlining {
// <- directive
//      ^^^^^^ keyword
//             ^^^^^^^^^ keyword
//                       ^^^^^^^ keyword
//                               ^^^^^^^ keyword
//                                       ^^^^^ keyword
//                                             ^^^^^^^^ keyword
//                                                      ^^^^ type
//                                                           ^^^ member
//                                                              ^ punctuation
//                                                               ^^ type
//                                                                  ^^^^ default
//                                                                      ^ punctuation
//                                                                        ^^^ keyword
//                                                                            ^^^^^^^ keyword
//                                                                                    ^^^^^^^^^^^^^^^^^^ keyword
//                                                                                                       ^ punctuation

.method public hidebysig static int32 Two() cil managed {
// <- directive
//                                    ^^^ member

  .locals (!T[] V_0, int32 V_1)
//^^^^^^^ directive
//        ^ punctuation
//         ^^ type
//           ^ punctuation
//            ^ punctuation
//              ^^^ default
//                 ^ punctuation
//                   ^^^^^ type
//                         ^^^ default
//                            ^ punctuation

    .field public int32 X
//  ^^^^^^ directive
//         ^^^^^^ keyword
//                ^^^^^ type
//                      ^ member

    .method public instance int32 Twice() { 5 instructions }
//  ^^^^^^^ directive
//          ^^^^^^ keyword
//                 ^^^^^^^^ keyword
//                          ^^^^^ type
//                                ^^^^^ member
//                                     ^ punctuation
//                                      ^ punctuation
//                                        ^ punctuation
//                                          ^ number
//                                            ^^^^^^^^^^^^ default
//                                                         ^ punctuation

    .property Count { get, set }
//  ^^^^^^^^^ directive
//            ^^^^^ type
//                  ^ punctuation
//                    ^^^ default
//                       ^ punctuation
//                         ^^^ default
//                             ^ punctuation

    .class class Inner { ... }
//  ^^^^^^ directive
//         ^^^^^ keyword
//               ^^^^^ type
//                     ^ punctuation
//                       ^^^ punctuation
//                           ^ punctuation
