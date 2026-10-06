# Update-BackupTasks.ps1
# Updates the scheduled tasks executable path to the network path:
# Old: D:\DifferentialBackup Program\DifferentialBackup.exe
# New: \\Ax8_max\e\DifferentialBackup Program\DifferentialBackup.exe

$logFile = "$env:TEMP\update_backup_tasks.log"
"--- Update Backup Tasks Started at $(Get-Date) ---" | Out-File -FilePath $logFile -Encoding utf8

$oldExe = "D:\DifferentialBackup Program\DifferentialBackup.exe"
$newExe = "\\Ax8_max\e\DifferentialBackup Program\DifferentialBackup.exe"

Write-Host "Scanning all scheduled tasks..."
"Scanning all scheduled tasks..." | Out-File -FilePath $logFile -Append -Encoding utf8

$allTasks = Get-ScheduledTask
$matched = @()

foreach ($t in $allTasks) {
    $isMatch = $false
    foreach ($a in $t.Actions) {
        $exec = "$($a.Execute)"
        $args = "$($a.Arguments)"
        if ($exec -like "*$oldExe*" -or $args -like "*$oldExe*" -or
            $exec -match "DifferentialBackup\.exe" -or $args -match "DifferentialBackup\.exe") {
            $isMatch = $true
        }
    }
    if ($isMatch) {
        $matched += $t
    }
}

Write-Host "Found $($matched.Count) task(s) matching DifferentialBackup.exe"
"Found $($matched.Count) task(s) matching DifferentialBackup.exe" | Out-File -FilePath $logFile -Append -Encoding utf8

foreach ($task in $matched) {
    $info = "Task: '$($task.TaskPath)$($task.TaskName)'"
    Write-Host $info
    $info | Out-File -FilePath $logFile -Append -Encoding utf8

    $newActions = @()
    foreach ($action in $task.Actions) {
        $oldExec = $action.Execute
        $oldArgs = $action.Arguments
        $oldWorkDir = $action.WorkingDirectory

        $updatedExec = $oldExec
        $updatedArgs = $oldArgs
        $updatedWorkDir = $oldWorkDir

        if ($updatedExec -and ($updatedExec -like "*$oldExe*")) {
            $updatedExec = $updatedExec -replace [regex]::Escape($oldExe), $newExe
        }

        if ($updatedArgs -and ($updatedArgs -like "*$oldExe*")) {
            $updatedArgs = $updatedArgs -replace [regex]::Escape($oldExe), $newExe
        }

        if ($oldWorkDir -and ($oldWorkDir -eq "D:\DifferentialBackup Program")) {
            $updatedWorkDir = "\\Ax8_max\e\DifferentialBackup Program"
        }

        $summary = @"
  Task: $($task.TaskName)
  Execute BEFORE: $oldExec
  Execute AFTER:  $updatedExec
  Arguments:      $updatedArgs
"@
        Write-Host $summary
        $summary | Out-File -FilePath $logFile -Append -Encoding utf8

        $newAction = New-ScheduledTaskAction -Execute $updatedExec -Argument $updatedArgs -WorkingDirectory $updatedWorkDir
        $newActions += $newAction
    }

    try {
        Set-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -Action $newActions -ErrorAction Stop | Out-Null
        $successMsg = "  [SUCCESS] Updated '$($task.TaskName)' successfully."
        Write-Host $successMsg -ForegroundColor Green
        $successMsg | Out-File -FilePath $logFile -Append -Encoding utf8
    } catch {
        $failMsg = "  [FAILED] Could not update '$($task.TaskName)': $($_.Exception.Message)"
        Write-Host $failMsg -ForegroundColor Red
        $failMsg | Out-File -FilePath $logFile -Append -Encoding utf8
    }
}

"--- Completed at $(Get-Date) ---" | Out-File -FilePath $logFile -Append -Encoding utf8
Write-Host "Completed. Output written to $logFile"
