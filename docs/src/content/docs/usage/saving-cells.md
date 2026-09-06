---
title: Saving cells
description: Write a cell to disk as an assembly, or look at it as ILAsm.
---

## As an assembly

`.save` writes the current cell as a real assembly with a static `IlRepl.Cell.Run` method. The
cell is kept, so you can still run it.

```
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

Saving uses `PersistedAssemblyBuilder`, so the output has real metadata: declared parameters,
locals, exception blocks, and tokens for every member the cell references.

## As ILAsm

`.il` renders the cell as ILAsm source and `.save` with an `.il` extension is not needed because
you can copy it from the transcript. Operands are fully qualified so the text assembles with
`ilasm` after adding the assembly references it lists.

```
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
