---
title: Command line
description: Options for the ilrepl executable.
---

```
ilrepl [options] [script]
```

| Option | Meaning |
| --- | --- |
| `script` | An IL script to run, one line per instruction. Prompts are echoed. |
| `-e, --eval <il>` | Run lines separated by `;` and exit. `ret` runs the cell. Can be repeated. |
| `--batch` | Read lines from standard input without the terminal UI. Implied when input is piped. |
| `-q, --quiet` | Do not echo the stack after each instruction. |
| `--no-color` | Plain output. `NO_COLOR` in the environment does the same. |
| `--no-history` | Do not read or write the history file. |
| `--version` | Print the version. |
| `--help` | Print the options. |

The exit code is 0 when every line succeeded, 1 when any line failed, 2 for a bad script path,
and 3 when the host could not be started. Input that ends inside a `.method` block is an error;
close it with `}` first.

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
