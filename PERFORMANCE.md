# Backup performance regression guards

Run:

```powershell
dotnet test DOPipeline.sln --configuration Release --logger "trx;LogFileName=platform.trx"
./eng/Assert-PerformanceGuards.ps1
```

The guards use real files, real ZIPs and the normal pipeline. They do not compare
elapsed times, which vary by runner. The byte observer counts actual reads inside
the checksum reader, rather than requested validation calls. The storage wrapper
counts component lookups and every entry materialized by a collection query.

Current budgets for a fresh backup:

- Local archive checksum reads: at most one archive-length pass.
- Destination archive checksum reads: at most two passes (transfer and
  postpublication state commit).
- Publication stage checksum reads: at most one pass, after the published-job marker.
- Destination baseline writes: exactly one.
- File lookup/snapshot work: at most 32 * files + 512.
- Part lookup/snapshot work: at most 32 * files + 64 * parts + 512.
- Part snapshot work: at most 32 * parts + 512.

Fresh and interrupted workloads use 64 and 512 files with eight files per part.
Recovery before publication and after the directory rename must use at most
8 * files + 128 file operations and three full file queries. Existing entities
must be reused. The fresh byte-budget corpora contain 8 MiB and 64 MiB of noncompressed payload
and cross the checksum reader's buffer boundary. Every published entry is
read back and compared with its original bytes.

A same-size archive mutation with its timestamp preserved, injected after
publication, must fail before destination state is committed and retain recovery
data. This prevents substituting length/timestamp guesses for integrity checks.

CI runs these guards within the full Windows, Linux and macOS suites and parses
the TRX to reject missing, skipped or failed cases. This is an algorithmic guard,
not a guarantee against all latency regressions: network throughput, locks,
compression, filesystem metadata and uninstrumented I/O require representative
storage measurements before deployment.

## Recorded regression evidence

The initial eight-case Windows run failed six cases against the previous
implementation with measurement hooks added. After repair:

| Measured work | Before | After |
| --- | ---: | ---: |
| Published recovery, 512 files: file lookups and snapshot entries | 393,472 | 1,024 |
| Unfinished recovery, 512 files: file lookups and snapshot entries | 69,120 | 1,536 |
| Fresh backup, 64 parts: part snapshot entries | 20,480 | 64 |
| Publication: full archive checksum passes | 4 | 2 |

The full Windows suite passed 245 backup tests and 44 framework tests, with three
filesystem/integration capability skips. The full WSL Ubuntu suite passed 268
backup tests and 44 framework tests, with six capability skips. All eight
performance guard cases passed on both. The CI result gate was also exercised
with synthetic missing and skipped guard results; both were rejected.

These are local Windows and WSL results. Native macOS CI has not run for this
change. Subsequent NAS release validation is recorded below.

## Shared-drive release validation (2026-10-05)

The Windows x64 framework-dependent release was executed from the actual NAS.
The generated corpus contained 6,008 files, 538,886,912 source bytes (513.9 MiB)
and two published archive parts. Backup elapsed time, including executable
startup, was 97.53 seconds. Measured pipeline times:

| Stage | Seconds |
| --- | ---: |
| Planning | 0.478 |
| Capture and transfer | 44.275 |
| Publication and state commit | 35.149 |

Archive checksum logging recorded one local pass and three destination passes.
The candidate restored all 6,008 filenames, lengths and SHA-256 hashes correctly.
The real NAS mapped-drive/UNC interruption-resume test also executed and passed.

The verified five-file runtime set was installed in the scheduled program
directory. Installed checksums and startup from its UNC working directory passed.
The previous runtime and validation reports were retained in the user's local
deployment records. This is a scaled NAS corpus; full Documents/Music production
timings and native macOS CI remain unmeasured for this change.

## Publication and baseline verification boundaries

Operation-driven publication is provisional until destination-state commit.
Transfer flushes the destination copy and reads its actual bytes before recording
transfer acknowledgement. Publication validates receipt identities, index bytes
and archive lengths, writes the manifest and publication receipt, renames the
working directory, and persists the published-job marker. It does not scan the
archive payload at this intermediate boundary. The existing state-commit stage
then reads every final archive, compares the observed digest with manifest and
checkpoint expectations, and validates the ZIP inventory before writing baseline
state or emitting terminal success. Transfer receipts describe expected content;
they never replace this postpublication observation. Legacy direct publication
callers have no state-commit stage and retain their publication scan.

An interruption before the receipt leaves ordinary transferred-part recovery.
After the publication receipt, rename, or job marker, startup recovery validates
actual destination bytes without reading source content, completes any pending
rename/marker, and reconstructs the operation. State commit revalidates bytes at
its subsequent boundary. No observation is reused across restart or the durable
published-job marker. Corruption after transfer or after that marker leaves state
uncommitted and recovery artifacts intact, including when file length and mtime
are unchanged. Restore remains protected by the destination operation lock and
its own content checks; a final directory name alone does not authorize baseline
skip decisions.

Baseline loading now uses the same buffered checksum reader and measurement hook
as the pipeline, with progress logging in the CLI. Each load validates committed
manifest evidence and referenced index bytes. A lookup built once per publication
selects parts whose relative descriptors still supply current baseline hashes.
Only those contributing parts are scanned, once each at that load boundary.
Superseded parts provide no skip-capture authority. A second load repeats actual
byte validation; it does not trust cached hashes, lengths or timestamps. Legacy
indexes without portable relative paths still force fresh capture.

