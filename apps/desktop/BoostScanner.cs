using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PCBoost;

public sealed class BoostApp
{
    public bool IsSelected { get; set; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required List<ProcessRow> Processes { get; init; }
    public required string Reason { get; init; }
    public long Bytes => Processes.Sum(p => p.Bytes);
    public string MemoryLabel => $"{Bytes / 1048576d:N1} MB";
    public string ProcessLabel => string.Join(", ", Processes.Select(p => p.Id));
}

public sealed record BoostScan(List<BoostApp> Apps, CleanupScan Cleanup, int KeptApps, DateTime ScannedUtc);

public static class BoostScanner
{
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Id;
        public UIntPtr Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);

    // Unknown background work is never automatically approved. Explicit approval is per executable path.
    private static readonly string[] ProtectedNames =
    ["msmpeng", "securityhealth", "nissrv", "avast", "avguard", "avg", "avp", "mbam", "sophos",
     "sentinel", "crowdstrike", "csfalcon", "forti", "nordvpn", "openvpn", "wireguard", "tailscale",
     "zerotier", "nvcontainer", "nvidia", "radeon", "audiodg", "realtek", "rtk", "intel", "amdfend",
     "onedrive", "dropbox", "googledrive", "backup", "syncthing", "rustdesk", "anydesk", "teamviewer",
     "parsec", "sunshine", "pcboost", "codex", "node", "dotnet", "msbuild", "python", "powershell",
     "pwsh", "conhost", "windowsterminal", "csc", "vbc"];

