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
| `.maxstack N` | Accepted and ignored. |

## Commands

| Command | Meaning |
| --- | --- |
| `.help` | Show help. |
| `.ops [filter]` | List opcodes with their stack transitions. The filter matches names and descriptions. |
| `.show` | List the cell with the stack after each instruction. |
| `.undo` | Remove the last line of the cell. |
| `.clear` | Drop the cell body, keep declarations. |
| `.reset` | Drop the cell body and every declaration. |
| `.il` | Render the cell as ILAsm. |
| `.save <path.dll>` | Write the cell to disk as an assembly. |
| `.load <name or path>` | Load an assembly so its types resolve. |
| `.assemblies` | List the assemblies loaded with `.load`. |
| `.stack` | Show the stack. |
| `.time [on\|off]` | Print how long each run took. |
| `.quiet [on\|off]` | Stop echoing the stack after each instruction. |
| `.run` | Run the cell, the same as `ret` or an empty line. |
| `.quit` | Leave. |

## Comments

`//` and `/* */` comments are stripped from every line, outside string literals.
