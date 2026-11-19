# CrossBuy AI service — keep it running (owner, 2026-11-19).
#
# Voice-message transcription and call summaries live in this service; when it is down they answer "not available".
# A reboot used to stop it until someone started it by hand. This launcher is what the Windows logon task runs
# (tools\install_ai_autostart.ps1): it starts uvicorn from the project's own virtual environment, on loopback only,
# and starts it again if it ever exits (a crash must not leave transcription off until the next reboot).
#
# It refuses to start a second copy: if something already answers /health on the port, it waits and checks again.
param([int]$Port = 8000)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$python = Join-Path $root '.venv\Scripts\python.exe'
$logDir = Join-Path $root 'data\logs'
New-Item -ItemType Directory -Force $logDir | Out-Null

function Test-Up {
    try { (Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 "http://127.0.0.1:$Port/health").StatusCode -eq 200 } catch { $false }
}

while ($true) {
    if (Test-Up) { Start-Sleep -Seconds 60; continue }
    $log = Join-Path $logDir ("ai-" + (Get-Date -Format 'yyyyMMdd') + '.log')
    Add-Content $log ("[" + (Get-Date -Format s) + "] starting uvicorn on 127.0.0.1:$Port")
    $p = Start-Process -FilePath $python -ArgumentList '-m', 'uvicorn', 'app.main:app', '--host', '127.0.0.1', '--port', "$Port" `
        -WorkingDirectory $root -RedirectStandardOutput "$log.out" -RedirectStandardError "$log.err" -WindowStyle Hidden -PassThru
    $p.WaitForExit()
    Add-Content $log ("[" + (Get-Date -Format s) + "] uvicorn exited with " + $p.ExitCode + "; restarting in 10 s")
    Start-Sleep -Seconds 10
}
