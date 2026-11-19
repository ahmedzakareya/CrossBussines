# Registers the CrossBuy AI service to start when this user logs on (owner, 2026-11-19). Idempotent: run it again to
# update the task. Remove with:  Unregister-ScheduledTask -TaskName 'CrossBuy AI service' -Confirm:$false
$launcher = Join-Path $PSScriptRoot 'run_ai_service.ps1'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$launcher`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName 'CrossBuy AI service' -Action $action -Trigger $trigger -Settings $settings `
    -Description 'Keeps crossbuy_ai (speech to text, call summaries) running on 127.0.0.1:8000.' -Force | Out-Null
Get-ScheduledTask -TaskName 'CrossBuy AI service' | Select-Object TaskName, State
