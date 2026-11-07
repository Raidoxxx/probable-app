# Gera installer/Ximbuismo-Setup.exe já apontando para o servidor online.
# Uso:  powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1 [-ServerUrl https://seu-servidor] [-Version 1.2.3]
param(
    [string]$ServerUrl = "https://probable-app.discloud.dev",
    [string]$Version = "",   # vazio = usa <Version> do Directory.Build.props
    [string]$UpdateRepo = "Raidoxxx/probable-app",
    [string]$UpdateSignerSha256 = "",
    [string]$UpdateManifestPublicKey = ""
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$out = Join-Path $root "installer"
if ([IO.Path]::GetFullPath($out) -ne (Join-Path $root 'installer')) { throw 'Destino do instalador inválido.' }
if ((Test-Path -LiteralPath $out) -and ((Get-Item -LiteralPath $out).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'A pasta installer não pode ser um link.' }
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
$extra = @()
if ($Version) { $extra += "-p:Version=$Version" }
if ($UpdateSignerSha256) { $extra += "-p:UpdateSignerSha256=$UpdateSignerSha256" }
if ($UpdateManifestPublicKey) { $extra += "-p:UpdateManifestPublicKey=$UpdateManifestPublicKey" }
# Executável único com .NET embutido: o usuário não instala nada além do Ximbuismo.
dotnet publish (Join-Path $root "apps\desktop\PCBoost.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:TeamServerUrl=$ServerUrl -p:UpdateRepo=$UpdateRepo @extra -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou." }
Move-Item (Join-Path $out "Ximbuismo.exe") (Join-Path $out "Ximbuismo-Setup.exe") -Force
"Instalador pronto: $out\Ximbuismo-Setup.exe (servidor: $ServerUrl)"
