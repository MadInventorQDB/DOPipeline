param(
    [string]$ResultsPath = (Join-Path $PSScriptRoot '../DifferentialBackup.Test/TestResults/platform.trx')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ResultsPath -PathType Leaf)) {
    throw "Performance guard results are missing: $ResultsPath"
}
[xml]$report = Get-Content -LiteralPath $ResultsPath -Raw
$required = @{
    PublicationAndTransferHaveBoundedArchiveReadAndStateWriteBudgets = 2
    FreshBackupLookupWorkStaysWithinLinearBudgets = 2
    InterruptedRecoveryLookupWorkStaysWithinLinearBudgets = 4
    SameSizeCorruptionAfterPublicationCannotCommitDestinationState = 2
    InterruptedPublicationReadsActualBytesAtRecoveryAndCommit = 6
    BaselineAndIncrementalReadsOnlyContributingPartsOncePerBoundary = 2
}
$definitions = @($report.TestRun.TestDefinitions.UnitTest | Where-Object {
    $_.TestMethod.className -eq 'DifferentialBackup.Test.Systems.BackupPerformanceRegressionTests'
})
$results = @{}
foreach ($result in $report.TestRun.Results.UnitTestResult) {
    $results[$result.testId] = $result
}
foreach ($method in $required.Keys) {
    $cases = @($definitions | Where-Object { $_.TestMethod.name -eq $method })
    if ($cases.Count -lt $required[$method]) {
        throw "Performance guard '$method' was not fully discovered: $($cases.Count) of $($required[$method]) cases."
    }
}
foreach ($test in $definitions) {
    $result = $results[$test.id]
    if ($null -eq $result -or $result.outcome -ne 'Passed') {
        throw "Performance guard did not pass: $($test.name). Missing, skipped and failed cases all block CI."
    }
}
Write-Host "Verified $($definitions.Count) discovered, executed, passing performance guard cases."
