param(
    [string]$CapturePath = "C:\Users\lttki\.gemini\antigravity\brain\f3cb8782-0657-4033-b2cd-591a03387c1a\pc_window.png"
)

$env:PHONECAM_CAPTURE_PATH = $CapturePath
Write-Host "Set PHONECAM_CAPTURE_PATH to $CapturePath"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "dotnet"
$psi.Arguments = "C:\Users\lttki\Documents\ChatGPT\phonecam\artifacts\windows\Phonecam.dll"
$psi.UseShellExecute = $false
$psi.EnvironmentVariables["PHONECAM_CAPTURE_PATH"] = $CapturePath

$process = [System.Diagnostics.Process]::Start($psi)
Write-Host "Started Phonecam with PID $($process.Id)"

Start-Sleep -Seconds 4

if (-not $process.HasExited) {
    Write-Host "Stopping Phonecam process"
    $process.Kill()
}

$exists = Test-Path $CapturePath
Write-Host "Capture result: $exists"
