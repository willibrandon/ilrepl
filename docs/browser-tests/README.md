These smoke tests exercise session files and worker recovery in the live demo using Chromium.
They run locally, separately from `dotnet test`, and are not part of CI.
Deployment tests also keep the same browser context across a site update, with cached worker imports and saved command history.

From the repository root, after installing the SDK's `wasm-tools` workload:

```sh
dotnet run --file scripts/Publish-Wasm.cs
pnpm --dir docs install --frozen-lockfile
pnpm --dir docs build
pnpm --dir docs exec playwright install chromium
pnpm --dir docs test:browser
```

The tests start an Astro preview server on port 4322. Failure screenshots and Playwright traces are written to `artifacts/browser-results`.
