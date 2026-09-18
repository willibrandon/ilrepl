# Terminal responsiveness validation

The normal suite tests real editing results, persistent execution-thread state, bounded request queues, snapshot ownership, retained
terminal frames, and small generated metadata and session fixtures. Full reference measurements run separately because the large
workloads exceed the time budget of ordinary CI legs.

Build and publish the Release Native AOT package for the machine being measured. Build the test driver in Release, then invoke its
private measurement mode directly. For example, on Linux x64:

```sh
dotnet run --file scripts/Publish-NativeAot.cs -- --rid linux-x64
dotnet build tests/IlRepl.Tests/IlRepl.Tests.csproj -c Release
dotnet tests/IlRepl.Tests/bin/Release/net10.0/IlRepl.Tests.dll \
  --responsiveness-measure artifacts/native-aot/linux-x64/publish/ilrepl \
  artifacts/responsiveness/linux-x64
```

Use the frontend executable from the publish directory containing its matching `host/` distribution. Run macOS, Windows, glibc Linux,
and musl packages in their supported target environments. Musl measurements require the Alpine container and musl .NET host used by
packaged smoke validation. Do not compare a JIT frontend or a package built for another libc with the Native AOT reference.

An optional scenario selects `empty`, `catalog`, `session`, `draft-200`, `draft-2000`, `generic-4`, `generic-16`, `generic-64`, or
`combined`. With no scenario, all run. `--quick` validates the harness using smaller generated workloads and samples; its records
explicitly identify it as a smoke run and are never accepted as reference baselines. `--prepare` generates and caches the selected
fixtures without launching timed terminals, so expensive fixture setup can finish before the quiet reference interval.

The generator writes content-addressed artifacts under `artifacts/responsiveness/fixtures/`. The large catalog contains 10,000 types
and 100,000 callable methods. Retained sessions are produced by submitting 10,000 actual cells and definitions through the packaged
host, including 1,000 definitions. The resulting journal is cached against the generator version and host, engine, and protocol
content hashes, then hydrated by the measured host. Drafts contain exactly 200 or 2,000 physical lines. Generic constructions increase
from four to 64 levels. No generated binary belongs in source control.

Each scenario launches the actual packaged frontend in a PTY and records synchronized terminal frames. Startup records distinguish the
first painted prompt with a focused editing caret, the first successful edit, and the first accepted submission. The 200 ms editable
prompt budget applies to the first focused prompt frame, after an immediate real edit proves it accepts input. The separate
`first-edit-painted` diagnostic retains the additional input and render cycle; interaction latency has its own budget. The launch
clock starts after constructing the PTY observer, immediately before starting its process. Frontend entry-to-frame samples distinguish
loader and terminal initialization cost without removing either from the acceptance budgets. Interaction samples measure input until
the first frame containing changed text or caret. Completion samples stop when the current usable member page is painted. Cold catalog
completion runs once per fresh process. Warm interaction samples exclude the first 25 operations, then retain at least 1,000
measurements. Startup distributions retain at least 30 launches. Every sample contributes to nearest-rank percentiles; outliers are
retained. Full runs keep all scenario records and exit unsuccessfully when any measured acceptance budget is exceeded.

The private `ILREPL_MEASUREMENTS_DIRECTORY` variable enables one measurement artifact per frontend, host, and supervisor process. These
artifacts contain the actual runtime, monotonic startup stage timestamps, allocated bytes, retained managed bytes after a shutdown
collection, and resident working set. Collection happens after latency samples. The driver records its SDK/runtime, OS/RID, CPU,
machine identity, commit and dirty state, package path and executable hash, fixture hashes, configuration, scenario, raw samples,
percentiles, and timestamp frequency. Every launch must retain its frontend, host, and applicable supervisor artifacts. The driver
does not copy the environment or credentials into artifacts.

Keep the machine idle, use the same power configuration, and retain at least two complete comparable runs before proposing a baseline
change. The reference budgets in `responsiveness-baselines.json` are acceptance targets, not observations. Add actual measurements and
their machine identities only after reviewing the JSON artifacts. Explain hardware, runtime, fixture, or implementation changes with
the baseline change; never replace a baseline automatically or relax a target to hide a regression. CI retains
`artifacts/responsiveness/` alongside its validation artifacts when measurement records exist.

The generated bootstrap catalog is checked separately:

```sh
dotnet run --file scripts/Generate-BootstrapCatalog.cs -- --check
```

When command or opcode metadata changes, run the same script without `--check` and review the generated source with the authoritative
catalog changes. A real-host parity test checks the bootstrap catalog against the host hello.
