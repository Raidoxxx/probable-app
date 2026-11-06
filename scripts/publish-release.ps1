# Gera o instalador e publica como release no GitHub; o botão "Baixar" do site aponta para
#   https://github.com/Raidoxxx/probable-app/releases/latest/download/Ximbuismo-Setup.exe
# Requer o GitHub CLI autenticado (gh auth login). O repositório precisa ser público para
# que qualquer pessoa consiga baixar o anexo.
# Uso:  powershell -ExecutionPolicy Bypass -File scripts/publish-release.ps1 [-ServerUrl https://...] [-Repo dono/repo]
param(
    [string]$ServerUrl = "https://probable-app.discloud.dev",
    [string]$Repo = "Raidoxxx/probable-app"
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw "Version não encontrada em Directory.Build.props." }
$tag = "v$version"

& (Join-Path $PSScriptRoot "build-installer.ps1") -ServerUrl $ServerUrl
$exe = Join-Path $root "installer\Ximbuismo-Setup.exe"

gh release view $tag --repo $Repo *> $null
if ($LASTEXITCODE -eq 0) {
    # Mesma versão: troca o anexo. Para uma versão nova, aumente <Version> no Directory.Build.props.
    gh release upload $tag $exe --repo $Repo --clobber
} else {
    gh release create $tag $exe --repo $Repo --title "Ximbuismo $version" --notes "Instalador do Ximbuismo para Windows 10/11 (64 bits). Servidor: $ServerUrl"
}
if ($LASTEXITCODE -ne 0) { throw "gh release falhou." }
"Release $tag publicada em $Repo."
