# Gera installer/PCBoost-Setup.exe já apontando para o servidor online.
# Uso:  powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1 [-ServerUrl https://seu-servidor]
param([string]$ServerUrl = "https://probable-app.discloud.app")
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$out = Join-Path $root "installer"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
# Executável único com .NET embutido: o usuário não instala nada além do PCBoost.
dotnet publish (Join-Path $root "apps\desktop\PCBoost.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:TeamServerUrl=$ServerUrl -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou." }
Move-Item (Join-Path $out "PCBoost.exe") (Join-Path $out "PCBoost-Setup.exe") -Force
"Instalador pronto: $out\PCBoost-Setup.exe (servidor: $ServerUrl)"
