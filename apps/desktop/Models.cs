using System.IO;
using System.Text.Json;

namespace PCBoost;

public sealed class CommandSpec
{
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
}
public enum ProfileAction { Preservar, SugerirFechamento, FecharComAutorizacao }
public sealed class ProfileRule
{
    public string Path { get; set; } = "";
    public ProfileAction Action { get; set; }
}
public sealed class GameConfig
{
    public CommandSpec Launch { get; set; } = new();
    public List<ProfileRule> Rules { get; set; } = [];
}
public sealed class ProjectConfig
{
    public string Name { get; set; } = "Meu plugin";
    public string Folder { get; set; } = "";
    public string Java { get; set; } = "";
    public string ServerFolder { get; set; } = "";
    public string ServerJar { get; set; } = "";
    public List<string> ServerArguments { get; set; } = ["nogui"];
    public string Artifact { get; set; } = "";
    public string PluginFileName { get; set; } = "plugin.jar";
    public CommandSpec Build { get; set; } = new();
    public CommandSpec Tests { get; set; } = new();
    public int XmsMb { get; set; } = 512;
    public int XmxMb { get; set; } = 2048;
    public int NativeMarginMb { get; set; } = 1024;
    public int BuildAvailableMb { get; set; } = 2048;
    public int CommitMarginMb { get; set; } = 4096;
    public string Ports { get; set; } = "tcp:25565";
    public string ReadyPattern { get; set; } = @"Done \(.+\)!";
    public string PluginPattern { get; set; } = "";
    public string TestPassPattern { get; set; } = "";
    public int ReadyTimeoutSeconds { get; set; } = 120;
}
public sealed class BoosterSettings
{
    public int SampleSeconds { get; set; } = 5;
    public int BackgroundSeconds { get; set; } = 30;
    public int ProcessSeconds { get; set; } = 20;
    public int RetentionHours { get; set; } = 24;
    public int LowAvailableMb { get; set; } = 2048;
    public int LowDurationSeconds { get; set; } = 180;
    public int CommitPercent { get; set; } = 90;
    public int PoolGrowthMb { get; set; } = 3072;
    public int PoolWindowMinutes { get; set; } = 120;
    public GameConfig Game { get; set; } = new();
    public List<ProfileRule> DevelopmentRules { get; set; } = [];
    public List<ProjectConfig> Projects { get; set; } = [];
    public List<string> CustomGames { get; set; } = [];
    // Alfinete do topo: painéis fixos em vez de recuarem sozinhos.
    public bool PinPanels { get; set; }
    public void Validate()
    {
        CustomGames ??= [];
        if (Game is null || Game.Launch is null || Game.Launch.Arguments is null || Game.Rules is null || DevelopmentRules is null || Projects is null
            || Projects.Any(p => p is null)) throw new InvalidDataException("Estrutura de configuração incompleta.");
        if (SampleSeconds is < 3 or > 60 || BackgroundSeconds < SampleSeconds || BackgroundSeconds > 300
            || ProcessSeconds < SampleSeconds || ProcessSeconds > 300 || RetentionHours is < 1 or > 168
            || LowAvailableMb is < 1 or > 1048576 || LowDurationSeconds is < 10 or > 604800 || CommitPercent is < 50 or > 99
            || PoolGrowthMb is < 1 or > 1048576 || PoolWindowMinutes is < 1 or > 10080) throw new InvalidDataException("Revise os limites e intervalos das configurações.");
    }
}
public sealed record SessionRecord(DateTime At, string Kind, string Result, string Evidence);
public sealed class RuleEditorRow
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Memory { get; set; } = "";
    public ProfileAction Action { get; set; }
}
public sealed class LocalStore
{
    private readonly object recordGate = new();
    public string Root { get; }
    public string? Warning { get; private set; }
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public LocalStore(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCBoost", "workspace");
        Directory.CreateDirectory(Root);
    }
    public BoosterSettings Load()
    {
        try
        {
            string path = Path.Combine(Root, "settings.json");
            var result = File.Exists(path) ? JsonSerializer.Deserialize<BoosterSettings>(File.ReadAllText(path), Json) ?? new() : new();
            result.Validate(); return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException or NullReferenceException)
        { Warning = "Configuração não carregada; o arquivo original foi preservado: " + ex.Message; return new(); }
    }
    public void Save(BoosterSettings settings) { settings.Validate(); Write("settings.json", settings); }
    public void Write<T>(string name, T value)
    {
        string target = Path.Combine(Root, name), temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, Json)); File.Move(temp, target, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public List<T> ReadList<T>(string name)
    {
        try { string path = Path.Combine(Root, name); return File.Exists(path) ? JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), Json) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Warning = ex.Message; return []; }
    }
    public void Record(SessionRecord record)
    {
        lock (recordGate)
        {
        var list = ReadList<SessionRecord>("sessions.json"); list.Add(record);
        Write("sessions.json", list.TakeLast(200).ToList());
        }
    }
}
