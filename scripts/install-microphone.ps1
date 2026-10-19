$ErrorActionPreference = 'Stop'

# Locate VBCABLE_Setup_x64.exe in local directory or vbcable subfolder
$candidates = @(
    (Join-Path $PSScriptRoot 'vbcable\VBCABLE_Setup_x64.exe'),
    (Join-Path $PSScriptRoot '..\windows\vbcable\VBCABLE_Setup_x64.exe'),
    (Join-Path $PSScriptRoot 'VBCABLE_Setup_x64.exe')
)

$installer = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $installer) {
    throw "VB-Cable installer (VBCABLE_Setup_x64.exe) not found."
}

$workingDir = Split-Path -Parent $installer

# Run silent installation with -i (install) -h (hide / silent)
$process = Start-Process -FilePath $installer -ArgumentList @('-i', '-h') -WorkingDirectory $workingDir -Wait -PassThru -WindowStyle Hidden

# Exit codes: 0 = success, or standard installer return
Write-Host "VB-Cable installer exited with code: $($process.ExitCode)"

# Restart Windows Audio endpoint builder to immediately enumerate new endpoints if needed
try {
    Restart-Service -Name "AudioSrv" -Force -ErrorAction SilentlyContinue
} catch {}

Write-Host "Virtual microphone installation complete."
