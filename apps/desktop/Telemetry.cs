using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PCBoost;

public sealed record MemorySample(DateTime At, long Total, long Available, long? Committed, long? CommitLimit,
    long? PagedPool, long? NonpagedPool, long? Cache, long? Standby = null, long? HardwareReserved = null)
{
    public long Used => Total - Available;
    public long? CommitHeadroom => CommitLimit is long limit && Committed is long used ? Math.Max(0, limit - used) : null;
    public string TimeLabel => At.ToLocalTime().ToString("HH:mm:ss");
}
public static class WindowsMemory
{
    [StructLayout(LayoutKind.Sequential)] private struct Status
    {
        public uint Size, Load;
        public ulong Total, Available, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Performance
    {
        public uint Size;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
            SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint Handles, Processes, Threads;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref Status value);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool GetPerformanceInfo(ref Performance value, uint size);
    public static MemorySample Capture()
    {
        var physical = new Status { Size = (uint)Marshal.SizeOf<Status>() };
        if (!GlobalMemoryStatusEx(ref physical)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var perf = new Performance { Size = (uint)Marshal.SizeOf<Performance>() };
        bool valid = GetPerformanceInfo(ref perf, perf.Size);
        long? Bytes(UIntPtr pages) => valid ? checked((long)(pages.ToUInt64() * perf.PageSize.ToUInt64())) : null;
        return new(DateTime.UtcNow, checked((long)physical.Total), checked((long)physical.Available),
            Bytes(perf.CommitTotal), Bytes(perf.CommitLimit), Bytes(perf.KernelPaged), Bytes(perf.KernelNonpaged), Bytes(perf.SystemCache));
    }
}
public sealed class TelemetryService
{
    private readonly LocalStore store;
    private readonly BoosterSettings settings;
    private readonly Func<MemorySample> capture;
    private readonly Func<List<ProcessRow>> processes;
    private DateTime nextProcesses, nextSave;
    private Dictionary<string, long?> previous = [];
    private readonly object historyGate = new();
    private List<MemorySample> samples;
    public List<MemorySample> Samples { get { lock (historyGate) return samples.ToList(); } }
    public List<ProcessReading> Processes { get; private set; } = [];
    public DateTime ProcessReadAt { get; private set; }
    public TelemetryService(LocalStore store, BoosterSettings settings, Func<MemorySample>? capture = null, Func<List<ProcessRow>>? processes = null)
    {
        this.store = store; this.settings = settings; this.capture = capture ?? WindowsMemory.Capture;
        this.processes = processes ?? (() => Monitor.Capture(new HashSet<string>()).Processes);
        samples = store.ReadList<MemorySample>("memory.json").Where(s => s.At >= DateTime.UtcNow.AddHours(-settings.RetentionHours)).TakeLast(20000).ToList();
    }
    public MemorySample Sample(bool collectProcesses)
    {
        var sample = capture();
        lock (historyGate) { samples.Add(sample); samples = samples.Where(s => s.At >= sample.At.AddHours(-settings.RetentionHours)).TakeLast(20000).ToList(); }
        if (collectProcesses && sample.At >= nextProcesses)
        {
            var rows = processes(); var parents = ProcessTree.Parents();
            Processes = rows.Select(row =>
            {
                string key = row.Id + ":" + row.StartedUtc?.Ticks;
                long? delta = previous.TryGetValue(key, out var old) && old is long before && row.PrivateBytes is long current ? current - before : null;
                return new ProcessReading(row, parents.GetValueOrDefault(row.Id), delta);
            }).ToList();
            previous = rows.ToDictionary(p => p.Id + ":" + p.StartedUtc?.Ticks, p => p.PrivateBytes);
            ProcessReadAt = sample.At; nextProcesses = sample.At.AddSeconds(settings.ProcessSeconds);
        }
        if (sample.At >= nextSave) { Flush(); nextSave = sample.At.AddSeconds(60); }
        return sample;
    }
    public void Flush() => store.Write("memory.json", Samples.TakeLast(20000).ToList());
}
public sealed record ProcessReading(ProcessRow Process, int ParentId, long? Delta)
{
    public string Name => Process.Name;
    public int Pid => Process.Id;
    public string Resident => Process.PrivateMemoryLabel;
    public string Commit => Process.PrivateCommit is long bytes ? FileCleaner.FormatSize(bytes) : "Indisponível";
    public string Change => Delta is long delta ? $"{delta / 1048576d:+0.0;-0.0;0} MB" : "—";
    public string Parent => ParentId > 0 ? ParentId.ToString() : "—";
}
public sealed record ProcessFamily(string Name, string Pid, string Parent, string Resident, string Commit, string Change)
{
    public static List<ProcessFamily> Group(IReadOnlyList<ProcessReading> readings)
    {
        var index = readings.ToDictionary(p => p.Pid);
        int Root(ProcessReading child)
        {
            var seen = new HashSet<int>(); var current = child;
            while (index.TryGetValue(current.ParentId, out var parent) && !parent.Process.Protected && seen.Add(parent.Pid)
                && parent.Process.StartedUtc is DateTime a && current.Process.StartedUtc is DateTime b && a <= b) current = parent;
            return current.Pid;
        }
        string Sum(IEnumerable<long?> source)
        {
            var values = source.ToList(); return values.All(v => v is null) ? "Indisponível" : (values.Any(v => v is null) ? "≥ " : "") + FileCleaner.FormatSize(values.Sum(v => v ?? 0));
        }
        return readings.GroupBy(Root).OrderByDescending(g => g.Sum(p => p.Process.PrivateBytes ?? 0)).Select(g =>
        {
            var root = index[g.Key]; var rows = g.ToList();
            return new ProcessFamily(root.Name + (rows.Count > 1 ? $" · {rows.Count} processos" : ""), root.Pid.ToString(), root.Parent,
                Sum(rows.Select(r => r.Process.PrivateBytes)), Sum(rows.Select(r => r.Process.PrivateCommit)),
                rows.All(r => r.Delta is not null) ? $"{rows.Sum(r => r.Delta ?? 0) / 1048576d:+0.0;-0.0;0} MB" : "—");
        }).ToList();
    }
}
public static class MemoryDiagnosis
{
    public static List<string> Evaluate(IReadOnlyList<MemorySample> source, BoosterSettings settings)
    {
        var result = new List<string>(); if (source.Count == 0) return ["Aguardando coleta de memória."];
        var samples = source.OrderBy(s => s.At).ToList(); var last = samples[^1];
        var suffix = samples.AsEnumerable().Reverse().TakeWhile(s => s.Available < settings.LowAvailableMb * 1048576L).Reverse().ToList();
        bool continuous = suffix.Zip(suffix.Skip(1)).All(pair => pair.Second.At - pair.First.At <= TimeSpan.FromSeconds(settings.BackgroundSeconds * 3));
        if (suffix.Count > 1 && continuous && last.At - suffix[0].At >= TimeSpan.FromSeconds(settings.LowDurationSeconds))
            result.Add($"RAM disponível abaixo de {settings.LowAvailableMb} MB por pelo menos {settings.LowDurationSeconds / 60d:N1} minutos.");
        if (last.Committed is long committed && last.CommitLimit is > 0 && 100d * committed / last.CommitLimit >= settings.CommitPercent)
            result.Add($"Comprometimento em {100d * committed / last.CommitLimit:N0}% do limite; margem {FileCleaner.FormatSize(last.CommitHeadroom ?? 0)}.");
        var target = last.At.AddMinutes(-settings.PoolWindowMinutes);
        var baseline = samples.LastOrDefault(s => s.At <= target);
        if (baseline is not null && target - baseline.At <= TimeSpan.FromSeconds(settings.BackgroundSeconds * 3)
            && baseline.NonpagedPool is long start && last.NonpagedPool is long end && end - start >= settings.PoolGrowthMb * 1048576L)
            result.Add($"Pool não paginável cresceu {FileCleaner.FormatSize(end - start)} em {settings.PoolWindowMinutes} minutos. Indício para investigar drivers; não é diagnóstico de vazamento.");
        if (result.Count == 0) result.Add("Nenhuma regra de pressão disparou na janela coletada. Isso não exclui problemas intermitentes.");
        return result;
    }
}
