---
title: Commands
description: Everything that starts with a dot.
---

Directives are part of the cell. Commands act on the session.

## Directives

| Directive | Meaning |
| --- | --- |
| `.locals init (T name, ...)` | Declare locals. Names are optional; `[N]` index prefixes and `pinned` are accepted. |
| `.args (T name = literal, ...)` | Declare cell arguments and their values. |
| `.typeparams (T, U)` | Make the cell generic. |
| `.typeargs (int32, string)` | Bind the type parameters for the next run. |
| `.vararg` | Use the vararg calling convention so `arglist` works. |
| `.try {` and friends | Exception blocks. See [Exception blocks](/usage/exception-blocks/). |
| `.method T Name(T arg, ...) {` | Define a method that persists across cells; `}` closes it and completes the cell. See [Methods](/usage/methods/). |
| `.class [attrs] Name [extends T] [implements I] {` | Define a type that persists across cells; `}` closes it and completes the cell. See [Types](/usage/types/). |
| `.field [access] [static] T Name [= constant]` | A field of the open class. `[N]` before the type sets its offset in an explicit layout. |
| `.method [access] [instance\|static] T Name(...) {` | A member of the open class. `ldarg.0` is `this`. |
| `.property T Name() {` and `.event T Name {` | A property or event of the open class, with `.get`, `.set`, `.other`, `.addon`, `.removeon`, `.fire` inside. |
| `.override T::Method` | Inside a member, the interface or base slot it implements; at class level, `.override T::M with method ...`. |
| `.param [N] = constant` | A default for a parameter of the open method. |
| `.custom instance void Attr::.ctor(...) = ...` | An attribute on the class, the field before it, the open method, or its parameter. |
| `.pack N` and `.size N` | The layout of the open class. |
| `.maxstack N` | Set a minimum stack limit from 0 to 65535. It increases if the method needs more. |
| `.locals (T name, ...)` | Declare locals without zero initialization. The list can be empty. |
| `.try A to B catch T handler C to D` | Declare exception ranges with exclusive ends. Also accepts filter, finally, and fault. |

## Commands

| Command | Meaning |
| --- | --- |
| `.help` | Show help. |
| `.ops [filter]` | List opcodes with their stack transitions. The filter matches names and descriptions. |
| `.show` | List the cell, or the open method or class, with control-flow stack states and diagnostic details. |
| `.dis <method>` | Disassemble a method: a framework or loaded method, a method defined with `.method`, or a member of a closed class. See [Disassembly](/usage/disassembly/). |
| `.undo` | Remove the last line of the cell, or of the open method or class. |
| `.clear` | Drop the cell body, keep declarations, methods, and types. Inside a method or class block, abandon the block. |
| `.reset` | Drop the cell body, every declaration, every method, and every type. |
| `.il` | Render the types, the methods, and the cell as ILAsm. |
| `.save <path.dll>` | Write the types, the methods, and the cell to disk as an assembly. |
| `.load <name or path>` | Load an assembly so its types resolve. |
| `.assemblies` | List the assemblies loaded with `.load`. |
| `.methods [Edit]` | List methods and edits, or inspect an edit's dependency report. |
| `.types` | List the types defined with `.class`, with their members. |
| `.stack` | Show the stack. |
| `.time [on\|off]` | Print how long each run took. |
| `.quiet [on\|off]` | Stop echoing the stack after each instruction. |
| `.run` | Run the cell, the same as `ret` or an empty line. |
| `.quit` | Leave. |

## Method edits

| Command | Meaning |
| --- | --- |
| `.edit [method] [as Name]` | Open an editable copy. With no method, use the last `.dis` target. |
| `.edit Name` | Reopen an existing edit. |
| `.edit method as Name { ... }` | Define a copy from a `.method` block. |
| `.dis Name [--original]` | Disassemble the copy or its original. |
| `.diff [Name] [--raw]` | Compare instructions, stack states, and metadata; `--raw` includes encoding differences. |
| `.compare Name (literals)` | Run a closed static method and its original with the same inputs. |
| `.compare Name using Scenario` | Use a parameterless method to set up inputs and call each version. |

Comparison options are `--assert`, `--timeout duration`, `--stdin "text"`, and `--files directory`.
See [Editing and comparing methods](/usage/editing-methods/) for examples and option details.

## Comments

`//` and `/* */` comments are stripped from every line, outside string literals.
