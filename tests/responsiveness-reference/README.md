# Recorded terminal measurements

These JSON archives retain the actual samples from packaged Native AOT reference runs. Directory names identify the RID and UTC run
start. Each record names the measured source commit, package executable hash, generated workload hashes, machine, runtime, and SDK.
These are observations on the recorded machine, not portable performance guarantees.

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
