using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PCBoost;

// Um jogo instalado: de onde veio, onde está e como abrir.
public sealed record GameEntry(string Key, string Name, string Source, string InstallDir, string? Exe, string? LaunchUri, string? Cover, string? LaunchArgs = null)
{
    public bool CanLaunch => LaunchUri is not null || Exe is not null && File.Exists(Exe);
}

// Detecta jogos das lojas mais comuns lendo os manifestos locais (nada é baixado).
public static partial class GameLibrary
{
    private static readonly string[] NotGames = ["redistributable", "steamworks", "proton", "steam linux runtime", "steamvr", "soundtrack", "dedicated server", " sdk", "benchmark", "wallpaper engine", "launcher prerequisites"];
    private static bool LooksLikeGame(string name) => !NotGames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

    public static List<GameEntry> Detect(IEnumerable<string> custom)
    {
        var all = new List<GameEntry>();
        void Safe(Func<IEnumerable<GameEntry>> source) { try { all.AddRange(source()); } catch (Exception) { } }
        Safe(Steam); Safe(Epic); Safe(Gog); Safe(Ubisoft); Safe(Riot);
        foreach (var exe in custom.Where(File.Exists))
            all.Add(new("custom:" + exe.ToLowerInvariant(), FileVersionInfo.GetVersionInfo(exe).ProductName is { Length: > 1 } product ? product : Path.GetFileNameWithoutExtension(exe), "MANUAL", Path.GetDirectoryName(exe)!, exe, null, null));
        return all.Where(g => Directory.Exists(g.InstallDir) && LooksLikeGame(g.Name))
            .GroupBy(g => g.InstallDir.TrimEnd('\\').ToLowerInvariant()).Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---------- Steam: libraryfolders.vdf + appmanifest_*.acf ----------
    [GeneratedRegex("\"(\\w+)\"\\s+\"([^\"]*)\"")] private static partial Regex VdfPair();
    private static Dictionary<string, string> Vdf(string text) => VdfPair().Matches(text).GroupBy(m => m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().Groups[2].Value.Replace("\\\\", "\\"), StringComparer.OrdinalIgnoreCase);
    private static IEnumerable<GameEntry> Steam()
    {
        string? root = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (root is null || !Directory.Exists(root)) yield break;
        root = Path.GetFullPath(root);
        var libraries = new List<string> { root };
        string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf)) libraries.AddRange(VdfPair().Matches(File.ReadAllText(vdf)).Where(m => m.Groups[1].Value == "path").Select(m => m.Groups[2].Value.Replace("\\\\", "\\")));
        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var data = Vdf(File.ReadAllText(manifest));
                if (!data.TryGetValue("appid", out var id) || !data.TryGetValue("name", out var name) || !data.TryGetValue("installdir", out var dir)) continue;
                if (data.TryGetValue("StateFlags", out var flags) && int.TryParse(flags, out int f) && (f & 4) == 0) continue; // não totalmente instalado
                yield return new("steam:" + id, name, "STEAM", Path.Combine(apps, "common", dir), null, "steam://rungameid/" + id, SteamCover(root, id));
            }
        }
    }
    private static string? SteamCover(string root, string id)
    {
        string cache = Path.Combine(root, "appcache", "librarycache");
        foreach (var candidate in new[] { Path.Combine(cache, id + "_library_600x900.jpg"), Path.Combine(cache, id + "_header.jpg") })
            if (File.Exists(candidate)) return candidate;
        string folder = Path.Combine(cache, id);
        if (!Directory.Exists(folder)) return null;
        return Directory.EnumerateFiles(folder, "library_600x900*.jpg", SearchOption.AllDirectories).FirstOrDefault()
            ?? Directory.EnumerateFiles(folder, "header*.jpg", SearchOption.AllDirectories).FirstOrDefault();
    }

    // ---------- Epic Games: ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item ----------
    private static IEnumerable<GameEntry> Epic()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(folder)) yield break;
        foreach (var file in Directory.EnumerateFiles(folder, "*.item"))
        {
            JsonElement root;
            try { root = JsonDocument.Parse(File.ReadAllText(file)).RootElement; } catch (JsonException) { continue; }
            string S(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) continue;
            if (root.TryGetProperty("AppCategories", out var categories) && categories.ValueKind == JsonValueKind.Array && !categories.EnumerateArray().Any(c => c.GetString() == "games")) continue;
            string dir = S("InstallLocation"), name = S("DisplayName"), exe = S("LaunchExecutable");
            if (dir.Length == 0 || name.Length == 0) continue;
            string uri = $"com.epicgames.launcher://apps/{Uri.EscapeDataString(S("CatalogNamespace"))}%3A{Uri.EscapeDataString(S("CatalogItemId"))}%3A{Uri.EscapeDataString(S("AppName"))}?action=launch&silent=true";
            yield return new("epic:" + S("AppName"), name, "EPIC", dir, exe.Length > 0 ? Path.Combine(dir, exe) : null, uri, null);
        }
    }

    // ---------- GOG Galaxy: HKLM\SOFTWARE\WOW6432Node\GOG.com\Games ----------
    private static IEnumerable<GameEntry> Gog()
    {
        using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (games is null) yield break;
        foreach (var id in games.GetSubKeyNames())
        {
            using var key = games.OpenSubKey(id); if (key is null) continue;
            if (key.GetValue("gameName") is not string name || key.GetValue("path") is not string dir) continue;
            string? exe = key.GetValue("exe") as string;
            yield return new("gog:" + id, name, "GOG", dir, exe, null, null, key.GetValue("launchParam") as string);
        }
    }

    // ---------- Ubisoft Connect: HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs ----------
    private static IEnumerable<GameEntry> Ubisoft()
    {
        using var installs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (installs is null) yield break;
        foreach (var id in installs.GetSubKeyNames())
        {
            using var key = installs.OpenSubKey(id);
            if (key?.GetValue("InstallDir") is not string dir) continue;
            dir = dir.Replace('/', '\\');
            yield return new("ubisoft:" + id, new DirectoryInfo(dir.TrimEnd('\\')).Name, "UBISOFT", dir, null, $"uplay://launch/{id}/0", null);
        }
    }

    // ---------- Riot (VALORANT, League of Legends...) ----------
    private static IEnumerable<GameEntry> Riot()
    {
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games");
        string installs = Path.Combine(data, "RiotClientInstalls.json"), metadata = Path.Combine(data, "Metadata");
        if (!File.Exists(installs) || !Directory.Exists(metadata)) yield break;
        string? client = null;
        try { using var doc = JsonDocument.Parse(File.ReadAllText(installs)); client = doc.RootElement.TryGetProperty("rc_default", out var rc) ? rc.GetString() : null; } catch (JsonException) { }
        if (client is null || !File.Exists(client)) yield break;
        foreach (var product in Directory.EnumerateDirectories(metadata))
        {
            string folder = Path.GetFileName(product); // ex.: valorant.live
            if (folder.StartsWith("Riot Client", StringComparison.OrdinalIgnoreCase)) continue;
            var settings = Directory.EnumerateFiles(product, "*.product_settings.yaml").FirstOrDefault(); if (settings is null) continue;
            var match = Regex.Match(File.ReadAllText(settings), "product_install_full_path:\\s*\"?([^\"\\r\\n]+)");
            if (!match.Success) continue;
            string[] parts = folder.Split('.'); string id = parts[0], patch = parts.Length > 1 ? parts[1] : "live";
            string name = id switch { "valorant" => "VALORANT", "league_of_legends" => "League of Legends", "bacon" => "Legends of Runeterra", _ => id.Replace('_', ' ') };
            yield return new("riot:" + id, name, "RIOT", match.Groups[1].Value.Trim().Replace('/', '\\'), client, null, null, $"--launch-product={id} --launch-patchline={patch}");
        }
    }

    // ---------- Abrir ----------
    public static void Launch(GameEntry game)
    {
        if (game.LaunchUri is not null) { Process.Start(new ProcessStartInfo(game.LaunchUri) { UseShellExecute = true })?.Dispose(); return; }
        if (game.Exe is null || !File.Exists(game.Exe)) throw new InvalidOperationException($"Não encontrei o executável de {game.Name}.");
        Process.Start(new ProcessStartInfo(game.Exe, game.LaunchArgs ?? "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(game.Exe)! })?.Dispose();
    }

    // ---------- Imagem: capa da loja, senão ícone grande do executável ----------
    public static ImageSource? Art(GameEntry game)
    {
        try
        {
            if (game.Cover is not null && File.Exists(game.Cover))
            {
                var image = new BitmapImage(); image.BeginInit(); image.UriSource = new Uri(game.Cover); image.DecodePixelWidth = 300; image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze();
                return image;
            }
            string? exe = game.Exe is not null && File.Exists(game.Exe) && !game.Source.Equals("RIOT") ? game.Exe : GuessExe(game.InstallDir);
            return exe is null ? null : JumboIcon(exe);
        }
        catch (Exception) { return null; }
    }
    private static string? GuessExe(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var skip = new[] { "unins", "crash", "setup", "redist", "vc_", "dxsetup", "launcher", "helper", "report", "easyanticheat", "be_service", "updater" };
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                .Where(f => !skip.Any(s => f.Name.Contains(s, StringComparison.OrdinalIgnoreCase))).OrderByDescending(f => f.Length).FirstOrDefault()?.FullName;
        }
        catch (Exception) { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo { public IntPtr Icon; public int Index; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string Type; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll")] private static extern int SHGetImageList(int list, ref Guid riid, out IImageList images);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        int Add(IntPtr image, IntPtr mask, ref int index); int ReplaceIcon(int i, IntPtr icon, ref int index); int SetOverlayImage(int image, int overlay);
        int Replace(int i, IntPtr image, IntPtr mask); int AddMasked(IntPtr image, int mask, ref int index); int Draw(IntPtr parameters); int Remove(int i);
        int GetIcon(int i, int flags, ref IntPtr icon);
    }
    private static ImageSource? JumboIcon(string path)
    {
        var info = new ShFileInfo();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), 0x4000 /* SYSICONINDEX */) == IntPtr.Zero) return null;
        var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
        if (SHGetImageList(0x4 /* JUMBO 256px */, ref iid, out var list) != 0) return null;
        IntPtr icon = IntPtr.Zero; list.GetIcon(info.Index, 0x1 /* transparent */, ref icon);
        if (icon == IntPtr.Zero) return null;
        try { var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); source.Freeze(); return source; }
        finally { DestroyIcon(icon); }
    }
}
