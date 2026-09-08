.locals init (int32 V_0, string s)
// <- directive
//      ^^^^ keyword
//           ^ punctuation
//            ^^^^^ type
//                  ^^^ default
//                     ^ punctuation
//                       ^^^^^^ type
//                              ^ default
//                               ^ punctuation

.locals init ([0] int32 'class')
// <- directive
//      ^^^^ keyword
//           ^ punctuation
//            ^ punctuation
//             ^ number
//              ^ punctuation
//                ^^^^^ type
//                      ^^^^^^^ default
//                             ^ punctuation

.locals init (native int)
// <- directive
//      ^^^^ keyword
//           ^ punctuation
//            ^^^^^^ type
//                   ^^^ type
//                      ^ punctuation

.locals (int32)
// <- directive
//      ^ punctuation
//       ^^^^^ type
//            ^ punctuation

.args (int32 n = 0, string s = "x", bool b = true)
// <- directive
//    ^ punctuation
//     ^^^^^ type
//           ^ default
//             ^ punctuation
//               ^ number
//                ^ punctuation
//                  ^^^^^^ type
//                         ^ default
//                           ^ punctuation
//                             ^^^ string
//                                ^ punctuation
//                                  ^^^^ type
//                                       ^ default
//                                         ^ punctuation
//                                           ^^^^ keyword
//                                               ^ punctuation

.typeparams (T, U)
// <- directive
//          ^ punctuation
//           ^ type
//            ^ punctuation
//              ^ type
//               ^ punctuation

.typeargs (int32, string)
// <- directive
//        ^ punctuation
//         ^^^^^ type
//              ^ punctuation
//                ^^^^^^ type
//                      ^ punctuation

.vararg
// <- directive