    internal static bool CanSuggest(IReadOnlyList<ProcessRow> group, IReadOnlySet<int> visible,
        IReadOnlyDictionary<int, double> cpuPercent, DateTime now)
    {
        if (group.Count == 0 || group.Sum(p => p.Bytes) < 20 * 1048576L) return false;
        return group.All(p => !p.Protected && p.Path is not null && p.StartedUtc is DateTime started
            && now - started >= TimeSpan.FromMinutes(3) && !visible.Contains(p.Id)
            && cpuPercent.TryGetValue(p.Id, out double cpu) && cpu < 0.5
            && !ProtectedNames.Any(name => p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
            && group.Sum(p => cpuPercent[p.Id]) < 0.5;
    }

    internal static bool IsSensitive(string name) => ProtectedNames.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static HashSet<int> VisibleProcesses()
    {
        var ids = new HashSet<int>();
        if (!EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window)) { GetWindowThreadProcessId(window, out uint pid); ids.Add((int)pid); }
            return true;
        }, IntPtr.Zero)) throw new InvalidOperationException("Não foi possível verificar as janelas abertas.");
        return ids;
    }

    private static HashSet<int> PreservedProcesses(IReadOnlyList<ProcessRow> rows)
    {
        var visible = VisibleProcesses();
        var parents = new Dictionary<int, int>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new InvalidOperationException("Não foi possível verificar as relações entre apps.");
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Name = "" };
        if (!Process32First(snapshot, ref entry)) throw new InvalidOperationException("Não foi possível ler a árvore de processos.");
        do { parents[(int)entry.Id] = (int)entry.Parent; } while (Process32Next(snapshot, ref entry));
        // Explorer owns the desktop and launches unrelated apps; it must not preserve every descendant.
        var shells = new HashSet<int>(rows.Where(p => p.Name.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            || p.Name.Equals("dwm", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("sihost", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id));
        var descendants = new HashSet<int>(visible.Where(id => !shells.Contains(id))) { Environment.ProcessId };
        bool changed;
        do
        {
            changed = false;
            foreach (var (id, parent) in parents)
                if (descendants.Contains(parent) && descendants.Add(id)) changed = true;
        } while (changed);
        descendants.UnionWith(visible);
        foreach (int visibleId in visible.Where(id => !shells.Contains(id)))
        {
            var chain = new HashSet<int>();
            int ancestor = visibleId;
            while (parents.TryGetValue(ancestor, out int parent) && parent > 0 && !shells.Contains(parent) && chain.Add(parent))
            { descendants.Add(parent); ancestor = parent; }
        }
        var ancestors = new HashSet<int>();
        int current = Environment.ProcessId;
        while (parents.TryGetValue(current, out int parent) && parent > 0 && ancestors.Add(parent)) current = parent;
        descendants.UnionWith(ancestors);
        return descendants;
    }

    private static Dictionary<int, (DateTime Started, TimeSpan Cpu)> CpuSample()
    {
        var result = new Dictionary<int, (DateTime, TimeSpan)>();
        foreach (var process in Process.GetProcesses())
        using (process)
        {
            try { result[process.Id] = (process.StartTime.ToUniversalTime(), process.TotalProcessorTime); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
        return result;
    }

    public static async Task<BoostScan> ScanAsync(IReadOnlySet<string> approved, bool includeFiles = true)
    {
        var first = CpuSample();
        var clock = Stopwatch.StartNew();
        await Task.Delay(1200);
        var second = CpuSample();
        var elapsed = clock.Elapsed.TotalMilliseconds;
        var cpu = new Dictionary<int, double>();
        foreach (var (pid, current) in second)
            if (first.TryGetValue(pid, out var previous) && current.Started == previous.Started)
                cpu[pid] = Math.Max(0, (current.Cpu - previous.Cpu).TotalMilliseconds / elapsed * 100);
        var snapshot = Monitor.Capture(approved);
        var visible = PreservedProcesses(snapshot.Processes);
        var now = DateTime.UtcNow;
        var groups = snapshot.Processes.Where(p => p.Path is not null).GroupBy(p => p.Path!, StringComparer.OrdinalIgnoreCase).ToList();
        var candidates = new List<BoostApp>();
        foreach (var group in groups)
        {
            var processes = group.ToList();
            if (!CanSuggest(processes, visible, cpu, now)) continue;
            bool allowed = approved.Contains(group.Key);
            candidates.Add(new BoostApp
            {
                Path = group.Key, Name = processes[0].DisplayName, Processes = processes,
                IsSelected = allowed,
                Reason = allowed ? "Permitido por você · sem janela visível · pouca CPU na amostra"
                    : "Revisar · sem janela visível · pouca CPU na amostra"
            });
        }
        return new(candidates.OrderByDescending(p => p.Bytes).ToList(), includeFiles ? FileCleaner.ScanDefault() : new CleanupScan([], 0, false),
            groups.Count - candidates.Count, DateTime.UtcNow);
    }

    public static string Close(BoostApp candidate)
    {
        // Check the entire executable group again: a newly opened window/worker cancels this app's boost.
        var all = Monitor.Capture(new HashSet<string>()).Processes;
        var current = all
            .Where(p => string.Equals(p.Path, candidate.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (current.Count != candidate.Processes.Count || current.Any(p => !candidate.Processes.Any(old => old.Id == p.Id && old.StartedUtc == p.StartedUtc)))
            return $"{candidate.Name}: ignorado, os processos mudaram; analise novamente.";
        var visible = PreservedProcesses(all);
        if (current.Any(p => p.Protected || visible.Contains(p.Id)))
            return $"{candidate.Name}: mantido, ganhou uma janela ou é protegido.";
        var initialCpu = CpuSample();
        var clock = Stopwatch.StartNew();
        Thread.Sleep(400);
        var nextCpu = CpuSample();
        var cpu = new Dictionary<int, double>();
        foreach (var row in current)
            if (initialCpu.TryGetValue(row.Id, out var initial) && nextCpu.TryGetValue(row.Id, out var next)
                && initial.Started == row.StartedUtc && next.Started == row.StartedUtc)
                cpu[row.Id] = Math.Max(0, (next.Cpu - initial.Cpu).TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100);
        if (!CanSuggest(current, PreservedProcesses(all), cpu, DateTime.UtcNow))
            return $"{candidate.Name}: mantido, ficou ativo ou deixou de atender às regras.";
        int closed = 0;
        var errors = new List<string>();
        foreach (var row in candidate.Processes)
        {
            try
            {
                // A window may have appeared since the group check.
                if (PreservedProcesses(all).Overlaps(current.Select(p => p.Id)))
                { errors.Add("uma janela foi aberta"); break; }
                Monitor.Terminate(row);
                using var process = Process.GetProcessById(row.Id);
                if (process.WaitForExit(1500)) closed++;
                else errors.Add($"PID {row.Id}: ainda em execução");
            }
            catch (ArgumentException) { closed++; /* Process already exited. */ }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            { errors.Add($"PID {row.Id}: {ex.Message}"); }
        }
        return $"{candidate.Name}: {closed}/{candidate.Processes.Count} processos encerrados. {string.Join("; ", errors)}";
    }
}
