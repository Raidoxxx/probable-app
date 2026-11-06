# Gera o pacote do servidor para a Discloud: deploy/pcboost-discloud.zip
# Uso:  powershell -ExecutionPolicy Bypass -File scripts/deploy-discloud.ps1
# Depois envie o .zip pelo painel da Discloud (ou: discloud upload / discloud commit).
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$stage = $root
$server = Join-Path $stage "server"
$zip = Join-Path $root "deploy\pcboost-discloud.zip"

$expectedServer = [System.IO.Path]::GetFullPath((Join-Path $root "server"))
if ([System.IO.Path]::GetFullPath($server) -ne $expectedServer -or (Split-Path $expectedServer -Parent) -ne $root) {
    throw "Pasta de publicação fora da raiz do projeto."
}
if (Test-Path -LiteralPath $server) { Remove-Item -LiteralPath $server -Recurse -Force }
# Executável Linux autocontido: a Discloud não precisa ter .NET instalado.
dotnet publish (Join-Path $root "apps\server\PCBoost.Server.csproj") -c Release -r linux-x64 --self-contained true `
    -p:InvariantGlobalization=true -p:DebugType=none -p:GenerateDocumentationFile=false -o $server
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou." }

# Zip com separador "/" (o Compress-Archive do Windows PowerShell grava "\" e quebra no Linux).
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path (Split-Path $zip -Parent) -Force | Out-Null
if (Test-Path $zip) { Remove-Item $zip }
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $files = @("discloud.config", "start.py", "requirements.txt") | ForEach-Object { Get-Item (Join-Path $stage $_) }
    $files += Get-ChildItem -Recurse -File $server
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($stage.Length + 1).Replace("\", "/")
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
"Pacote pronto: $zip ({0:N1} MB)" -f ((Get-Item $zip).Length / 1MB)
