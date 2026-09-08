ldc.i4 6
// <- opcode
//     ^ number

ldc.i4.s -1
// <- opcode
//       ^^ number

ldc.i8 0x10
// <- opcode
//     ^^^^ number

ldc.i4 'a'
// <- opcode
//     ^^^ number

ldc.r4 float32(0x7f800001)
// <- opcode
//     ^^^^^^^^^^^^^^^^^^^ number

ldc.r8 -inf
// <- opcode
//     ^^^^ number

ldc.r8 1.5e-3
// <- opcode
//     ^^^^^^ number

ldstr "a\"b"
// <- opcode
//    ^^^^^^ string

ldloc i
// <- opcode
//    ^ default

stloc 'my local'
// <- opcode
//    ^^^^^^^^^^ default

ldarg 0
// <- opcode
//    ^ number

blt LOOP
// <- opcode
//  ^^^^ label

switch (IL_000a, IL_000c)
// <- opcode
//     ^ punctuation
//      ^^^^^^^ label
//             ^ punctuation
//               ^^^^^^^ label
//                      ^ punctuation

call int32 Fib(int32)
// <- opcode
//   ^^^^^ type
//         ^^^ member
//            ^ punctuation
//             ^^^^^ type
//                  ^ punctuation

call int32 [System.Runtime]System.Math::Max(int32, int32)
// <- opcode
//   ^^^^^ type
//         ^^^^^^^^^^^^^^^^ dim
//                         ^^^^^^^^^^^ type
//                                    ^^ punctuation
//                                      ^^^ member
//                                         ^ punctuation
//                                          ^^^^^ type
//                                               ^ punctuation
//                                                 ^^^^^ type
//                                                      ^ punctuation

callvirt instance string Exception::get_Message()
// <- opcode
//       ^^^^^^^^ keyword
//                ^^^^^^ type
//                       ^^^^^^^^^ type
//                                ^^ punctuation
//                                  ^^^^^^^^^^^ member
//                                             ^ punctuation
//                                              ^ punctuation

newobj instance void [System.Runtime]System.Exception::.ctor(string)
// <- opcode
//     ^^^^^^^^ keyword
//              ^^^^ type
//                   ^^^^^^^^^^^^^^^^ dim
//                                   ^^^^^^^^^^^^^^^^ type
//                                                   ^^ punctuation
//                                                     ^^^^^ member
//                                                          ^ punctuation
//                                                           ^^^^^^ type
//                                                                 ^ punctuation

call int32 F<int32>(int32)
// <- opcode
//   ^^^^^ type
//         ^ member
//          ^ punctuation
//           ^^^^^ type
//                ^ punctuation
//                 ^ punctuation
//                  ^^^^^ type
//                       ^ punctuation

ldfld int32 Point::X
// <- opcode
//    ^^^^^ type
//          ^^^^^ type
//               ^^ punctuation
//                 ^ member

ldfld !0[] class [System.Runtime]System.Collections.Generic.List`1<!0>::_items
// <- opcode
//    ^^ type
//      ^ punctuation
//       ^ punctuation
//         ^^^^^ keyword
//               ^^^^^^^^^^^^^^^^ dim
//                               ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ type
//                                                                ^ punctuation
//                                                                 ^^ type
//                                                                   ^ punctuation
//                                                                    ^^ punctuation
//                                                                      ^^^^^^ member

ldtoken method !!0 [X]N.T::Generic<[1]>()
// <- opcode
//      ^^^^^^ keyword
//             ^^^ type
//                 ^^^ dim
//                    ^^^ type
//                       ^^ punctuation
//                         ^^^^^^^ member
//                                ^ punctuation
//                                 ^ punctuation
//                                  ^ number
//                                   ^ punctuation
//                                    ^ punctuation
//                                     ^ punctuation
//                                      ^ punctuation

ldtoken field int32 Point::X
// <- opcode
//      ^^^^^ keyword
//            ^^^^^ type
//                  ^^^^^ type
//                       ^^ punctuation
//                         ^ member

ldtoken int32
// <- opcode
//      ^^^^^ type

box Point
// <- opcode
//  ^^^^^ type

isinst class [System.Runtime]System.DivideByZeroException
// <- opcode
//     ^^^^^ keyword
//           ^^^^^^^^^^^^^^^^ dim
//                           ^^^^^^^^^^^^^^^^^^^^^^^^^^^^ type

castclass [X]'weird.name'
// <- opcode
//        ^^^ dim
//           ^^^^^^^^^^^^ type

stelem !0
// <- opcode
//     ^^ type

calli unmanaged cdecl int32(int32)
// <- opcode
//    ^^^^^^^^^ keyword
//              ^^^^^ keyword
//                    ^^^^^ type
//                         ^ punctuation
//                          ^^^^^ type
//                               ^ punctuation

calli int32(int32, int32)
// <- opcode
//    ^^^^^ type
//         ^ punctuation
//          ^^^^^ type
//               ^ punctuation
//                 ^^^^^ type
//                      ^ punctuation

ldftn int32 Math::Max(int32, int32)
// <- opcode
//    ^^^^^ type
//          ^^^^ type
//              ^^ punctuation
//                ^^^ member
//                   ^ punctuation
//                    ^^^^^ type
//                         ^ punctuation
//                           ^^^^^ type
//                                ^ punctuation

no. 1
// <- opcode
//  ^ number

tail. call int32 Fib(int32)
// <- opcode
//    ^^^^ opcode
//         ^^^^^ type
//               ^^^ member

constrained. [X]T callvirt instance void Object::Dispose()
// <- opcode
//           ^^^ dim
//              ^ type
//                ^^^^^^^^ opcode
//                         ^^^^^^^^ keyword
//                                  ^^^^ type
//                                       ^^^^^^ type
//                                             ^^ punctuation
//                                               ^^^^^^^ member

unaligned. 4 ldind.i4
// <- opcode
//         ^ number
//           ^^^^^^^^ opcode

ret
// <- opcode

nop
// <- opcode

add
// <- opcode

cgt.un
// <- opcode

ldelem int32
// <- opcode
//     ^^^^^ type

ldelem.any int32
// <- error
//         ^^^^^ type

sizeof valuetype [System.Runtime]System.ValueTuple`2<int32, string>
// <- opcode
//     ^^^^^^^^^ keyword
//               ^^^^^^^^^^^^^^^^ dim
//                               ^^^^^^^^^^^^^^^^^^^ type
//                                                  ^ punctuation
//                                                   ^^^^^ type
//                                                        ^ punctuation
//                                                          ^^^^^^ type
//                                                                ^ punctuation
