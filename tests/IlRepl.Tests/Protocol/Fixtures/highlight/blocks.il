.try {
// <- directive
//   ^ punctuation

} catch [System.Runtime]System.Exception {
// <- punctuation
//^^^^^ keyword
//      ^^^^^^^^^^^^^^^^ dim
//                      ^^^^^^^^^^^^^^^^ type
//                                       ^ punctuation

} catch InvalidOperationException {
// <- punctuation
//^^^^^ keyword
//      ^^^^^^^^^^^^^^^^^^^^^^^^^ type
//                                ^ punctuation

} catch class [System.Runtime]System.DivideByZeroException {
// <- punctuation
//^^^^^ keyword
//      ^^^^^ keyword
//            ^^^^^^^^^^^^^^^^ dim
//                            ^^^^^^^^^^^^^^^^^^^^^^^^^^^^ type
//                                                         ^ punctuation

} filter {
// <- punctuation
//^^^^^^ keyword
//       ^ punctuation

} handler {
// <- punctuation
//^^^^^^^ keyword
//        ^ punctuation

} finally {
// <- punctuation
//^^^^^^^ keyword
//        ^ punctuation

} fault {
// <- punctuation
//^^^^^ keyword
//      ^ punctuation

}
// <- punctuation

{
// <- punctuation

  .try {
//^^^^ directive
//     ^ punctuation

    } catch [System.Runtime]System.Exception {
//  ^ punctuation
//    ^^^^^ keyword
//          ^^^^^^^^^^^^^^^^ dim
//                          ^^^^^^^^^^^^^^^^ type
//                                           ^ punctuation

      }
//    ^ punctuation

catch [System.Runtime]System.Exception
// <- keyword
//    ^^^^^^^^^^^^^^^^ dim
//                    ^^^^^^^^^^^^^^^^ type

finally
// <- keyword

filter
// <- keyword

fault
// <- keyword

handler
// <- keyword

.method int32 Twice(int32 n) {
// <- directive
//      ^^^^^ type
//            ^^^^^ member
//                 ^ punctuation
//                  ^^^^^ type
//                        ^ default
//                         ^ punctuation
//                           ^ punctuation

  ldarg n
//^^^^^ opcode
//      ^ default

}
// <- punctuation
