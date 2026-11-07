#Requires -Version 7.4
# Segredos são fornecidos apenas pelo ambiente do runner.
param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$ServerUrl, [Parameter(Mandatory)][string]$Repository)
$ErrorActionPreference = 'Stop'
foreach ($key in @('UPDATE_CERTIFICATE_PFX', 'UPDATE_CERTIFICATE_PASSWORD', 'UPDATE_MANIFEST_PRIVATE_KEY')) {
    if (-not [Environment]::GetEnvironmentVariable($key)) { throw "Segredo obrigatório ausente: $key" }
}
$releaseRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pfxPath = Join-Path ([IO.Path]::GetTempPath()) ('ximbuismo-sign-' + [Guid]::NewGuid().ToString('N') + '.pfx')
$certificate = $null
$certificateWasPresent = $false
$rsa = $null
# O helper C# evita chamadas de métodos com ReadOnlySpan pelo binder do PowerShell.
if (-not ('Ximbuismo.ReleaseSigner' -as [type])) {
    Add-Type -TypeDefinition @'
using System.Security.Cryptography;
namespace Ximbuismo {
    public static class ReleaseSigner {
        public static RSA Load(string pem) {
            var rsa = RSA.Create();
            try { rsa.ImportFromPem(pem); return rsa; }
            catch { rsa.Dispose(); throw; }
        }
        public static byte[] Sign(RSA rsa, byte[] data) {
            return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
    }
}
'@
}
try {
    [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($env:UPDATE_CERTIFICATE_PFX))
    $password = ConvertTo-SecureString $env:UPDATE_CERTIFICATE_PASSWORD -AsPlainText -Force
    $preview = [Security.Cryptography.X509Certificates.X509Certificate2]::new($pfxPath, $env:UPDATE_CERTIFICATE_PASSWORD, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    try { $certificateWasPresent = Test-Path -LiteralPath ("Cert:\CurrentUser\My\" + $preview.Thumbprint) } finally { $preview.Dispose() }
    $certificate = Import-PfxCertificate -FilePath $pfxPath -CertStoreLocation Cert:\CurrentUser\My -Password $password
    if (-not $certificate.HasPrivateKey) { throw 'O certificado não possui chave privada.' }
    $pin = $certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)
    $rsa = [Ximbuismo.ReleaseSigner]::Load($env:UPDATE_MANIFEST_PRIVATE_KEY)
    $publicKey = [Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo())
    & (Join-Path $PSScriptRoot 'build-installer.ps1') -Version $Version -ServerUrl $ServerUrl -UpdateRepo $Repository -UpdateSignerSha256 $pin -UpdateManifestPublicKey $publicKey
    $setup = Join-Path $releaseRoot 'installer/Ximbuismo-Setup.exe'
    $signed = Set-AuthenticodeSignature -LiteralPath $setup -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
    if ($signed.Status -ne 'Valid') { throw "Assinatura do instalador inválida: $($signed.Status)" }
    $manifest = [ordered]@{
        Version = $Version
        DownloadUrl = "https://github.com/$Repository/releases/download/v$Version/Ximbuismo-Setup.exe"
        Size = (Get-Item -LiteralPath $setup).Length
        Sha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    } | ConvertTo-Json -Compress
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($manifest)
    [IO.File]::WriteAllBytes((Join-Path $releaseRoot 'installer/update-manifest.json'), $bytes)
    $signature = [Ximbuismo.ReleaseSigner]::Sign($rsa, $bytes)
    [IO.File]::WriteAllBytes((Join-Path $releaseRoot 'installer/update-manifest.sig'), $signature)
}
finally {
    if ($rsa) { $rsa.Dispose() }
    if ($certificate -and -not $certificateWasPresent) { Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $certificate.Thumbprint) -Force }
    if (Test-Path -LiteralPath $pfxPath) { Remove-Item -LiteralPath $pfxPath -Force }
}
