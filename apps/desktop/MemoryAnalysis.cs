using System.Collections.Concurrent;
using System.Diagnostics;

namespace PCBoost;

public sealed class AppRow
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required List<ProcessRow> Processes { get; init; }
    public required string Description { get; init; }
    public ulong TotalPhysical { get; init; }
    public string? Path => Processes[0].Path;
    public string Initial => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "·";
    public int Count => Processes.Count;
    public int MissingReadings => Processes.Count(p => p.PrivateBytes is null);
    public long? PrivateBytes => MissingReadings == Count ? null : Processes.Sum(p => p.PrivateBytes ?? 0);
    public string MemoryLabel => PrivateBytes is long bytes ? (MissingReadings > 0 ? "≥ " : "") + FileCleaner.FormatSize(bytes) : "—";
    public string PercentLabel => PrivateBytes is long bytes ? $"{(MissingReadings > 0 ? "≥ " : "")}{100d * bytes / TotalPhysical:N1}%" : "—";
    public double Share => PrivateBytes is long bytes && TotalPhysical > 0 ? 100d * bytes / TotalPhysical : 0;
    public string Status => Processes.Any(p => p.Status.Contains("Na sua lista")) ? "Acompanhando"
        : Processes.All(p => p.Protected) ? "Protegido" : "Aplicativo";
    public string ReadingHint => MissingReadings > 0 ? $"{MissingReadings} de {Count} processos sem leitura privada. O valor, se exibido, é parcial." : "RAM física privada dos processos deste executável; exclui páginas compartilhadas.";
}

public sealed record MemoryBreakdown(long PrivateRead, long Remainder, int MissingReadings, bool Inconsistent)
{
    public static MemoryBreakdown From(Snapshot snapshot)
    {
        long used = checked((long)(snapshot.Total - snapshot.Available));
        long known = snapshot.Processes.Sum(p => p.PrivateBytes ?? 0);
        return new(known, Math.Max(0, used - known), snapshot.Processes.Count(p => p.PrivateBytes is null), known > used);
    }
}

public static class MemoryAnalysis
{
    private static readonly ConcurrentDictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase);
    public static List<AppRow> Group(Snapshot snapshot) => snapshot.Processes.GroupBy(p => p.Path is string path
        ? "path:" + path : $"pid:{p.Id}:{p.StartedUtc?.Ticks}", StringComparer.OrdinalIgnoreCase)
        .Select(g =>
        {
            var processes = g.OrderByDescending(p => p.PrivateBytes ?? -1).ThenByDescending(p => p.Bytes).ToList();
            string name = processes[0].Name;
            string description = processes[0].Path is string path ? Descriptions.GetOrAdd(path, ReadDescription) : "Origem indisponível";
            string friendly = name.ToLowerInvariant() switch
            {
                "brave" => "Brave", "code" => "Visual Studio Code", "svchost" => "Serviços do Windows",
                "explorer" => "Explorador de Arquivos", "memory compression" => "Memória compactada",
                "steamwebhelper" => "Steam · WebHelper", "msedgewebview2" => "Microsoft · WebView2",
                "pcboost" => "PCBoost", _ => name
            };
            return new AppRow { Key = g.Key, DisplayName = friendly, Description = description, Processes = processes, TotalPhysical = snapshot.Total };
        }).OrderByDescending(p => p.PrivateBytes ?? -1).ToList();

    private static string ReadDescription(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.Join(" · ", new[] { info.FileDescription, info.CompanyName }.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
        catch { return "Descrição indisponível"; }
    }
}
