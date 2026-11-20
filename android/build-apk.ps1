# Compila o APK. Uso: powershell -ExecutionPolicy Bypass -File build-apk.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$sdk = "$env:LOCALAPPDATA\Android\Sdk"
"sdk.dir=$($sdk -replace '\\','\\')" | Out-File -Encoding ascii local.properties
if (-not $env:JAVA_HOME -or -not (Test-Path "$env:JAVA_HOME\bin\java.exe")) {
  $jdk = Get-ChildItem "$env:USERPROFILE\.jdks" -Directory | Where-Object { $_.Name -match 'jdk-17|jbr-21' } | Select-Object -First 1
  if ($jdk) { $env:JAVA_HOME = $jdk.FullName }
}
.\gradlew.bat assembleRelease --no-daemon
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação do APK' }
New-Item -ItemType Directory -Force output | Out-Null
Copy-Item app\build\outputs\apk\release\app-release.apk output\LANTransfer-5.0.0.apk -Force
Get-ChildItem output
