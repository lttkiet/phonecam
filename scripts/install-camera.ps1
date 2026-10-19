$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'PhonecamCameraSource.dll'
if (-not (Test-Path $source)) { throw "Camera source missing: $source" }

$targetDirectory = Join-Path $env:ProgramFiles 'Phonecam'
$hash = (Get-FileHash $source -Algorithm SHA256).Hash.Substring(0, 12).ToLowerInvariant()
$target = Join-Path $targetDirectory "PhonecamCameraSource-$hash.dll"
New-Item $targetDirectory -ItemType Directory -Force | Out-Null
if (-not (Test-Path $target)) { Copy-Item $source $target }

$regsvr = Join-Path $env:SystemRoot 'System32\regsvr32.exe'
$registration = Start-Process -FilePath $regsvr -ArgumentList @('/s', "`"$target`"") -WindowStyle Hidden -PassThru -Wait
if ($registration.ExitCode -ne 0) { throw "Media Foundation camera registration failed with exit code $($registration.ExitCode)." }

$clsid = 'HKLM:\SOFTWARE\Classes\CLSID\{f15889e2-7b53-4ee1-bad4-9e7c51a94d60}\InprocServer32'
if (-not (Test-Path $clsid)) { throw 'Phonecam camera registration was not found after installation.' }
if ((Get-Item $clsid).GetValue('') -ne $target) { throw 'Phonecam camera registration points to a different source DLL.' }
