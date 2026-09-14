---
title: Editing and comparing methods
description: Copy a method, change its IL, and compare it with the original.
---

`.edit` makes an editable copy of a method, including its signature, locals, labels, and exception handlers.
Use `.diff` to inspect your changes and `.compare` to run both versions with the same inputs.

## Open a method

```cil
.dis int32 Math::Max(int32, int32)
.edit
```

With no method name, `.edit` uses the last `.dis` target. Add `as` to name the copy:

```cil
.edit int32 Math::Max(int32, int32) as Maximum
```

Session methods and edits must have different names. An edit reserves its name until `.reset`.

Change the method and press Enter to save a revision. Reopen it with `.edit Maximum`. If validation fails,
the source returns to the editor and the last working revision stays callable. The original is kept
until `.reset`; `.clear` abandons an open edit without removing saved revisions.

`.methods` lists copies and their revisions. `.types` shows their declaring types, such as
`IlRepl.Edits.Maximum.Owner`. Call the copy by its name: `call Maximum`.
The `IlRepl` namespace is reserved; use another namespace for your own types.

## Replay an edit

Scripts use the same `.edit` block. This one changes an identity method into an increment:

```cil
.method int32 Identity(int32 value) {
  ldarg.0
  ret
}
.edit Identity as Incremented {
  .method public static int32 Identity(int32 value) cil managed {
    ldarg.0
    ldc.i4.1
    add
    ret
  }
}
.diff Incremented
.compare Incremented (41)
```

The comparison reports `different`: the original returns `41` and the copy returns `42`.
Call the copy in a cell:

```cil
ldc.i4.s 41
call Incremented
ret
```

The cell returns `42`. Calling `Identity` still returns the input unchanged.

Vararg copies use the usual call syntax, such as `call vararg int32 Copy(int32, ..., string)`.
Optional arguments follow `...`. Managed vararg execution requires Windows.

## Inspect the differences

`.dis Incremented` shows the copy; `.dis Incremented --original` shows the original.
`.diff Incremented` compares their IL, stack states, signatures, locals, and exception regions.
`.diff` alone selects the latest edit.

Equivalent encodings, such as `ldc.i4.1` and `ldc.i4 1`, compare as equal. Add `--raw` to include encoding
and byte-offset differences. Floating-point bit patterns remain distinct.

## Compare execution

Use `.compare Name (<literals>)` for a static method with literal arguments.
The literals must fit the parameter count and types of both versions.
After saving an unchanged `Maximum`, `.compare Maximum (17, 42) --assert` reports `match`.
Generic methods use the type arguments selected when you opened the edit.
Each `ref` or `out` literal gets a separate variable. Use a scenario when arguments need to share a variable.

For an instance method, object input, or repeated call, write a parameterless
session method to set up the inputs. Run it with `.compare Name using Scenario`.
Both versions must have matching signatures to use the same scenario. Each side calls the original or the copy:

```cil
.class public Counter {
  .field public int32 Value
  .method public instance void .ctor() {
    ldarg.0
    call instance void Object::.ctor()
    ret
  }
  .method public instance int32 Advance() {
    ldarg.0
    dup
    ldfld int32 Counter::Value
    ldc.i4.1
    add
    stfld int32 Counter::Value
    ldarg.0
    ldfld int32 Counter::Value
    ret
  }
}
.edit instance int32 Counter::Advance() as Step {
  .method public instance int32 Advance() cil managed {
    ldarg.0
    dup
    ldfld int32 Counter::Value
    ldc.i4.2
    add
    stfld int32 Counter::Value
    ldarg.0
    ldfld int32 Counter::Value
    ret
  }
}
.method int32 Scenario() {
  newobj instance void IlRepl.Edits.Step.Owner::.ctor()
  call Step
  ret
}
.compare Step using Scenario
```

Both sides start with a new counter. The original returns `1` and sets `Value` to `1`; the copy returns
`2` and sets `Value` to `2`.

The report shows the receiver and arguments before and after each call, the return value or exception,
and console output, including direct stream writes. It tracks shared objects and `ref` aliases. Objects are compared through their fields
without calling properties, `ToString`, or equality methods. `Task` and `ValueTask` results are awaited.
Tasks keep their identity and `AsyncState`; a null task remains null. A scenario must await any work it starts.
Spans and other byref-like values remain usable in a scenario, but their observations are unavailable.
A null managed reference is reported as `null reference`, distinct from a variable containing `null`.

