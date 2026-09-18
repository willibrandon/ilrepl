# Terminal recordings

Run from the repository root with VHS installed:

```sh
dotnet build src/IlRepl/IlRepl.csproj
vhs vhs/readme.tape
vhs vhs/quick-start.tape
```

Both tapes use the same font, padding, theme, and window bar as dotsider.
`--no-history` keeps existing history out of the recording.
The prompt opens before the execution host is ready. Both tapes wait for the status bar
to start with the empty stack before showing the terminal and sending instructions.

`readme.tape` writes `assets/ilrepl.png` at 1200 × 640, used by the README and the editing page.
The extra height fits the transcript and completion details without a scrollbar.
`quick-start.tape` writes `docs/public/quick-start.gif` at 1200 × 600. Its roughly 24-second loop uses
100 ms typing, short pauses before Enter, one-second pauses on stack changes, and three
seconds on each result. Keep this pacing when updating the recording.