.try {
// <- directive
//   ^ punctuation

.maxstack 8
// <- directive
//        ^ number

.pack 4
// <- directive
//    ^ number

.size 16
// <- directive
//    ^^ number

.method public hidebysig static int32 Fib(int32 n) cil managed {
// <- directive
//      ^^^^^^ keyword
//             ^^^^^^^^^ keyword
//                       ^^^^^^ keyword
//                              ^^^^^ type
//                                    ^^^ member
//                                       ^ punctuation
//                                        ^^^^^ type
//                                              ^ default
//                                               ^ punctuation
//                                                 ^^^ keyword
//                                                     ^^^^^^^ keyword
//                                                             ^ punctuation

.method int32 F() {
// <- directive
//      ^^^^^ type
//            ^ member
//             ^ punctuation
//              ^ punctuation
//                ^ punctuation

.method public static void 'add'(int32 'value') {
// <- directive
//      ^^^^^^ keyword
//             ^^^^^^ keyword
//                    ^^^^ type
//                         ^^^^^ member
//                              ^ punctuation
//                               ^^^^^ type
//                                     ^^^^^^^ default
//                                            ^ punctuation
//                                              ^ punctuation

.method public static !!T Id<T>(!!T x) {
// <- directive
//      ^^^^^^ keyword
//             ^^^^^^ keyword
//                    ^^^ type
//                        ^^ member
//                          ^ punctuation
//                           ^ type
//                            ^ punctuation
//                             ^ punctuation
//                              ^^^ type
//                                  ^ default
//                                   ^ punctuation
//                                     ^ punctuation

.method public static native int Handle() {
// <- directive
//      ^^^^^^ keyword
//             ^^^^^^ keyword
//                    ^^^^^^ type
//                           ^^^ type
//                               ^^^^^^ member
//                                     ^ punctuation
//                                      ^ punctuation
//                                        ^ punctuation

.method int32 modreq([System.Runtime]System.Runtime.CompilerServices.IsConst) G(int32 x) {
// <- directive
//      ^^^^^ type
//            ^^^^^^ keyword
//                  ^ punctuation
//                   ^^^^^^^^^^^^^^^^ dim
//                                   ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ type
//                                                                          ^ punctuation
//                                                                            ^ member
//                                                                             ^ punctuation
//                                                                              ^^^^^ type
//                                                                                    ^ default
//                                                                                     ^ punctuation
//                                                                                       ^ punctuation

.method public instance int32 Twice() {
// <- directive
//      ^^^^^^ keyword
//             ^^^^^^^^ keyword
//                      ^^^^^ type
//                            ^^^^^ member
//                                 ^ punctuation
//                                  ^ punctuation
//                                    ^ punctuation

.method public abstract virtual instance void M() {
// <- directive
//      ^^^^^^ keyword
//             ^^^^^^^^ keyword
//                      ^^^^^^^ keyword
//                              ^^^^^^^^ keyword
//                                       ^^^^ type
//                                            ^ member
//                                             ^ punctuation
//                                              ^ punctuation
//                                                ^ punctuation

.class public auto ansi Point extends [System.Runtime]System.ValueType {
// <- directive
//     ^^^^^^ keyword
//            ^^^^ keyword
//                 ^^^^ keyword
//                      ^^^^^ type
//                            ^^^^^^^ keyword
//                                    ^^^^^^^^^^^^^^^^ dim
//                                                    ^^^^^^^^^^^^^^^^ type
//                                                                     ^ punctuation

.class public sequential sealed Pair extends [System.Runtime]System.ValueType {
// <- directive
//     ^^^^^^ keyword
//            ^^^^^^^^^^ keyword
//                       ^^^^^^ keyword
//                              ^^^^ type
//                                   ^^^^^^^ keyword
//                                           ^^^^^^^^^^^^^^^^ dim
//                                                           ^^^^^^^^^^^^^^^^ type
//                                                                            ^ punctuation

.class nested public Inner {
// <- directive
//     ^^^^^^ keyword
//            ^^^^^^ keyword
//                   ^^^^^ type
//                         ^ punctuation

.class public Box`1<T> {
// <- directive
//     ^^^^^^ keyword
//            ^^^^^ type
//                 ^ punctuation
//                  ^ type
//                   ^ punctuation
//                     ^ punctuation

.class public interface abstract IShape {
// <- directive
//     ^^^^^^ keyword
//            ^^^^^^^^^ keyword
//                      ^^^^^^^^ keyword
//                               ^^^^^^ type
//                                      ^ punctuation

.class public Circle implements IShape, [System.Runtime]System.IDisposable {
// <- directive
//     ^^^^^^ keyword
//            ^^^^^^ type
//                   ^^^^^^^^^^ keyword
//                              ^^^^^^ type
//                                    ^ punctuation
//                                      ^^^^^^^^^^^^^^^^ dim
//                                                      ^^^^^^^^^^^^^^^^^^ type
//                                                                         ^ punctuation

.field public int32 X
// <- directive
//     ^^^^^^ keyword
//            ^^^^^ type
//                  ^ member

.field public static literal int32 X = int32(5)
// <- directive
//     ^^^^^^ keyword
//            ^^^^^^ keyword
//                   ^^^^^^^ keyword
//                           ^^^^^ type
//                                 ^ member
//                                   ^ punctuation
//                                     ^^^^^ type
//                                          ^ punctuation
//                                           ^ number
//                                            ^ punctuation

.field private class [System.Runtime]System.Collections.Generic.List`1<int32> items
// <- directive
//     ^^^^^^^ keyword
//             ^^^^^ keyword
//                   ^^^^^^^^^^^^^^^^ dim
//                                   ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ type
//                                                                    ^ punctuation
//                                                                     ^^^^^ type
//                                                                          ^ punctuation
//                                                                            ^^^^^ member

.field public int32 'add'
// <- directive
//     ^^^^^^ keyword
//            ^^^^^ type
//                  ^^^^^ member

.property int32 Count()
// <- directive
//        ^^^^^ type
//              ^^^^^ member
//                   ^ punctuation
//                    ^ punctuation

.property instance int32 Item(int32) {
// <- directive
//        ^^^^^^^^ keyword
//                 ^^^^^ type
//                       ^^^^ member
//                           ^ punctuation
//                            ^^^^^ type
//                                 ^ punctuation
//                                   ^ punctuation

.event class [System.Runtime]System.EventHandler Changed {
// <- directive
//     ^^^^^ keyword
//           ^^^^^^^^^^^^^^^^ dim
//                           ^^^^^^^^^^^^^^^^^^^ type
//                                               ^^^^^^^ member
//                                                       ^ punctuation

.get instance int32 Counter::get_Count()
// <- directive
//   ^^^^^^^^ keyword
//            ^^^^^ type
//                  ^^^^^^^ type
//                         ^^ punctuation
//                           ^^^^^^^^^ member
//                                    ^ punctuation
//                                     ^ punctuation

.set instance void Counter::set_Count(int32)
// <- directive
//   ^^^^^^^^ keyword
//            ^^^^ type
//                 ^^^^^^^ type
//                        ^^ punctuation
//                          ^^^^^^^^^ member
//                                   ^ punctuation
//                                    ^^^^^ type
//                                         ^ punctuation

.addon instance void Button::add_Click(class [System.Runtime]System.EventHandler)
// <- directive
//     ^^^^^^^^ keyword
//              ^^^^ type
//                   ^^^^^^ type
//                         ^^ punctuation
//                           ^^^^^^^^^ member
//                                    ^ punctuation
//                                     ^^^^^ keyword
//                                           ^^^^^^^^^^^^^^^^ dim
//                                                           ^^^^^^^^^^^^^^^^^^^ type
//                                                                              ^ punctuation

.removeon instance void Button::remove_Click(class [System.Runtime]System.EventHandler)
// <- directive
//        ^^^^^^^^ keyword
//                 ^^^^ type
//                      ^^^^^^ type
//                            ^^ punctuation
//                              ^^^^^^^^^^^^ member

.fire instance void Button::raise_Click()
// <- directive
//    ^^^^^^^^ keyword
//             ^^^^ type
//                  ^^^^^^ type
//                        ^^ punctuation
//                          ^^^^^^^^^^^ member

.other instance void Button::other_Click()
// <- directive
//     ^^^^^^^^ keyword
//              ^^^^ type
//                   ^^^^^^ type
//                         ^^ punctuation
//                           ^^^^^^^^^^^ member

.override [System.Runtime]System.IDisposable::Dispose
// <- directive
//        ^^^^^^^^^^^^^^^^ dim
//                        ^^^^^^^^^^^^^^^^^^ type
//                                          ^^ punctuation
//                                            ^^^^^^^ member

.override method instance void IShape::Draw() with method instance void Circle::DrawShape()
// <- directive
//        ^^^^^^ keyword
//               ^^^^^^^^ keyword
//                        ^^^^ type
//                             ^^^^^^ type
//                                   ^^ punctuation
//                                     ^^^^ member
//                                         ^ punctuation
//                                          ^ punctuation
//                                            ^^^^ keyword
//                                                 ^^^^^^ keyword
//                                                        ^^^^^^^^ keyword
//                                                                 ^^^^ type
//                                                                      ^^^^^^ type
//                                                                            ^^ punctuation
//                                                                              ^^^^^^^^^ member

.param [1] = int32(3)
// <- directive
//     ^ punctuation
//      ^ number
//       ^ punctuation
//         ^ punctuation
//           ^^^^^ type
//                ^ punctuation
//                 ^ number
//                  ^ punctuation

.custom instance void Attr::.ctor(string) = { string('gone') }
// <- directive
//      ^^^^^^^^ keyword
//               ^^^^ type
//                    ^^^^ type
//                        ^^ punctuation
//                          ^^^^^ member
//                               ^ punctuation
//                                ^^^^^^ type
//                                      ^ punctuation
//                                        ^ punctuation
//                                          ^ punctuation
//                                            ^^^^^^ type
//                                                  ^ punctuation
//                                                   ^^^^^^ string
//                                                         ^ punctuation
//                                                           ^ punctuation

.custom instance void [System.Runtime]System.ObsoleteAttribute::.ctor() = ( 01 00 00 00 )
// <- directive
//      ^^^^^^^^ keyword
//               ^^^^ type
//                    ^^^^^^^^^^^^^^^^ dim
//                                    ^^^^^^^^^^^^^^^^^^^^^^^^ type
//                                                            ^^ punctuation
//                                                              ^^^^^ member
//                                                                   ^ punctuation
//                                                                    ^ punctuation
//                                                                      ^ punctuation
//                                                                        ^ punctuation
//                                                                          ^^ number
//                                                                             ^^ number
//                                                                                ^^ number
//                                                                                   ^^ number
//                                                                                      ^ punctuation
