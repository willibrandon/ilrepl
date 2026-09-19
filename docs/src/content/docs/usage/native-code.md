---
title: Native code
description: Inspect and compare the machine code CoreCLR generates for your IL.
---

Use `.jit` to see the native code CoreCLR generates on your machine. It compiles the selected body in a
fresh worker using the host's runtime and architecture.

## Select a body

| Command | Body inspected |
| --- | --- |
| `.jit` | The pending cell, or the latest retained executable cell when the pending cell is empty. |
| `.jit cell 3` | A retained cell with its declarations and method bindings at that point in the session. |
| `.jit Fib` | The current implementation of a session method. |
| `.jit int32 Example::Calculate(int32)` | A method in a closed class or loaded assembly. |
| `.jit Copy --original` | The original method associated with an edit. |
| `.jit --info` | Native inspection capabilities and the worker's runtime configuration. |

Method references follow [the same syntax as `.dis`](/usage/disassembly/). Close generic type and method
arguments before inspecting them. Constructors and instance methods can be compiled without creating
an instance. Bare `.jit` always selects a cell; the last `.dis` target does not affect it.

Earlier cells are not replayed. A retained cell is compiled again from its captured source. Method
bodies retain their IL, attributes, and bindings. Static fields start fresh in the worker, so a native
view describes a fresh compilation rather than a snapshot of the live session's native memory.

## Compilation and execution

The default is `--tier fullopts`, with tiered compilation disabled. It requests normal optimized
compilation without invoking the body. Method attributes such as `nooptimization` still apply; the
report shows the actual runtime tier.

Preparing code can activate a module initializer. If a module reachable from the selected body has one,
an inspection without execution asks you to add `--allow-initializers`. The diagnostic names the module.
Unused captured dependencies do not require this permission. This flag permits initialization during
compilation; it does not authorize calling the target body.

Literals or a scenario explicitly authorize execution, just as they do for `.compare`:

```text
.jit Fib (10) --tier tier1
.jit Step using Scenario --tier tier1
.jit Tick --run --tier tier1
```

Use `--run` for a parameterless method or a cell with captured argument values. Instance methods need a
parameterless scenario that constructs the receiver. No arguments or receivers are invented.

Each workload uses a fresh worker. Calls within that worker share its state. Choose a repeatable
workload and account for file writes, output, and other side effects. Process isolation does not make
arbitrary user code a security sandbox.

## Tiers and profiles

| Option | Behavior |
| --- | --- |
| `--tier fullopts` | Optimized compilation without tiering; `optimized` is an alias. |
| `--tier tier0` | Request the initial tier with tiering enabled. |
| `--tier tier1` | Repeat an explicit workload until the requested optimized tier is observed. |
| `--pgo on` or `--pgo off` | Control dynamic PGO for tiered requests; the default is on. |
| `--iterations N` | Cap workload invocations. |
| `--timeout 30s` | Limit work after worker startup. |

Tier 1 execution continues on a paced loop, including after an instrumented version appears. The
report shows the number of driver invocations actually made. Recursive calls made by the target are
not included in that count.

The default Tier 1 cap is 1,000 invocations at up to 100 invocations per second, within a 30-second work
timeout. Slow calls can make the time limit arrive first. There is no fixed call count that guarantees
promotion. If the cap is reached, ilrepl stops calling and waits for pending runtime evidence until the
deadline. A missing requested tier is an incomplete inspection.

Runtime tier labels, such as `Tier0`, `Instrumented Tier0`, `Tier1`, `OSR`, `FullOpts`, and `MinOpts`, are
preserved. PGO provenance is reported separately as Dynamic, Synthesized, Static, None, or Unknown.
An initial tier without a consumed profile reports PGO as not applicable.
Optimized code can use synthesized profiles even when dynamic PGO is disabled.

The worker uses a noncollectible loading context by default. Add `--collectible` to reproduce the live
session's collectible loading behavior. Collectible methods cannot tier, and their static-access
helpers can differ from those used by ordinary assemblies. Combining `--collectible` with a tiered
request is rejected before execution.

