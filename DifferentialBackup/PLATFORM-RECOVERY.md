# Run identity and platform behavior

The command syntax and exit codes remain unchanged: 0 for completion, 1 for
completion with omissions or warnings, 2 for failure, and 130 for cancellation.

## Unfinished runs

A backup requires write access to both roots. Each root contains the reserved
`.differential-backup.active.json` record while a run is unfinished. Both records
identify the same GUID, their source/destination roles, and the owner of the
local staging profile. Only one unfinished run may use a root. A tag belonging
to another machine/profile must be resolved on its owner; it is never replaced.

Set `DIFFERENTIALBACKUP_STAGING_ROOT` to configure local staging. Staging must
be separate from both roots; backing up an entire home directory or drive may
require a staging location outside it.

Staging is under the user's application-data directory, in
`DifferentialBackup/Staging/<full-run-guid>`. Initialization journals and
independent completion records are outside that run directory. Tags have no
expiry. Failures and cancellation retain tags and captured parts. Successful
completion, including a finalized incomplete backup, removes owned staging and
both tags. Completion records are removed last. A restart that finds a completed
run finishes its cleanup and exits; a subsequent invocation starts another run.

Ordinary root aliases are resolved for safe access and overlap checks. Tags,
rather than resolved paths, identify unfinished runs. Missing, malformed,
conflicting, or foreign tags and missing recovery data stop the operation.
An interrupted creation of one tag is repaired only when the journal verifies
both roots. Locks use reusable anchors; Unix locking failures are checked
explicitly and prevent execution.

The root metadata names `.differential-backup.active.json`,
`.differential-backup.lock`, and application temporary metadata files are
reserved. Only root metadata is excluded from discovery. A matching filename
inside an ordinary child directory remains backup data. Do not place unrelated
data at a reserved tag filename.

## Names, state, and compatibility

Job format 3 stores exact root-relative file identities. Manifest format 4
records the run GUID and uses `/` archive separators. Case and Unicode spelling
are preserved; literal Unix backslashes and colons remain literal names.
Restore rejects traversal, unsafe links, file/directory conflicts, names the
target cannot represent, and names that collide on its actual filesystem.
Temporary restore files have short names independent of the original filename.
Root restore journal/log/lock names and the selected restore report name are
reserved; a colliding archive name is rejected before any file is replaced.
Preflight may create empty destination directories and removes its owned probes;
it completes before any existing target file is replaced.

Destination state is `.differential-backup.state.json` in the selected backup
destination. Source scopes and exact relative file keys carry publication dates.
Older recovery commits cannot replace newer baselines. A later source alias
that cannot be matched confidently is captured afresh. The temporary run GUID
does not provide a permanent source identity. Executable-directory hash/history
files are no longer used by the CLI. Query and restore enumerate published sets
and legacy ZIPs in the selected destination, so they work from another
installation. Logs are in the user's application-data `DifferentialBackup/Logs`.

Readers retain job format 2, published formats 2/3, and legacy timestamped ZIPs.
Legacy staging is adopted only after journal, artifact, and root validation.
Multiple matching jobs require explicit resolution. Published checksummed files
are never rewritten. Ambiguous legacy backslash names on Unix are rejected.
Callers that manage pipeline hosts directly should own a `BackupRunSession`,
use its resolved roots and `CreateRunState()`, and call `Complete` only after a
terminal success/warning outcome. The low-level untagged journal API remains
available to read older state and to exercise individual pipeline systems.

## Verification

CI runs both projects on Windows, Linux, macOS, and a case-sensitive APFS volume.
Capabilities are checked on the test filesystem. An open handle does not block
replacement on Unix; tests specifically requiring that behavior record a skip.
NAS and mapped-drive integration requires a real Windows share and is not
verified by hosted CI. Record those results before deploying to that provider.
WSL testing must use its native Linux filesystem, rather than `/mnt/c`.