The TRX gate now requires 18 cases: fresh byte/read budgets and lookup work at
64/512 files, four interrupted lookup cases, six full interrupted-publication
cases, two unchanged/incremental baseline cases, and two same-size corruption
cases. Unchanged runs allow one baseline scan and no new archive or state write.
Incremental runs allow one baseline scan plus two passes over new archive bytes.
Loading after an incremental replaces a complete part permits reads of exactly
current contributing parts. Interrupted publication permits one startup scan and
one later commit scan, with zero local recapture reads.

## Further NAS verification reduction (2026-10-05)

The operation-driven protocol above removes the prepublication payload scan while
retaining the transfer scan and the actual-byte scan after published-job-committed.
At 64/512 files the final fresh guards measured archive lengths of 8,395,312 and
67,162,496 bytes: local reads were exactly those lengths, publication reads were
exactly those lengths, and destination reads were exactly twice those lengths.
Baseline reads include the previously uninstrumented load path. The incremental
baseline guard excluded a 66,374-byte superseded part at both sizes and repeated
all contributing reads on a subsequent load. No budgets were raised.

Final full-suite results: Windows passed 255 backup tests and 44 framework tests
with three capability skips; native WSL Ubuntu passed 278 backup tests and 44
framework tests with six capability skips. All 18 deterministic performance cases
passed and their final TRX files passed eng/Assert-PerformanceGuards.ps1 on both.
Five actual NAS integration cases passed without skips, including kill/restart
through mapped-drive and UNC aliases with the original run GUID and captured
index/content bytes. Native macOS CI has not run for this candidate.

A generated local source was backed up to isolated directories on the actual
\\Ax8_max\e share, running both runtimes from that share. It contained 6,008
files and 538,886,912 source bytes (513.9 MiB), including exact-case and Unicode
names. Each fresh run wrote two parts totaling 537,807,893 archive bytes.
The comparison used the previous deployed performance runtime and the candidate,
not the older main-branch binaries. Measured pipeline stages:

| Workload / stage | Previous runtime (s) | Candidate (s) |
| --- | ---: | ---: |
| Fresh: capture and transfer | 44.822 | 42.477 |
| Fresh: publication and state commit | 30.970 | 17.717 |
| Fresh: total including startup and cleanup | 77.383 | 62.763 |
| Unchanged: baseline loading | 20.794 | 16.766 |
| Unchanged: total | 23.043 | 19.048 |
| Incremental: baseline loading | 10.704 | 10.370 |
| Incremental: capture and transfer | 6.090 | 5.404 |
| Incremental: publication and state commit | 5.783 | 5.094 |
| Incremental: total | 38.874 | 23.127 |

Fresh checksum logs showed one local pass and three destination passes before,
one local pass and two destination passes after: a full 537,807,893-byte NAS
checksum pass eliminated. Fresh elapsed time improved by 18.9% and publication
by 42.8% in this pair. Unchanged candidate logs showed one baseline pass over
the two parts, no capture reads, and no new set. Previous baseline reads were
uninstrumented and must not be interpreted as zero reads.

The incremental corpus changed 17 files, including one 64 MiB payload. Both
runtimes wrote one 67,111,793-byte archive. Candidate logs include one baseline
pass plus two passes over the new archive. The previous incremental run had a
separate 14.2-second session/setup delay before baseline loading, versus 0.2
seconds in the candidate. Most of that elapsed-time difference cannot be
attributed to this patch. These are single paired runs, sensitive to NAS caching,
metadata, locking and throughput; capture-stage variation is also not evidence
of a capture algorithm change.

Fresh restore verified all 6,008 exact filenames, lengths and SHA-256 hashes.
The existing restore command selects a version, so the incremental version's 17
entries were first restored and verified separately. Applying that version to the
restored base then verified all 6,008 final files and 538,887,376 bytes. Every
manifest matched its destination commit evidence, no issues were reported, and
both run tags, staging directories, initialization records and completion records
were removed. The generated NAS fixture and large local corpus were cleaned up
after retaining evidence; production backup data and recovery state were untouched.

At the end of validation, the scheduled runtime still used the previous five-file set;
the candidate had not yet been deployed or committed. The 8.45 GB Music full backup has
not been rerun with the candidate. The prior reported Music timings (10m 59s
capture/transfer, 7m 50s publication/commit, 19m 07s total) are baseline evidence,
not candidate measurements. A new full Music run and native macOS CI remain
unmeasured. Validation evidence, pre-edit snapshots and candidate runtime are at:

C:\Users\John Bruce\AppData\Local\DifferentialBackup\ProgramDeployments\nas-read-pass-20261005

## Requested manual-test deployment (2026-10-05)

At 23:21 America/Chicago, the user-requested candidate was installed in
D:\DifferentialBackup Program, resolving to
\\Ax8_max\e\DifferentialBackup Program. No backup executable was running and
the scheduled task was Ready. Both final performance TRX gates were revalidated
before installation. Only the five runtime files were replaced, with the
executable locked first and released last. Installed SHA-256 hashes match the
NAS-tested candidate; startup with help succeeded from both mapped-drive and UNC
working directories, each with exit code zero.

The previous five runtime files were retained and their rollback hashes verified:

C:\Users\John Bruce\AppData\Local\DifferentialBackup\ProgramDeployments\nas-read-pass-20261005\rollback

Deployment details and startup logs are retained alongside the validation evidence
in deployment.json and evidence/installed-help-mapped.log and
installed-help-unc.log. The scheduled task points to the same installed directory
and will use the candidate. Its schedule, arguments and priority were unchanged.
No manual backup was started, and no production backup data or recovery state was
modified. A full Music candidate timing and native macOS CI remain unmeasured.
