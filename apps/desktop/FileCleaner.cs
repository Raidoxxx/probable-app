using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PCBoost;

public sealed record CleanupFile(string Root, string Path, long Bytes, long LastWrite,
    long Creation, uint Volume, ulong Identity)
{
    public bool IsSelected { get; set; } = true;
}

public sealed class CleanupGroup
{
    public bool IsSelected { get; set; } = true;
    public required string Name { get; init; }
    public required string Root { get; init; }
    public required List<CleanupFile> Files { get; init; }
    public int Count => Files.Count;
    public long Bytes => Files.Sum(p => p.Bytes);
    public string SizeLabel => FileCleaner.FormatSize(Bytes);
}

public sealed record CleanupScan(List<CleanupGroup> Groups, int Skipped, bool Limited);
public sealed record CleanupResult(int Deleted, long Bytes, int Skipped, List<string> Errors);

public static class FileCleaner
{
    private const uint ReparsePoint = 0x400, DirectoryAttribute = 0x10;
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".sys", ".msi", ".msp", ".cab", ".bat", ".cmd", ".ps1" };
    private static readonly TimeSpan MinimumAge = TimeSpan.FromDays(7);
    private const int MaximumFiles = 50000;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTime { public uint Low, High; public readonly long Value => ((long)High << 32) | Low; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public NativeTime Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IdHigh, IdLow;
        public readonly long Bytes => ((long)SizeHigh << 32) | SizeLow;
        public readonly ulong Identity => ((ulong)IdHigh << 32) | IdLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Disposition { public byte Delete; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref Disposition info, uint size);

    public static string FormatSize(long bytes) => bytes >= 1073741824L ? $"{bytes / 1073741824d:N2} GB" : $"{bytes / 1048576d:N1} MB";
    internal static bool IsInside(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool HasLinkInPath(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private static FileInformation ValidateHandle(SafeFileHandle handle, string path, string root, DateTime now)
    {
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var final = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity) throw new IOException("Não foi possível verificar o caminho real.");
        string resolved = final.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        long cutoff = (now - MinimumAge).ToFileTimeUtc();
        if (!IsInside(path, root) || !string.Equals(Path.GetFullPath(path), resolved, StringComparison.OrdinalIgnoreCase)
            || !IsInside(resolved, root) || (info.Attributes & (ReparsePoint | DirectoryAttribute)) != 0
            || info.Links != 1 || info.Write.Value >= cutoff || info.Creation.Value >= cutoff || info.Access.Value >= cutoff
            || (info.Attributes & (uint)(FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Offline)) != 0
            || CodeExtensions.Contains(Path.GetExtension(path)))
            throw new IOException("Arquivo recente, protegido, com vínculo ou fora da pasta temporária permitida.");
        return info;
    }

    public static CleanupScan ScanDefault() => Scan(
    [
        ("Temporários do usuário · mais de 7 dias", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp")),
        ("Temporários do Windows · mais de 7 dias", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"))
    ], DateTime.UtcNow);

    internal static CleanupScan Scan(IEnumerable<(string Name, string Root)> roots, DateTime now)
    {
        var groups = new List<CleanupGroup>();
        int skipped = 0, visited = 0;
        bool limited = false;
        var watch = Stopwatch.StartNew();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputs = roots.ToList();
        foreach (var (name, inputRoot) in inputs)
        {
            bool rootLimited = false;
            int rootVisited = 0;
            var rootStarted = watch.Elapsed;
            var rootBudget = TimeSpan.FromSeconds(15d / Math.Max(1, inputs.Count));
            string root = Path.GetFullPath(inputRoot).TrimEnd(Path.DirectorySeparatorChar);
            var files = new List<CleanupFile>();
            groups.Add(new CleanupGroup { Name = name, Root = root, Files = files });
            try
            {
                if (!Directory.Exists(root)) continue;
                if (HasLinkInPath(root)) { skipped++; continue; }
                var pending = new Stack<(string Path, int Depth)>();
                pending.Push((root, 0));
                while (pending.Count > 0)
                {
                    if (visited >= MaximumFiles || rootVisited >= MaximumFiles / Math.Max(1, inputs.Count)
                        || watch.Elapsed - rootStarted > rootBudget) { rootLimited = true; break; }
                    var (directory, depth) = pending.Pop();
                    try
                    {
                        if (HasLinkInPath(directory)) { skipped++; continue; }
                        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                        {
                            visited++; rootVisited++;
                            if (visited > MaximumFiles || rootVisited > MaximumFiles / Math.Max(1, inputs.Count)
                                || watch.Elapsed - rootStarted > rootBudget) { rootLimited = true; break; }
                            try
                            {
                                if (!IsInside(entry, root) || !seen.Add(Path.GetFullPath(entry))) continue;
                                var attributes = File.GetAttributes(entry);
                                if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                                if ((attributes & FileAttributes.Directory) != 0)
                                { if (depth < 20) pending.Push((entry, depth + 1)); else skipped++; continue; }
                                if (CodeExtensions.Contains(Path.GetExtension(entry))) { skipped++; continue; }
                                var basic = new FileInfo(entry);
                                if (now - basic.LastWriteTimeUtc < MinimumAge || now - basic.CreationTimeUtc < MinimumAge
                                    || now - basic.LastAccessTimeUtc < MinimumAge) { skipped++; continue; }
                                using var handle = CreateFile(entry, 0x80, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
                                var info = ValidateHandle(handle, entry, root, now);
                                files.Add(new(root, entry, info.Bytes, info.Write.Value, info.Creation.Value, info.Volume, info.Identity));
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { skipped++; }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                    if (rootLimited) break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
            limited |= rootLimited;
        }
        return new(groups, skipped, limited);
    }

    // Deletion is bound to the verified, exclusively opened file handle, not a path checked earlier.
    internal static CleanupResult Delete(IEnumerable<CleanupFile> selected, IReadOnlySet<string> roots, DateTime now, CancellationToken cancellation = default)
    {
        int deleted = 0, skipped = 0;
        long bytes = 0;
        var errors = new List<string>();
        foreach (var file in selected.Where(f => f.IsSelected).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (cancellation.IsCancellationRequested) break;
            try
            {
                if (!roots.Contains(file.Root) || !IsInside(file.Path, file.Root) || HasLinkInPath(Path.GetDirectoryName(file.Path)!))
                    throw new IOException("Pasta não permitida ou com vínculo.");
                using var handle = CreateFile(file.Path, 0x10080, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
                var info = ValidateHandle(handle, file.Path, file.Root, now);
                if (info.Identity != file.Identity || info.Volume != file.Volume || info.Bytes != file.Bytes
                    || info.Write.Value != file.LastWrite || info.Creation.Value != file.Creation)
                    throw new IOException("Arquivo alterado após a análise.");
                var disposition = new Disposition { Delete = 1 };
                if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<Disposition>()))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                deleted++; bytes += info.Bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
            {
                skipped++;
                if (errors.Count < 50) errors.Add($"{file.Path}: {ex.Message}");
            }
        }
        return new(deleted, bytes, skipped, errors);
    }
}
