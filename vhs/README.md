# Terminal recordings

Run from the repository root with VHS installed:

```sh
dotnet build src/IlRepl/IlRepl.csproj
vhs vhs/readme.tape
vhs vhs/quick-start.tape
```

Both tapes capture the running terminal at 1200 × 600 with the same font, padding, theme,
and window bar as dotsider. `--no-history` keeps existing history out of the recording.

`readme.tape` writes `assets/ilrepl.png`, used by the README and the editing page.
`quick-start.tape` writes `docs/public/quick-start.gif`. Its roughly 24-second loop uses
100 ms typing, short pauses before Enter, one-second pauses on stack changes, and three
seconds on each result. Keep this pacing when updating the recording.
