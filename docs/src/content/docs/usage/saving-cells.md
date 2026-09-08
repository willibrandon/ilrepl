---
title: Saving cells
description: Write a cell to disk as an assembly, or look at it as ILAsm.
---

## As an assembly

`.save` writes the current cell as a real assembly with a static `IlRepl.Cell.Run` method,
every method defined with `.method` beside it, and every type defined with `.class` before it.
The cell is kept, so you can still run it.

```ilrepl
il[1]> .args (int32 n = 0)
il[1]> ldarg n
il[1]> ldc.i4 2
il[1]> mul
il[1]> .save doubler.dll
  wrote /home/you/doubler.dll with IlRepl.Cell.Run
```

The file loads like any other assembly:

```csharp
var assembly = Assembly.LoadFrom("doubler.dll");
var run = assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!;
Console.WriteLine(run.Invoke(null, [21])); // 42
```

Session methods are public static methods on the same type, so `GetMethod("Fib")` finds them the
same way, and a session type is a type of the assembly under its own name, `Point` or
`Outer+Inner`. The file is written by the same Mono.Cecil writer that produces the types the
session runs, so it carries what you declared and nothing else: the layouts and offsets, the
constants and modifiers, parameter defaults, attributes, and no constructor you did not write.
A call from one session method to another is a direct call in the file, and nothing in it
refers back to the session.

## As ILAsm

`.il` renders the types, the methods, and the cell as ILAsm source, and `.save` with an `.il`
extension is not needed because you can copy it from the transcript. Operands are fully
qualified and each class is written out with its fields, members, and nested types, so the text
assembles with `ilasm` after adding the assembly references it lists.

```ilrepl
il[2]> .il
.assembly extern System.Runtime {}
.assembly ilrepl_cell {}
.module ilrepl_cell.dll

.class public abstract sealed auto ansi beforefieldinit IlRepl.Cell extends [System.Runtime]System.Object
{
    .method public static object Run(int32 n) cil managed
    {
        .maxstack 16
        ldarg n
        ldc.i4 2
        mul
        box int32
        ret
    }
}
```
