LOOP: ldloc i
// <- label
//    ^^^^^ opcode
//          ^ default

L1: L2: add
// <- label
//  ^^^ label
//      ^^^ opcode

IL_0004:
// <- label

BASE: ldarg n
// <- label
//    ^^^^^ opcode
//          ^ default

END: ret
// <- label
//   ^^^ opcode

br.s IL_002a
// <- opcode
//   ^^^^^^^ label

brfalse DONE
// <- opcode
//      ^^^^ label

leave END
// <- opcode
//    ^^^ label

blt.un.s LOOP
// <- opcode
//       ^^^^ label

switch (A, B, C)
// <- opcode
//     ^ punctuation
//      ^ label
//       ^ punctuation
//         ^ label
//          ^ punctuation
//            ^ label
//             ^ punctuation

call void Math::Max()
// <- opcode
//   ^^^^ type
//        ^^^^ type
//            ^^ punctuation
//              ^^^ member

LOOP: call int32 String::get_Length()
// <- label
//    ^^^^ opcode
//         ^^^^^ type
//               ^^^^^^ type
//                     ^^ punctuation
//                       ^^^^^^^^^^ member

LOOP:
// <- label
