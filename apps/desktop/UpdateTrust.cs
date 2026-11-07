using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace PCBoost;

public sealed record UpdateManifest(string Version, string DownloadUrl, long Size, string Sha256);

public static class UpdateTrust
{
    private static string Metadata(string key) => typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? "";
    public static UpdateManifest VerifyManifest(byte[] data, byte[] signature, string publicKey)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (!rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw new CryptographicException("Manifesto de atualização sem assinatura válida.");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(data) ?? throw new InvalidDataException("Manifesto inválido.");
        if (manifest.Size <= 0 || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit) || !Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var url) || url.Scheme != "https")
            throw new InvalidDataException("Manifesto de atualização inválido.");
        return manifest;
    }
    public static UpdateManifest VerifyManifest(byte[] data, byte[] signature)
    {
        string key = Metadata("UpdateManifestPublicKey");
        if (key.Length == 0) throw new CryptographicException("Chave de atualizações não configurada neste build.");
        return VerifyManifest(data, signature, key);
    }
    public static void VerifyInstaller(string path, string? expectedHash = null)
    {
        if (expectedHash is not null)
        {
            using var stream = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("O hash do instalador não confere.");
        }
        string pinned = Metadata("UpdateSignerSha256");
        VerifyAuthenticode(path, pinned);
    }
    public static void VerifyAuthenticode(string path, string pinned)
    {
        if (pinned.Length != 64) throw new CryptographicException("Publicador de atualizações não configurado neste build.");
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = Path.GetFullPath(path) };
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Revocation = 1, Choice = 1, File = pointer, StateAction = 1, Flags = 0x80 };
        try
        {
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) throw new CryptographicException("Assinatura Authenticode do instalador inválida ou não confiável.");
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            if (!certificate.GetCertHashString(HashAlgorithmName.SHA256).Equals(pinned, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("O instalador não pertence ao publicador autorizado.");
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer);
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, Subject; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustData
    {
        public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Choice; public IntPtr File;
        public uint StateAction; public IntPtr State, Url; public uint Flags, UiContext; public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
