using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace PCBoost;

// Atualizações: consulta a última release do GitHub, baixa o instalador novo e o executa em modo
// "--update", que espera este processo fechar, troca o executável instalado e reabre o app.
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string PageUrl, long Size);

public static class Updater
{
    // Repositório das releases: gravado no build (-p:UpdateRepo=dono/repo) ou na variável XIMBUISMO_UPDATE_REPO.
    public static string Repo =>
        Environment.GetEnvironmentVariable("XIMBUISMO_UPDATE_REPO") is { Length: > 0 } env ? env :
        typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdateRepo")?.Value is { Length: > 0 } repo ? repo :
        "Raidoxxx/probable-app";

    public static Version Current
    {
        get { var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0); return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }
    }

    // Só a cópia instalada se troca sozinha; as outras (dist/, depuração) abrem a página da release.
    public static bool IsInstalled => string.Equals(Environment.ProcessPath, Installer.InstalledExe, StringComparison.OrdinalIgnoreCase);

    private static HttpClient Http(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{Installer.AppName}-Updater/{Current}");
        return http;
    }

    // Devolve a release mais nova que a versão atual, ou null se já está em dia.
    public static async Task<UpdateInfo?> Check(Version? current = null, CancellationToken cancel = default)
    {
        using var http = Http(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancel);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // ainda não há release
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancel));
        var root = json.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        if (latest <= (current ?? Current)) return null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
            return new UpdateInfo(latest, tag, asset.GetProperty("browser_download_url").GetString()!,
                root.GetProperty("html_url").GetString() ?? "", asset.GetProperty("size").GetInt64());
        }
        return null; // release sem instalador anexado
    }

    // Baixa o instalador para %TEMP%. O nome precisa conter "Setup" para o exe rodar como instalador.
    public static async Task<string> Download(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        string folder = Path.Combine(Path.GetTempPath(), Installer.AppName + "-Update");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, $"{Installer.AppName}-Setup-{update.Version}.exe"), partial = target + ".part";
        using var http = Http(TimeSpan.FromMinutes(15));
        using (var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.Size, done = 0;
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var file = File.Create(partial);
            var buffer = new byte[81920]; int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel); done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
            if (update.Size > 0 && done != update.Size) throw new IOException("Download incompleto. Tente de novo.");
        }
        File.Move(partial, target, true);
        return target;
    }

    // Inicia o instalador baixado; o app deve fechar logo depois.
    public static void Launch(string setup) =>
        Process.Start(new ProcessStartInfo(setup, $"--update {Environment.ProcessId}") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(setup)! })?.Dispose();

    // Executado pelo instalador em modo --update: espera o app antigo fechar, instala e reabre.
    public static void RunUpdate(string[] args)
    {
        int index = Array.IndexOf(args, "--update");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int pid))
            try { using var old = Process.GetProcessById(pid); old.WaitForExit(30000); } catch (ArgumentException) { } // já fechou
        Installer.Install(Installer.HasDesktopShortcut);
        Process.Start(new ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true, WorkingDirectory = Installer.InstallDir })?.Dispose();
    }
}
