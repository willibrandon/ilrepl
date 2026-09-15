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
Use single quotes around CIL names containing spaces, such as `.edit int32 Owner::'Read as Text'()`.

Change the method and press Enter to save a revision. Reopen it with `.edit Maximum`. If validation fails,
the source returns to the editor and the last working revision stays callable. The original is kept
until `.reset`; `.clear` abandons an open edit without removing saved revisions.

The method name, generic parameter count, and instance/static declaration must stay the same.
Changes to parameter names and `[in]`, `[out]`, and `[opt]` flags are saved with the copy.
Use `.param [1] = int32(8)` to change a parameter's default. Unmentioned defaults are kept; `[opt]` controls whether it is optional.
Use `.custom` to add an attribute to the method, or after `.param [N]` to add one to a parameter; `[0]` selects the return value.
Original attributes are kept. Each revision applies its attribute additions afresh.
Use `.override` to map the edited method to an interface or virtual base method. Other dispatch mappings are kept.

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

Changing `vararg` in the method header changes the copy's calling convention.
Vararg copies use the usual call syntax, such as `call vararg int32 Copy(int32, ..., string)`.
Optional arguments follow `...`, including calls to private copies and comparisons with external originals.
Their types can use the caller's generic parameters.
Managed vararg execution requires Windows.

## Inspect the differences

`.dis Incremented` shows the copy; `.dis Incremented --original` shows the original.
`.diff Incremented` compares their IL, stack states, signatures, locals, and exception regions.
`.diff` alone selects the latest edit.

Equivalent encodings, such as `ldc.i4.1` and `ldc.i4 1`, compare as equal. Add `--raw` to include encoding
and byte-offset differences. Floating-point bit patterns remain distinct.

## Compare execution

Use `.compare Name (<literals>)` for a static method with literal arguments.
Both versions must be static with all generic arguments supplied.
The literals must fit the parameter count and types of both versions.
After saving an unchanged `Maximum`, `.compare Maximum (17, 42) --assert` reports `match`.
Generic methods use the type arguments selected when you opened the edit.
Each `ref` or `out` literal gets a separate variable. Use a scenario when arguments need to share a variable.

For an instance method, object input, or repeated call, write a parameterless
session method to set up the inputs. Run it with `.compare Name using Scenario`.
Use CIL quotes for names with spaces: `.compare Name using 'My Scenario'`.
The scenario itself must be non-generic; supply generic arguments in the calls inside it.
Both versions must have matching signatures and generic constraints to use the same scenario.
Renaming a generic parameter or reordering equivalent constraints keeps the scenario compatible.
Each side calls the original or the copy:

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

The report shows receiver and argument values as `before -> after`, followed by each call's return value
or exception, including all aggregate children, inner exceptions, and exceptions caught by the scenario.
Exception details include stored fields such as `ParamName`, `ActualValue`, and custom data, without reading getters or stack traces.
If an exception cannot be captured completely, the report explains why.
It also shows console output, including direct stream writes, and tracks shared objects and `ref` aliases.
Objects are compared through their fields without calling user properties, `ToString`, or equality methods.
Comparisons detect reference replacement even when the new object has the same field values.
`Dictionary` and `HashSet` retain their entries, enumeration order, comparer settings, and shared references.
Immutable hash collections, their builders, and frozen collections use a stable structural order and retain their comparer settings.
Concurrent dictionaries and hashtables use their logical entries and comparer settings, independent of bucket layout and insertion order.
Synchronized hashtables retain their relationship to the underlying table.
Distinct keys with identical structural observations make the comparison incomplete.
`Task`, its subclasses, and `ValueTask` results are awaited.
Tasks keep their identity and `AsyncState`. A null task is reported as `null Task`, distinct from a completed task with a null result.
A scenario must await any work it starts.
Value tasks keep their original representation. Return a source-backed value task from the scenario to observe its completion;
consuming it inside the scenario leaves that call's result unavailable.
Spans and other byref-like values remain usable in a scenario, but their observations are unavailable.
A null managed reference is reported as `null reference`, distinct from a variable containing `null`.
Array observations keep their bounds and type: `T[]` is a vector, while `T[*]` is a rank-one multidimensional array.
Virtual calls follow overrides, including through delegates. Calling an override alone does not invoke the selected base method.

`different-inputs` means the inputs or call counts differed. A comparison is incomplete if it cannot
inspect all results or the scenario never calls the method. With `--assert`, anything other than a
complete match gives the script exit code 1.