## Compare native code

Use `--against` for two selected bodies:

```text
.jit Left --against Right
.jit Left --against Right --assert
.diff Copy --native --assert
```

`.diff Copy --native` compares an edit's original and current implementation through the same native
comparison path. Without a name, it uses the latest edit.

Comparisons label the original and edited sides, or the left and right selectors. Changes appear in
unified hunks with surrounding instructions.

Both sides run in separate fresh processes with matching runtime settings and inputs. With `--files`,
the captured fixture tree is restored at the same working path before each side. `--stdin` supplies the
same input to each worker. The workers execute sequentially.

The normal view removes encoding bytes and replaces addresses only when their identities can be
established from runtime or compilation evidence. Constants, fields, string contents, types, callees,
and instruction structure remain meaningful. If address evidence is insufficient, the comparison is
indeterminate and explains why. It never reports equality from incomplete evidence. Displayed symbols
use concise CIL names; comparisons retain their full assembly identities. Ambiguous names stay qualified.

On Arm64 the JIT builds an address from up to four 16-bit moves and leaves out any part that is zero, so
the same load can take a different number of instructions in each worker. A comparison reads such a load
as one step. `.jit` on its own still shows every instruction.

`--raw` includes encoding-level detail, including bytes and process-specific addresses. Raw comparisons
can differ because the workers occupy different addresses. Use normal comparisons for ordinary
code-generation assertions.

Without `--assert`, a complete comparison can report either equal or different successfully. With
`--assert`, a difference fails the command and a batch script. Missing tiers, timeouts, crashes, lost
evidence, and indeterminate comparisons fail regardless of `--assert`. Native equality describes the
captured compilation, not a proof that arbitrary programs behave identically.

## Runtime settings and reports

Use repeatable `--env NAME=VALUE` options to try worker-scoped code-generation settings:

```text
.jit VectorWork --env DOTNET_EnableAVX512F=0
.jit VectorWork --env DOTNET_PreferredVectorBitWidth=128
```

Inherited ISA settings are honored. Explicit overrides take precedence, and equivalent `DOTNET_` and
`COMPlus_` names are treated as one setting. Capture, tiering, and ReadyToRun controls belong to ilrepl;
use their command options instead of conflicting environment overrides. Worker settings never modify
the parent shell or live session. Native capture enables diagnostics in its worker even when your shell
disables them. An explicit override that disables diagnostics is rejected.

ReadyToRun remains enabled for worker startup and unrelated assemblies. An image containing the
selected target is excluded when necessary so its body is actually JIT-compiled. Any global fallback
is visible in the reported settings.

The default view puts native code beneath a compact header with its runtime, architecture, loading
context, tier, code size, and profile provenance. Frame and interruptibility comments and observed
inlinees remain visible. Use `.jit --info` for the full host runtime and JIT identity, effective settings,
and instruction sets. Detailed provenance also remains available to clients in the structured report.
Available instruction sets come from the worker's runtime; they do not identify the physical CPU model. Inlining
evidence identifies contributors, without inventing instruction-by-instruction source boundaries.

Listings vary with the runtime, architecture, and settings. For example, an x64 optimized integer
increment can contain the following instructions:

```ilrepl-native
; Illustrative .NET 10 x64 CoreCLR FullOpts instructions
    lea eax, [rdi+1]
    ret
```

Each side allows up to 16 MiB of JIT output and 64 KiB each of standard output and standard error.
Cancellation and failures retain the available report and terminate the worker's process group.

In a batch script, a trailing inspection command suppresses pending-cell execution at EOF. This applies
to `.show`, `.il`, `.dis`, `.diff`, `.jit`, and their aliases, even if inspection fails. Further accepted
cell instructions restore ordinary EOF execution. An explicit `.run`, blank-line execution, or `ret`
keeps its usual behavior.
