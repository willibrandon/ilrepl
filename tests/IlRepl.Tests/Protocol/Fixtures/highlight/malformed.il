lcd.i4 2
// <- error
//     ^ number

ld
// <- error

.locls init (int32 i)
// <- error
//     ^^^^ keyword
//          ^ punctuation
//           ^^^^^ type
//                 ^ default
//                  ^ punctuation

LOOP add
// <- error
//   ^^^ keyword

'quoted name'
// <- error

ldstr "unterminated
// <- opcode
//    ^^^^^^^^^^^^^ string

ldarg 'unterminated
// <- opcode
//    ^^^^^^^^^^^^^ default

add /* never closed
// <- opcode
//  ^^^^^^^^^^^^^^^ comment



still inside */ ldc.i4 1
// <- comment
//              ^^^^^^ opcode
//                     ^ number

add ~ # %
// <- opcode
//  ^ default
//    ^ default
//      ^ default

ldc.i4
// <- opcode

call
// <- opcode

call int32
// <- opcode
//   ^^^^^ type

call int32 Fib(
// <- opcode
//   ^^^^^ type
//         ^^^ member
//            ^ punctuation

.method
// <- directive

.method int32
// <- directive
//      ^^^^^ type

.method int32 F(
// <- directive
//      ^^^^^ type
//            ^ member
//             ^ punctuation

.class
// <- directive

.
// <- error

..
// <- error

42
// <- number

"just a string"
// <- string

} } extra
// <- punctuation
//^ punctuation
//  ^^^^^ default

ret 1
// <- opcode
//  ^ number

add extra words here
// <- opcode
//  ^^^^^ default
//        ^^^^^ default
//              ^^^^ default

ldc.i4 1 2 3
// <- opcode
//     ^ number
//       ^ number
//         ^ number

!
// <- default

[
// <- punctuation

::
// <- punctuation

.?x
// <- error
//^ default