## Starting state and limits

Each comparison starts two clean processes on desktop or two workers in the browser. Both load the
declarations and run their initializers. Previous cells are not rerun. The original keeps the method
and dependency versions from when you opened the edit.
Desktop comparisons stop background child processes when each side ends.

Both sides get the same culture, environment, stdin, and working path. Each run starts with fresh files.
Use `--stdin "text\n"` for console input and `--files directory` to supply the files.
Desktop comparisons also supply that input to `Console.OpenStandardInput()` and native stdin readers.
Files, directories, and relative symlinks keep their captured timestamps. Links outside the supplied directory are rejected.
File attributes and Unix permission modes are also kept.
If the filesystem cannot restore the captured metadata, the comparison reports a setup failure.
Browser paths refer to its virtual filesystem.

The timeout is 30 seconds per side, starting after runtime startup. Change it with `--timeout 500ms`,
`--timeout 30s`, or `--timeout 2m`. Cancellation, timeouts, crashes, and output limits end the comparison
as incomplete. Your session stays available.
Output captured before termination stays in the report, up to the limit for each stream.

Comparisons cannot reset clocks, randomness, remote services, or files outside the working directory.
Set up repeatable inputs in your scenario. The transcript describes these conditions before running it.

## Types and dependencies

`.edit` copies the declaring type and the fields, constructors, and helpers the method needs.
Signatures, generic constraints, layout, and member metadata are kept, including property and event accessor associations.
Interface inheritance and explicit implementations are preserved.
An external base keeps its original interface types.
Type and module initializers keep the helpers they need. Opening or saving an edit does not run its code.
String lookups through `Type.GetType`, `Assembly.GetType`, and `Module.GetType` recognize original names of copied types,
including nested types and generic arguments.
String-based `Activator.CreateInstance`, `Activator.CreateInstanceFrom`, and `Assembly.CreateInstance` calls also recognize copied type names.
Types reached only by name are included, even when declared separately in the source assembly.
If the name is supplied at runtime, the source assembly's types must all be copyable.
Public helpers in the same assembly are included when they use copied state or types.
Other public methods remain references to their original assemblies.
The dependency list includes local and member signatures, custom modifiers, catch types, and `calli` signatures.
Type and member custom attributes keep their constructors, named members, and type arguments in saved copies.
Marshal descriptors keep their referenced types, including SAFEARRAY subtypes and custom marshaler implementations.
Private delegates retain their runtime methods and copied targets. Delegate binding by name keeps the named members available.
When code uses reflection, copied types retain their full member context, including private and nested declarations.
Known constructor lookups and reflective invocation use the copied constructors.
Runtime declarations needed only for reflection keep their metadata.
Generated helpers do not change the declaring type's reflected member list.
Assembly and module inspection is rejected for identity, reference comparisons, module lists, global members, token resolution,
file paths, image metadata, type lists, resources, satellite assemblies, and custom attributes.
Copies do not retain this source metadata. This includes `GetTypes`, `DefinedTypes`, `GetForwardedTypes`,
`Assembly.GetName()`, `Assembly.Location`, `Assembly.GetReferencedAssemblies()`, `Module.ModuleVersionId`, and attribute APIs.
Use type or member APIs to inspect copied attributes.
Copied members receive new metadata tokens, so reading their `MetadataToken` is rejected.
Type names that change when their type is copied are rejected.
External helpers cannot receive copied metadata, including callback results, because its identity changes.
These limits also apply to reflective invocation and delegate binding. The target must be known during preflight.
Call type lookup and string activation APIs directly so copied names can be translated.

Use `.methods Name` to see each dependency's source location, member, assembly, access, and whether it
was copied. The report updates with each saved revision.
A missing optional library only affects a comparison if the executed code needs it.

Copied types are distinct .NET types. For an instance call, construct the copied type shown by `.types`,
as the counter example does. Code that requires the original type cannot accept the copy.
This also applies to external signatures that refer to it inside custom modifiers or function pointers.
If a dependency cannot be copied, `.edit` explains why and leaves the source available to change.

Some framework methods use runtime internals that cannot be copied. You can replace that code and
compare the result with the actual original. Scenario calls need compatible signatures.
When the original needs its assembly file context, that file must still match the captured image or comparison reports a setup failure.
Satellite assemblies are captured from loaded assemblies and adjacent culture directories.
Adding, changing, or removing an adjacent satellite after capture causes a setup failure.

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
