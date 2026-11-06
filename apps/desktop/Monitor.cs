using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace PCBoost;

public sealed record ProcessRow(int Id, string Name, string DisplayName, string? Path,
    DateTime? StartedUtc, long Bytes, int SessionId, bool Protected, string Status, double Percent)
{
    public long? PrivateBytes { get; init; }
    public long? PrivateCommit { get; init; }
    public string PrivateMemoryLabel => PrivateBytes is long value ? FileCleaner.FormatSize(value) : "Sem leitura";
    public string MemoryLabel => $"{Bytes / 1048576d:N1} MB";
    public string PercentLabel => $"{Percent:N1}%";
    public string? WatchKey => Path is null ? null : System.IO.Path.GetFullPath(Path);
}

public sealed record Snapshot(ulong Total, ulong Available, List<ProcessRow> Processes);

public static class Monitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemory
    {
        public uint Size, PageFaults;
        public UIntPtr PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool,
            NonPagedPool, Pagefile, PeakPagefile, PrivateCommit, PrivateWorkingSet;
        public ulong SharedCommit;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref ProcessMemory info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);

    private static string? ReadPath(int id)
    {
        using var handle = OpenProcess(0x1000, false, id);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(32768);
        uint length = (uint)buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref length) ? buffer.ToString() : null;
    }

    private static (long WorkingSet, long? Private, long? Commit) ReadMemory(int id, long fallback)
    {
        using var handle = OpenProcess(0x410, false, id);
        if (handle.IsInvalid) return (fallback, null, null);
        var info = new ProcessMemory { Size = (uint)Marshal.SizeOf<ProcessMemory>(), PrivateWorkingSet = UIntPtr.MaxValue };
        if (!GetProcessMemoryInfo(handle, ref info, info.Size))
        {
            info.Size = (uint)Marshal.OffsetOf<ProcessMemory>(nameof(ProcessMemory.PrivateWorkingSet));
            if (!GetProcessMemoryInfo(handle, ref info, info.Size)) return (fallback, null, null);
            return (checked((long)info.WorkingSet.ToUInt64()), null, checked((long)info.PrivateCommit.ToUInt64()));
        }
        long? resident = info.PrivateWorkingSet == UIntPtr.MaxValue || info.PrivateWorkingSet.ToUInt64() > info.WorkingSet.ToUInt64()
            ? null : checked((long)info.PrivateWorkingSet.ToUInt64());
        return (checked((long)info.WorkingSet.ToUInt64()), resident, checked((long)info.PrivateCommit.ToUInt64()));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(IntPtr handle, [MarshalAs(UnmanagedType.Bool)] out bool critical);
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    private static readonly HashSet<string> Essential = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "secure system", "smss", "csrss", "wininit", "services",
        "lsass", "winlogon", "svchost", "dwm", "fontdrvhost", "sihost", "explorer", "memory compression"
    };
    private static readonly string WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly int CurrentSession = Process.GetCurrentProcess().SessionId;

    public static bool IsProtected(int id, string name, string? path, DateTime? started, int session)
    {
        return id == Environment.ProcessId || id <= 4 || Essential.Contains(name)
            || path is null || started is null || session != CurrentSession
            || path.StartsWith(WindowsDirectory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static Snapshot Capture(IReadOnlySet<string> watched)
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var rows = new List<ProcessRow>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string name = process.ProcessName;
                    long bytes = process.WorkingSet64;
                    var memoryReading = ReadMemory(process.Id, bytes);
                    bytes = memoryReading.WorkingSet;
                    string? path = ReadPath(process.Id);
                    DateTime? started = null;
                    int session = -1;
                    try { started = process.StartTime.ToUniversalTime(); } catch { }
                    try { session = process.SessionId; } catch { }
                    bool protectedProcess = IsProtected(process.Id, name, path, started, session);
                    string status = path is null ? "Origem indisponível" : protectedProcess ? "Protegido no PCBoost" : "App da sua sessão";
                    if (path is not null && watched.Contains(path)) status = "★ Na sua lista";
                    rows.Add(new(process.Id, name, name + ".exe", path, started, bytes, session,
                        protectedProcess, status, 100d * bytes / memory.TotalPhys) { PrivateBytes = memoryReading.Private, PrivateCommit = memoryReading.Commit });
                }
                catch (InvalidOperationException) { /* Exited during snapshot. */ }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return new(memory.TotalPhys, memory.AvailPhys, rows.OrderByDescending(p => p.Bytes).ToList());
    }

    // Revalidate PID, start time, executable and critical state immediately before acting.
    public static void Terminate(ProcessRow row)
    {
        using var process = VerifiedProcess(row);
        process.Kill(entireProcessTree: false);
    }

    public static void TrimWorkingSet(ProcessRow row)
    {
        using var process = VerifiedProcess(row);
        if (!EmptyWorkingSet(process.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static Process VerifiedProcess(ProcessRow row)
    {
        var process = Process.GetProcessById(row.Id);
        try
        {
            var handle = process.Handle; // Keep this exact process instance open throughout validation and action.
            var started = process.StartTime.ToUniversalTime();
            var path = process.MainModule?.FileName;
            if (started != row.StartedUtc || !string.Equals(path, row.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O processo mudou. Atualize a lista e selecione novamente.");
            if (IsProtected(process.Id, process.ProcessName, path, started, process.SessionId)
                || !IsProcessCritical(handle, out bool critical) || critical)
                throw new InvalidOperationException("Este processo é protegido ou não pôde ser verificado.");
            return process;
        }
        catch { process.Dispose(); throw; }
    }
}

public sealed class WatchStore
{
    private readonly string file;
    public HashSet<string> Paths { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LoadWarning { get; private set; }
    public WatchStore(string? file = null)
    {
        this.file = file ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCBoost", "watchlist.json");
        try
        {
            if (File.Exists(this.file)) Paths = new(JsonSerializer.Deserialize<string[]>(File.ReadAllText(this.file)) ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { LoadWarning = "Não foi possível ler sua lista salva: " + ex.Message; }
    }
    public void Toggle(string path)
    {
        var next = new HashSet<string>(Paths, StringComparer.OrdinalIgnoreCase);
        if (!next.Remove(path)) next.Add(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(next.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, file, overwrite: true);
        Paths = next;
    }
}
