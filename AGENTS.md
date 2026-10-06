# Repository development instructions

Backup performance is a correctness requirement. Changes to capture, transfer,
publication, recovery, or state commit must run the full solution tests and the
deterministic performance guards in
DifferentialBackup.Test/Systems/BackupPerformanceRegressionTests.cs.

Before publishing or deploying such changes, validate the TRX with
eng/Assert-PerformanceGuards.ps1. Missing, skipped, or failed guard cases are
failures. Do not raise operation/read budgets to make a regression pass; first
explain the algorithm and measure its work at both tested input sizes.

Build lookups once outside file/part loops. Count query snapshot entries as well
as component accesses. Archive verification must have an explicit read budget.
Reuse a checksum only within the boundary where those bytes were observed;
restart and subsequent durability boundaries must still validate actual bytes.
Keep corruption, crash-recovery, exact-name, and round-trip content tests passing.

These deterministic guards catch excessive measured work, not every source of
latency. Before deploying performance-related changes, measure a representative
backup on the target storage and record payload size, part/file counts and stage
times. Do not claim a NAS or operating system was tested based on local unit tests.
