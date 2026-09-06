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
| `.maxstack N` | Accepted and ignored. |

## Commands

| Command | Meaning |
| --- | --- |
| `.help` | Show help. |
| `.ops [filter]` | List opcodes with their stack transitions. The filter matches names and descriptions. |
| `.show` | List the cell, or the open method, with the stack after each instruction. |
| `.undo` | Remove the last line of the cell, or of the open method. |
| `.clear` | Drop the cell body, keep declarations and methods. Inside a method block, abandon the method. |
| `.reset` | Drop the cell body, every declaration, and every method. |
| `.il` | Render the cell and its methods as ILAsm. |
| `.save <path.dll>` | Write the cell and its methods to disk as an assembly. |
| `.load <name or path>` | Load an assembly so its types resolve. |
| `.assemblies` | List the assemblies loaded with `.load`. |
| `.methods` | List the methods defined with `.method`. |
| `.stack` | Show the stack. |
| `.time [on\|off]` | Print how long each run took. |
| `.quiet [on\|off]` | Stop echoing the stack after each instruction. |
| `.run` | Run the cell, the same as `ret` or an empty line. |
| `.quit` | Leave. |

## Comments

`//` and `/* */` comments are stripped from every line, outside string literals.
