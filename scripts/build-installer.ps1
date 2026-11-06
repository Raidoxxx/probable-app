# Gera installer/Ximbuismo-Setup.exe já apontando para o servidor online.
# Uso:  powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1 [-ServerUrl https://seu-servidor] [-Version 1.2.3]
param(
    [string]$ServerUrl = "https://probable-app.discloud.app",
    [string]$Version = ""   # vazio = usa <Version> do Directory.Build.props
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$out = Join-Path $root "installer"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
$extra = @()
if ($Version) { $extra += "-p:Version=$Version" }
# Executável único com .NET embutido: o usuário não instala nada além do Ximbuismo.
dotnet publish (Join-Path $root "apps\desktop\PCBoost.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:TeamServerUrl=$ServerUrl @extra -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou." }
Move-Item (Join-Path $out "Ximbuismo.exe") (Join-Path $out "Ximbuismo-Setup.exe") -Force
"Instalador pronto: $out\Ximbuismo-Setup.exe (servidor: $ServerUrl)"
