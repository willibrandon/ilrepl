These smoke tests exercise session files and worker recovery in the live demo using Chromium.
They run locally when needed and are excluded from CI and `dotnet test`.
The shared control-flow corpus also executes independently assembled and exported images on Mono WebAssembly.
Deployment tests also keep the same browser context across a site update, with cached worker imports and saved command history.

From the repository root, after installing the SDK's `wasm-tools` workload:

```sh
dotnet run --project tests/IlRepl.Tests/IlRepl.Tests.csproj -c Release -- --export-browser-corpus artifacts/browser-corpus.json
dotnet run --file scripts/Publish-Wasm.cs -- --conformance
pnpm --dir docs install --frozen-lockfile
pnpm --dir docs build:site
pnpm --dir docs exec playwright install chromium
pnpm --dir docs test:browser
```

The tests start an Astro preview server on port 4322. Failure screenshots and Playwright traces are written to `artifacts/browser-results`.

The `--conformance` switch includes a test-only execution entry point. Normal documentation publishing excludes it.
`build:site` builds the pages from that publish. Plain `build` publishes the browser build again without the switch.
Corpus images are generated under `artifacts/` from the existing shared cases and are never committed. Each browser
conformance case gets a fresh real worker so static state and loaded assembly identities cannot leak between images.
