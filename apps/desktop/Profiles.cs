using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PCBoost;

public static class ProcessTree
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Entry
    {
        public uint Size, Usage, Id; public UIntPtr Heap; public uint Module, Threads, Parent;
        public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(SafeFileHandle handle, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(SafeFileHandle handle, ref Entry entry);
    public static Dictionary<int, int> Parents()
    {
        var result = new Dictionary<int, int>(); using var handle = CreateToolhelp32Snapshot(2, 0);
        var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Name = "" };
        if (handle.IsInvalid || !Process32First(handle, ref entry)) return result;
        do { result[(int)entry.Id] = (int)entry.Parent; } while (Process32Next(handle, ref entry));
        return result;
    }
    public static HashSet<int> Related(IEnumerable<int> roots, IReadOnlyDictionary<int, int> parents)
    {
        var result = roots.ToHashSet(); bool changed;
        do { changed = false; foreach (var (id, parent) in parents) if (result.Contains(parent) && result.Add(id)) changed = true; } while (changed);
        return result;
    }
}
public sealed class ProfileCandidate
{
    public required ProcessRow Process { get; init; }
    public required string Reason { get; init; }
    public bool Selected { get; set; }
    public string Name => Process.Name;
    public int Pid => Process.Id;
    public string Path => Process.Path ?? "Origem indisponível";
    public string Memory => Process.PrivateMemoryLabel;
    public string Commit => Process.PrivateCommit is long value ? FileCleaner.FormatSize(value) : "Indisponível";
    public string Started => Process.StartedUtc?.ToLocalTime().ToString("dd/MM HH:mm") ?? "Indisponível";
}
public sealed record ProfileResult(MemorySample Before, MemorySample After, List<string> Actions);
public static class ProfileService
{
    private static readonly string[] DevelopmentNames = ["code", "idea", "eclipse", "devenv", "rider", "powershell", "pwsh", "cmd", "windowsterminal", "conhost", "codex", "claude", "node", "dotnet", "msbuild", "gradle", "mvn"];
    public static List<ProfileCandidate> Review(IReadOnlyList<ProcessRow> rows, IReadOnlyList<ProfileRule> rules,
        bool development, string? gamePath = null, IEnumerable<int>? owned = null)
    {
        var preserve = rows.Where(p => BoostScanner.IsSensitive(p.Name)
            || (development && DevelopmentNames.Any(n => p.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
            || (gamePath is not null && string.Equals(p.Path, gamePath, StringComparison.OrdinalIgnoreCase))
            || rules.Any(r => r.Action == ProfileAction.Preservar && string.Equals(r.Path, p.Path, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Id).Concat(owned ?? []);
        var protectedIds = ProcessTree.Related(preserve, ProcessTree.Parents());
        protectedIds.UnionWith(rows.Where(p => p.Protected).Select(p => p.Id));
        return rows.Where(p => !protectedIds.Contains(p.Id) && p.Path is not null)
            .Select(p => (Process: p, Rule: rules.FirstOrDefault(r => string.Equals(r.Path, p.Path, StringComparison.OrdinalIgnoreCase))))
            .Where(pair => pair.Rule is not null && pair.Rule.Action != ProfileAction.Preservar)
            .Select(pair => new ProfileCandidate { Process = pair.Process, Reason = pair.Rule!.Action == ProfileAction.SugerirFechamento
                ? "Sugerido por sua regra; precisa marcar" : "Autorização prévia; revise e marque", Selected = false }).ToList();
    }
    public static async Task<ProfileResult> ApplyAsync(IEnumerable<ProfileCandidate> selected, Func<HashSet<int>> protectedNow)
    {
        var before = WindowsMemory.Capture(); var actions = new List<string>();
        foreach (var item in selected.Where(p => p.Selected))
        {
            try
            {
                if (protectedNow().Contains(item.Pid)) { actions.Add($"{item.Name} / {item.Pid}: preservado pela revisão atual."); continue; }
                using var process = Monitor.VerifiedProcess(item.Process);
                if (!process.CloseMainWindow()) { actions.Add($"{item.Name} / {item.Pid}: sem janela para fechamento normal; mantido."); continue; }
                bool exited = await Task.Run(() => process.WaitForExit(2000));
                actions.Add($"{item.Name} / {item.Pid}: " + (exited ? "fechado normalmente." : "fechamento solicitado; aguardando resposta/confirmação do aplicativo."));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            { actions.Add($"{item.Name} / {item.Pid}: preservado — {ex.Message}"); }
        }
        await Task.Delay(700);
        return new(before, WindowsMemory.Capture(), actions);
    }
}
