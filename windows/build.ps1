# Compila o app e gera o instalador.  Uso:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$dotnet = "$env:ProgramFiles\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
if (Test-Path publish) { Remove-Item publish -Recurse -Force }
& $dotnet publish WiFiTransfer\WiFiTransfer.csproj -c Release -o publish
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação' }
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup (ISCC.exe) não encontrado' }
& $iscc installer.iss
if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar o instalador' }
Get-ChildItem output
