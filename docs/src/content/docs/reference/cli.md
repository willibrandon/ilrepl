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
| `--version` | Print the version. |
| `--help` | Print the options. |

The exit code is 0 when every line succeeded, 1 when any line failed, 2 for a bad script path,
and 3 when the host could not be started.

## Examples

```sh
ilrepl -e 'ldc.i4 6; ldc.i4 7; mul; ret'
ilrepl samples/Transcripts/exceptions.il
printf 'ldstr "piped"\nret\n' | ilrepl --no-color
```

## Environment

| Variable | Meaning |
| --- | --- |
| `ILREPL_HOST_PATH` | Path to `ilrepl-host.dll`. Defaults to `host/` beside the executable. |
| `DOTNET_HOST_PATH` | The `dotnet` muxer used to run the host. Defaults to the running muxer or `dotnet` on the path. |
| `NO_COLOR` | Disables colors in batch output. |
