# Recorded terminal measurements

These JSON archives retain the actual samples from packaged Native AOT reference runs. Directory names identify the RID and UTC run
start. Each record names the measured source commit, package executable hash, generated workload hashes, machine, runtime, and SDK.
These are observations on the recorded machine, not portable performance guarantees.

The initial baseline contains two Linux x64 runs of `f1dcf38` on a Ryzen 9 9950X running Debian 13, with SDK 10.0.400 and runtime
10.0.11. Both use the same Native AOT package and generated fixtures, including the session-signature and Cecil core-library import
cache changes. Measurements of later commits retain their own source and package identities.

The combined-workload observation at `20260918T144856480` measures `b12e3d0`, including the terminal reply framing and diagnostic
navigation corrections. It meets all existing budgets and retains its exact package and host hashes. The preceding attempt ended
before writing its final record; its available process artifacts and log remain in `20260918T144148154`. These observations do not
replace the two complete baseline runs.

The combined-workload observation at `20260918T164312150` measures `0523005`, including acknowledged completion delivery and the
help refresh fix. It meets all existing budgets and retains the matching package, host, and fixture identities. It is an additional
observation; the two complete baseline runs remain unchanged.

`ArchiveSchema` identifies this storage format. `Record` preserves every original startup timestamp, latency sample, percentile,
process allocation, and memory observation. `Fixture` separates the original colon-delimited generator and content hashes into fields
to keep generated data readable. `OriginalRecordSha256` identifies the original measurement artifact before that structural change.

`Outcome` distinguishes runs meeting their budgets from measured failures. Failed runs retain all observations and the exact missed
budgets; they are evidence for the subsequent change, not replacement baselines. Reviewed baseline summaries and archive hashes live
in `../responsiveness-baselines.json`. Generated assembly and session fixtures remain outside source control and can be regenerated
using `../responsiveness-validation.md`.

Incomplete attempts retain their failure details and available process observations separately from complete reference distributions.
They never qualify as baselines. When an older driver stopped before writing a record, an incomplete archive preserves its original
log and process artifacts without reconstructing missing latency samples.
Concatenating `OriginalLogSegments` without separators reconstructs that log exactly; each retained process artifact has its own hash.