`different-inputs` means the inputs or call counts differed. A comparison is incomplete if it cannot
inspect all results or the scenario never calls the method. With `--assert`, anything other than a
complete match gives the script exit code 1.

## Starting state and limits

Each comparison starts two clean processes on desktop or two workers in the browser. Both load the
declarations and run their initializers. Previous cells are not rerun. The original keeps the method
and dependency versions from when you opened the edit.

Both sides get the same culture, environment, and stdin, with separate working directories.
Use `--stdin "text\n"` for console input and `--files directory` to copy files into each directory.
Empty directories and relative symlinks are kept; links outside the supplied directory are rejected.
Browser paths refer to its virtual filesystem.

The timeout is 30 seconds per side, starting after runtime startup. Change it with `--timeout 500ms`,
`--timeout 30s`, or `--timeout 2m`. Cancellation, timeouts, crashes, and output limits end the comparison
as incomplete. Your session stays available.

Comparisons cannot reset clocks, randomness, remote services, or files outside the working directory.
Set up repeatable inputs in your scenario. The transcript describes these conditions before running it.

## Types and dependencies

`.edit` copies the declaring type and the fields, constructors, and helpers the method needs.
Signatures, generic constraints, layout, and member metadata are kept. Public external methods remain
references to their original assemblies. Opening or saving an edit does not run its code.
Catch types and `calli` signatures are included in the dependency list.

Use `.methods Name` to see each dependency's source location, member, assembly, access, and whether it
was copied. The report updates with each saved revision.
A missing optional library only affects a comparison if the executed code needs it.

Copied types are distinct .NET types. For an instance call, construct the copied type shown by `.types`,
as the counter example does. Code that requires the original type cannot accept the copy.
If a dependency cannot be copied, `.edit` explains why and leaves the source available to change.

Some framework methods use runtime internals that cannot be copied. You can replace that code and
compare the result with the actual original. Scenario calls need compatible signatures.

Methods without an IL body cannot be opened. Instructions and native calls still need support from
the runtime where you run them.

## Generic methods

The editor keeps `!N` type parameters and `!!N` method parameters. A closed method keeps its chosen type
arguments; an open generic method accepts them at the call, such as `call Copy<string>`.

This edit changes which string argument the method returns:

```cil
.class public Choice {
  .method public static !!T Pick<T>(!!T first, !!T second) {
    ldarg.0
    ret
  }
}
.edit !!0 Choice::Pick<string>(!!0, !!0) as Second {
  .method public static !!T Pick<T>(!!T first, !!T second) cil managed {
    ldarg.1
    ret
  }
}
.method string GenericScenario() {
  ldstr "first"
  ldstr "second"
  call Second
  ret
}
.compare Second using GenericScenario
```

The original returns `"first"` and the copy returns `"second"`. You can also compare this call directly:
`.compare Second ("first", "second")`. Session types used as generic arguments keep their captured
definitions after redefinition. See [Arguments and generics](/usage/arguments-and-generics/).

## Exception handlers

Exception regions use labels with exclusive end boundaries, including layouts that braces cannot represent.
This edit catches division by zero and returns `42`:

```cil
.method int32 Divide(int32 divisor) {
  ldc.i4.1
  ldarg.0
  div
  ret
}
.edit Divide as SafeDivide {
  .method public static int32 Divide(int32 divisor) cil managed {
    .maxstack 2
    .locals init (int32 result)
    .try TRY to CATCH catch DivideByZeroException handler CATCH to DONE
    TRY: ldc.i4.1
    ldarg.0
    div
    stloc.0
    leave DONE
    CATCH: pop
    ldc.i4.s 42
    stloc.0
    leave DONE
    DONE: ldloc.0
    ret
  }
}
.compare SafeDivide (0)
```

The original throws `DivideByZeroException`; the copy returns `42`. Handler order, locals, initialization,
and stack settings are kept. See [Exception blocks](/usage/exception-blocks/) for the range syntax.

## Save the result

`.save result.dll` saves the current cell, copies, and their dependencies as an assembly.
`.il` shows the same code as Microsoft ILAsm source. Both work independently of the session.
The entry point is `IlRepl.Cell.Run`; copied members use the type names shown by `.types`.
