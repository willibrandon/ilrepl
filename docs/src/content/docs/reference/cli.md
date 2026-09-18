---
title: Command line
description: Options for the ilrepl executable.
---

```
ilrepl [options] [script]
```

| Option | Meaning |
| --- | --- |
| `script` | An IL script to run, or a `.ilrepl.json` session to open without execution. |
| `--session <path>` | Open a session, including a file without the recommended extension. |
| `--run` | Explicitly run the opened session and exit. Requires a session; cannot accompany a script or `--eval`. |
| `-e, --eval <il>` | Run lines separated by `;` and exit. `ret` runs the cell. Can be repeated. |
| `--batch` | Read lines from standard input without the terminal UI. Implied when input is piped. |
| `-q, --quiet` | Do not echo the stack after each instruction. |
| `--no-color` | Plain output. `NO_COLOR` in the environment does the same. |
| `--no-history` | Do not read or write the history file. |
| `--version` | Print the version. |
| `--help` | Print the options. |

The exit code is 0 when every line succeeded, 1 for source, document, dependency, or execution failures, 2 for an unreadable input path,
3 when the host cannot start or exits unexpectedly, and 130 when execution is interrupted.
An unexpected host exit is a failure even when user code exits with code 0.
Input that ends inside a `.method`, `.class`, or `.edit` block is an error;
close it with `}` first.

Ctrl+C during a script, `--eval`, piped input, or `--run` stops its processes and exits with code 130.
Output already received is written before exit. Remaining input is not executed, and batch mode does not restart the runtime.

Reopened source and drafts stay inert at batch EOF. Use `--run` or `.session run` to execute a saved experiment.
See [Saving and sharing sessions](/usage/sessions/).

Scripts accept `.edit method as Name { ... }` blocks. Add `--assert` to `.compare` to require a complete
match and return exit code 1 otherwise. Without it, a difference is reported as a result.

A trailing `.show`, `.il`, `.dis`, `.diff`, or `.jit` suppresses pending-cell execution at EOF, including
when inspection fails. Their aliases have the same behavior. Further accepted cell instructions restore
EOF execution; explicit `.run`, blank-line execution, and `ret` retain their usual meanings. Native
comparisons accept `--assert` to fail a script when the captured native code differs.

## Examples

```sh
ilrepl -e 'ldc.i4 6; ldc.i4 7; mul; ret'
ilrepl samples/Transcripts/exceptions.il
printf 'ldstr "piped"\nret\n' | ilrepl --no-color
```

## History

The terminal UI keeps every submission, a block as one entry, in `~/.config/ilrepl/history`, or
in `$XDG_CONFIG_HOME/ilrepl/history` when that variable is set, and in `LocalApplicationData\ilrepl\history`
on Windows. The file is only ever appended to, under a lock file beside it, and the newest
thousand entries are loaded at start. When it cannot be written the session says so once and
goes on. See [Editing blocks](/usage/editing/).

## Environment

| Variable | Meaning |
| --- | --- |
| `ILREPL_HOST_PATH` | Path to `ilrepl-host.dll`. Defaults to `host/` beside the executable. |
| `DOTNET_HOST_PATH` | The `dotnet` muxer used to run the host. Defaults to the running muxer or `dotnet` on the path. |
| `NO_COLOR` | Disables colors in batch output. |
| `XDG_CONFIG_HOME` | Where the history file is kept, under `ilrepl/`. |
