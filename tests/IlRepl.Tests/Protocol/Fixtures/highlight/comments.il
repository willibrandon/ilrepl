ldc.i4 1 // one
// <- opcode
//     ^ number
//       ^^^^^^ comment

ldc.i4.1// no space
// <- opcode
//      ^^^^^^^^^^^ comment

ldc.i4.2/* inline */
// <- opcode
//      ^^^^^^^^^^^^ comment

ldc.i4/* between */ 3
// <- opcode
//    ^^^^^^^^^^^^^ comment
//                  ^ number

// a whole line
// <- comment

/* a whole line */
// <- comment

ldstr "// not a comment" // but this is
// <- opcode
//    ^^^^^^^^^^^^^^^^^^ string
//                       ^^^^^^^^^^^^^^ comment

ldarg 'a//b' // c
// <- opcode
//    ^^^^^^ default
//           ^^^^ comment

.method int32 F() { // header
// <- directive
//      ^^^^^ type
//            ^ member
//             ^ punctuation
//              ^ punctuation
//                ^ punctuation
//                  ^^^^^^^^^ comment

add /* open
// <- opcode
//  ^^^^^^^ comment

still open
// <- comment



done */ sub
// <- comment
//      ^^^ opcode

/* } */ ret
// <- comment
//      ^^^ opcode

/*
// <- comment

.reset
// <- comment

*/
// <- comment

nop
// <- opcode
