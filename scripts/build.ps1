$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $root '.nuget\packages'
$javaCandidates = @($env:JAVA_HOME) + @(Get-ChildItem (Join-Path $env:ProgramFiles 'Java') -Directory -Filter 'jdk*' -ErrorAction SilentlyContinue | Sort-Object Name -Descending | ForEach-Object FullName)
$env:JAVA_HOME = $javaCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ 'bin\javac.exe')) } | Select-Object -First 1
if (-not $env:JAVA_HOME) { throw 'A Java JDK is required.' }
$env:PATH = (Join-Path $env:JAVA_HOME 'bin') + ';' + $env:PATH
$sdkCandidates = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, (Join-Path $env:LOCALAPPDATA 'Android\Sdk'))
$sdk = $sdkCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ 'platforms')) } | Select-Object -First 1
if (-not $sdk) { throw 'An Android SDK is required.' }
$env:ANDROID_HOME = $sdk
$tools = Get-ChildItem (Join-Path $sdk 'build-tools') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
$platform = Get-ChildItem (Join-Path $sdk 'platforms') -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'android.jar') } | Sort-Object { [version](($_.Name -replace '^android-', '') -replace '^(\d+)$', '$1.0') } -Descending | Select-Object -First 1
$androidJar = Join-Path $platform.FullName 'android.jar'
$build = Join-Path $root 'android\build'
$artifacts = Join-Path $root 'artifacts'
$windowsOutput = Join-Path $artifacts 'windows'

Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
New-Item $build -ItemType Directory -Force | Out-Null
New-Item $artifacts -ItemType Directory -Force | Out-Null
New-Item $windowsOutput -ItemType Directory -Force | Out-Null

dotnet restore (Join-Path $root 'windows\Phonecam.csproj') --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'Windows receiver restore failed.' }
dotnet build (Join-Path $root 'windows\Phonecam.csproj') -c Release --no-restore -o $windowsOutput
if ($LASTEXITCODE -ne 0) { throw 'Windows receiver build failed.' }

$cameraSource = Join-Path $root 'windows\CameraSource\PhonecamCameraSource.csproj'
$cameraHost = Join-Path $root 'windows\CameraHost\PhonecamCameraHost.csproj'
dotnet restore $cameraSource -r win-x64 --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'Windows camera source restore failed.' }
dotnet publish $cameraSource -c Release -r win-x64 --no-restore -p:Platform=x64 -o $windowsOutput
if ($LASTEXITCODE -ne 0) { throw 'Windows camera source publish failed.' }
dotnet restore $cameraHost -r win-x64 --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'Windows camera host restore failed.' }
dotnet publish $cameraHost -c Release -r win-x64 --no-restore -p:Platform=x64 -o $windowsOutput
if ($LASTEXITCODE -ne 0) { throw 'Windows camera host publish failed.' }
Copy-Item (Join-Path $root 'scripts\install-camera.ps1') (Join-Path $windowsOutput 'install-camera.ps1') -Force
Copy-Item (Join-Path $root 'scripts\install-microphone.ps1') (Join-Path $windowsOutput 'install-microphone.ps1') -Force
if (Test-Path (Join-Path $root 'windows\vbcable')) {
    Copy-Item (Join-Path $root 'windows\vbcable') (Join-Path $windowsOutput 'vbcable') -Recurse -Force
}
Copy-Item (Join-Path $root 'windows\CameraSource\VCamNetSample-LICENSE.txt') (Join-Path $windowsOutput 'PhonecamCameraSource-LICENSE.txt') -Force

$classes = Join-Path $build 'classes'
New-Item $classes -ItemType Directory -Force | Out-Null
$javaFiles = Get-ChildItem (Join-Path $root 'android\src') -Filter '*.java' -Recurse | ForEach-Object FullName
    & (Join-Path $env:JAVA_HOME 'bin\javac.exe') --release 17 -encoding UTF-8 -classpath $androidJar -d $classes $javaFiles
if ($LASTEXITCODE -ne 0) { throw 'Android Java compile failed.' }

$baseApk = Join-Path $build 'base.apk'
& (Join-Path $tools 'aapt2.exe') link -o $baseApk -I $androidJar --manifest (Join-Path $root 'android\AndroidManifest.xml') --min-sdk-version 23 --target-sdk-version 35 --version-code 1 --version-name 1.0
if ($LASTEXITCODE -ne 0) { throw 'Android resource link failed.' }

Push-Location $classes
try {
    & (Join-Path $tools 'd8.bat') --release --min-api 23 --lib $androidJar --output $build (Get-ChildItem $classes -Filter '*.class' -Recurse | ForEach-Object FullName)
    if ($LASTEXITCODE -ne 0) { throw 'Android dex conversion failed.' }
} finally { Pop-Location }

Push-Location $build
try {
    & (Join-Path $env:JAVA_HOME 'bin\jar.exe') --update --file $baseApk classes.dex
    if ($LASTEXITCODE -ne 0) { throw 'Android APK assembly failed.' }
} finally { Pop-Location }

$aligned = Join-Path $build 'aligned.apk'
& (Join-Path $tools 'zipalign.exe') -f -p 4 $baseApk $aligned
if ($LASTEXITCODE -ne 0) { throw 'Android APK alignment failed.' }

$key = Join-Path $artifacts 'phonecam-debug.keystore'
if (-not (Test-Path $key)) {
    & (Join-Path $env:JAVA_HOME 'bin\keytool.exe') -genkeypair -keystore $key -storepass android -keypass android -alias androiddebugkey -keyalg RSA -keysize 2048 -validity 10000 -dname 'CN=Android Debug,O=Android,C=US' -noprompt
    if ($LASTEXITCODE -ne 0) { throw 'Android signing key creation failed.' }
}

$apk = Join-Path $artifacts 'phonecam.apk'
& (Join-Path $tools 'apksigner.bat') sign --ks $key --ks-key-alias androiddebugkey --ks-pass pass:android --key-pass pass:android --out $apk $aligned
if ($LASTEXITCODE -ne 0) { throw 'Android APK signing failed.' }
& (Join-Path $tools 'apksigner.bat') verify --verbose $apk
if ($LASTEXITCODE -ne 0) { throw 'Android APK signature verification failed.' }
