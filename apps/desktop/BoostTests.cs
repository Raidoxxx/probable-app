using System.IO;
using System.Runtime.InteropServices;

namespace PCBoost;

internal static class BoostTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr reserved);

    public static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        DateTime now = DateTime.UtcNow;
        var row = new ProcessRow(12345, "example", "example.exe", @"C:\Apps\example.exe", now.AddMinutes(-10),
            100 * 1048576L, 1, false, "", 1);
        var hidden = new HashSet<int>();
        var idle = new Dictionary<int, double> { [row.Id] = 0.1 };
        Check(BoostScanner.CanSuggest([row], hidden, idle, now), "Idle background app suggestion");
        Check(!BoostScanner.CanSuggest([row], new HashSet<int> { row.Id }, idle, now), "Visible app exclusion");
        Check(!BoostScanner.CanSuggest([row with { Protected = true }], hidden, idle, now), "Protected app exclusion");
        Check(!BoostScanner.CanSuggest([row with { Name = "OneDrive" }], hidden, idle, now), "Sync exclusion");
        Check(!BoostScanner.CanSuggest([row with { Name = "MsMpEng" }], hidden, idle, now), "Security process exclusion");
        Check(!BoostScanner.CanSuggest([row with { StartedUtc = now }], hidden, idle, now), "New process exclusion");
        Check(!BoostScanner.CanSuggest([row], hidden, new Dictionary<int, double> { [row.Id] = 10 }, now), "Active background work exclusion");
        Check(!BoostScanner.CanSuggest([row], hidden, new Dictionary<int, double>(), now), "Missing activity sample exclusion");
        var visibleSibling = row with { Id = 12346 };
        Check(!BoostScanner.CanSuggest([row, visibleSibling], new HashSet<int> { visibleSibling.Id },
            new Dictionary<int, double> { [row.Id] = 0, [visibleSibling.Id] = 0 }, now), "Entire executable group preserved");
        Check(!BoostScanner.CanSuggest([row, visibleSibling], hidden,
            new Dictionary<int, double> { [row.Id] = 0.4, [visibleSibling.Id] = 0.4 }, now), "Aggregate group CPU activity exclusion");

        string fixture = Path.Combine(AppContext.BaseDirectory, "cleanup-test-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(fixture, "temp"), outside = Path.Combine(fixture, "temp-other");
        Directory.CreateDirectory(root); Directory.CreateDirectory(outside);
        string MakeOld(string directory, string name)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllText(path, "disposable test data");
            File.SetCreationTimeUtc(path, now.AddDays(-10));
            File.SetLastWriteTimeUtc(path, now.AddDays(-10));
            File.SetLastAccessTimeUtc(path, now.AddDays(-10));
            return path;
        }
        try
        {
            string old = MakeOld(root, "old.tmp");
            string kept = MakeOld(root, "kept.tmp");
            string locked = MakeOld(root, "locked.tmp");
            string changed = MakeOld(root, "changed.tmp");
            string replaced = MakeOld(root, "replaced.tmp");
            string code = MakeOld(root, "program.dll");
            string readOnly = MakeOld(root, "readonly.tmp");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            string recent = MakeOld(root, "recent.tmp");
            File.SetLastWriteTimeUtc(recent, now);
            string outsideFile = MakeOld(outside, "outside.tmp");
            string hardlinkSource = MakeOld(root, "hardlink-source.tmp");
            Check(CreateHardLink(Path.Combine(root, "hardlink.tmp"), hardlinkSource, IntPtr.Zero), "Hardlink fixture creation");
            string nested = Path.Combine(root, "nested"); Directory.CreateDirectory(nested);
            string nestedFile = MakeOld(nested, "nested.tmp");
            string? linkDirectory = null;
            try { linkDirectory = Path.Combine(root, "linked"); Directory.CreateSymbolicLink(linkDirectory, outside); }
            catch (UnauthorizedAccessException) { linkDirectory = null; }
            catch (IOException) { linkDirectory = null; }
            Check(!FileCleaner.IsInside(outsideFile, root), "Sibling root prefix must be rejected");
            Check(!FileCleaner.IsInside(Path.Combine(root, "..", "temp-other", "outside.tmp"), root), "Traversal must be rejected");
            var plan = FileCleaner.Scan([("Fixture", root)], now);
            var files = plan.Groups.Single().Files;
            Check(files.Count == 6, $"Only old ordinary temporary files are eligible (found {files.Count})");
            files.Single(f => f.Path == kept).IsSelected = false;
            Check(!files.Any(f => f.Path == code || f.Path == recent || f.Path == readOnly || f.Path == hardlinkSource), "Protected files omitted");
            Check(files.All(f => FileCleaner.IsInside(f.Path, root)), "Links must not escape root");
            File.AppendAllText(changed, " changed after scan");
            File.Delete(replaced); MakeOld(root, "replaced.tmp");
            using (var active = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = FileCleaner.Delete(files, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root }, now);
                Check(result.Deleted == 2 && result.Skipped == 3, $"Expected two deletions and three skips, got {result.Deleted}/{result.Skipped}");
                Check(!File.Exists(old) && !File.Exists(nestedFile), "Only approved files deleted");
                Check(File.Exists(kept), "Individually deselected file must be preserved");
                Check(File.Exists(changed) && File.Exists(replaced) && File.Exists(locked), "Changed, replaced and busy files preserved");
                Check(result.Bytes == 2 * new System.Text.UTF8Encoding(false).GetByteCount("disposable test data"), "Actual deleted byte count");
            }
            Check(File.Exists(outsideFile) && File.Exists(code) && File.Exists(recent) && File.Exists(readOnly) && File.Exists(hardlinkSource), "Excluded files preserved");
            var rejected = FileCleaner.Delete([files.Single(f => f.Path == locked)], new HashSet<string>(), now);
            Check(rejected.Deleted == 0 && File.Exists(locked), "Unapproved cleanup root refused");
            if (linkDirectory is not null) Directory.Delete(linkDirectory);
        }
        finally
        {
            // The only recursive removal is the canonical, uniquely generated fixture below this build directory.
            string full = Path.GetFullPath(fixture);
            if (!FileCleaner.IsInside(full, AppContext.BaseDirectory) || !Path.GetFileName(full).StartsWith("cleanup-test-", StringComparison.Ordinal))
                throw new Exception("Invalid test cleanup path");
            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.SetAttributes(path, FileAttributes.Normal);
            string link = Path.Combine(root, "linked");
            if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) Directory.Delete(link);
            Directory.Delete(full, recursive: true);
        }
    }
}
