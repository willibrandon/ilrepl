---
title: Saving and sharing sessions
description: Keep an editable experiment, reopen it without running code, and share it with its dependencies.
---

Save an experiment with `.session save example.ilrepl.json`, or press Ctrl+S. A session keeps your source,
comments, declarations, method edits, arguments, unfinished blocks, editor draft, and previous results.
It also records the runtime and the dependencies used by the experiment.

```cil
ldc.i4 6
ldc.i4 7
mul
ret
.session save arithmetic.ilrepl.json
```

Open it with `.session open arithmetic.ilrepl.json`, Ctrl+O, or `ilrepl arithmetic.ilrepl.json`.
Opening starts a fresh runtime and reconstructs source and definitions without executing cells.
It redisplays saved input with its original prompt numbers and saved output, clearly labeled as history.
Your unsent draft, caret, and selection return to the editor. Historical output is not recomputed.
You can inspect definitions with `.dis`, `.il`, `.edit`, and `.diff`, or continue working in the restored draft.

`.save example.ilrepl.json` and `.load example.ilrepl.json` are aliases for saving and opening sessions.
Other `.save` paths still [export an assembly](/usage/saving-cells/).

Try the [portable arithmetic session](/sessions/arithmetic.ilrepl.json). It needs no external dependencies.
Here is the same experiment saved, reopened, inspected, and explicitly run:

<!-- transcript-only -->
```ilrepl
il[2]> .session save arithmetic.ilrepl.json
  saved session /work/arithmetic.ilrepl.json
il[2]> .session open arithmetic.ilrepl.json
  Session opened. Nothing has run yet. Saved output is shown for reference.
  saved session history (no code executed)
  1: cell, succeeded (historical)
il[1]> ldc.i4 6
il[1]> ldc.i4 7
il[1]> mul
il[1]> ret
  = 42 : int32
  end of saved history; no code executed
il[2]> .session run
  running cell 1 from the saved source
  = 42 : int32
```

## Run and recall

`.session cells` lists submissions using the numbers from their prompts. Results are labeled as historical output.
`.session cell 3` recalls that submission into the editor, reusing active argument declarations and restoring missing ones.
Press Enter when ready; recalled source uses the definitions and argument values currently in the session.

`.session run` runs the recorded experiment in source order from a fresh runtime. It includes a complete current draft.
Definitions, redefinitions, edits, and resets take effect where they originally appeared. Execution stops at the first failure,
and the remaining source stays available. Incomplete source and missing dependencies are reported before execution starts.
Ctrl+C during `.session run` stops its execution host immediately and reopens the source without replaying it.
This differs from [interrupting an ordinary cell](/reference/keyboard/), which asks before discarding runtime state.

Use `.session run 2 4-6` to run selected executable cells. Other cells are skipped, including setup cells that might create
objects or set static fields. Use run-all when those effects are needed.

To run a saved experiment and exit:

```sh
ilrepl arithmetic.ilrepl.json --run
```

Opening never restores live objects, static field values, file handles, or background tasks. Previous output is text,
not a live result value. Explicit execution can run embedded code and affect files or services just as ordinary IL can.
A fresh runtime does not undo those external effects.

## Restart and recovery

`.session restart` starts a fresh runtime from the retained source. Definitions and method edits are reconstructed
and can be called by the next cell. Earlier cells are not replayed, and saved argument declarations do not recreate objects.
Objects, argument values, and static field values from the previous runtime are lost.

If the execution host exits unexpectedly, ilrepl reports its exit status and available diagnostic output, then attempts
one restart. The interrupted cell remains in `.session cells` and in saved session history. Recall it with `.session cell`
to inspect or change it before executing it again.

Your draft stays editable during recovery. If a replacement host cannot start, `.help`, `.session save`, `.session restart`,
and `.quit` remain available. Saving does not require a working execution host. Restarting does not undo files or other
external changes made before the interruption.

## Save changes

Ctrl+S saves to the associated file. The first save asks for a path, initially `session.ilrepl.json`.
Ctrl+O asks for a file to open. Escape cancels either dialog and keeps the editor selection and caret.
Use Up, Down, or Tab to choose Save, Discard, or Cancel when asked about unsaved changes.
`.session save another.ilrepl.json` saves under a new name and uses that path for later saves.

When opening would discard modified source, the terminal offers Save, Discard, and Cancel.
Quitting offers the same choices for a modified session associated with a file. Scratch sessions that have never been saved
keep the usual quit behavior. Ctrl+Q during a running cell stops the host immediately without a save prompt.
In batch input, `.session open path --force` explicitly permits replacement.
Reaching batch EOF does not run reopened source; only newly supplied executable input retains implicit EOF execution.

`.session` shows the file, modification status, runtime, and references.
`.quiet` and `.time` are your presentation settings and do not travel with the document.
`.reset` clears current definitions, edits, arguments, and the cell, while keeping dependencies, the file association,
and historical submissions.

## Dependencies

Load a package using NuGet version syntax:

```cil
.load "nuget:Humanizer.Core"
.load "nuget:Humanizer.Core,2.14.1"
.load "nuget:Humanizer.Core,[2.14.1]"
```

Omitting a version selects the latest stable release and records it as the minimum for later package additions.
A bare version is a minimum; brackets pin an exact version.
Ranges, floating versions, and prereleases follow NuGet rules. Root requests resolve together, and the session records
the exact resolved graph. Your NuGet.Config, package sources, source mapping, caches, and credential providers apply.
Package loading requires the .NET runtime; it does not require an SDK.

Load a project file, or a directory containing one supported project:

```cil
.load "../Library/Library.csproj"
.load "../Library" --configuration Release
.load "../Library" --framework net10.0 --no-build
```

Projects require an installed .NET SDK and honor `global.json`. C#, F#, and Visual Basic SDK projects are supported.
The default configuration is Debug; ilrepl chooses the nearest compatible target framework.
`--no-build` uses existing outputs and reports missing or stale files. Projects requiring `Microsoft.AspNetCore.App`
or `Microsoft.WindowsDesktop.App` need a different shared framework from ilrepl's `Microsoft.NETCore.App` host.

Opening a session uses verified embedded, cached, or existing dependency files. It does not download packages or build projects.
Missing dependencies leave source available for inspection and saving. `.session restore` explicitly recovers locked packages;
add `--build` to permit project evaluation and builds. If rebuilt output differs from the recorded hashes, use `.load` to adopt it.
Restoring on another operating system keeps the recorded package versions and selects assets for that platform.
Package asset paths are relative to their packages. Projects within the same repository keep relative paths.
For projects outside that repository, shared files retain the project filename; place the project beside the session file
or load its new path before rebuilding. Local reopening also remembers the original location.

Rebuilt dependencies can replace unused references. When a definition, edit, current cell, or earlier execution uses the assembly,
`.load "../Library" --reload` reconstructs the source against the new output in a fresh runtime without replaying cells.
Saving and reopening is also a way to start with fresh execution state. `.assemblies` shows reference origins and availability.

Use `.session save example.ilrepl.json --embed` to include available dependency images in a shared file.
Captured edit originals remain included even without `--embed`. A dependency may still require a particular platform,
architecture, native library, or runtime. Embedded images do not make incompatible code portable.
Use `--embed` when a file must remain usable without the original dependency files or your local cache.
