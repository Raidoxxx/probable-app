using System.Diagnostics;
using System.IO;

namespace PCBoost;

internal static class SelfTest
{
    public static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var snapshot = Monitor.Capture(new HashSet<string>());
        Check(snapshot.Total > 0 && snapshot.Available <= snapshot.Total, "Invalid physical memory values");
        Check(snapshot.Processes.Count > 0, "No live processes found");
        Check(snapshot.Processes.Zip(snapshot.Processes.Skip(1)).All(pair => pair.First.Bytes >= pair.Second.Bytes), "RAM ordering");
        var self = snapshot.Processes.Single(p => p.Id == Environment.ProcessId);
        Check(self.Protected, "Self protection");
        Check(Monitor.IsProtected(100, "csrss", @"C:\apps\csrss.exe", DateTime.UtcNow, 1), "Essential process protection");
        Check(Monitor.IsProtected(100, "unknown", null, null, 1), "Unknown process protection");
        Check(Monitor.IsProtected(100, "example", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "example.exe"), DateTime.UtcNow, self.SessionId), "Windows directory protection");
        Check(!Monitor.IsProtected(100, "example", @"C:\apps\example.exe", DateTime.UtcNow, self.SessionId), "User app should be eligible");
        Check(Monitor.IsProtected(100, "example", @"C:\apps\example.exe", DateTime.UtcNow, self.SessionId + 1), "Other session protection");
        bool rejected = false;
        try { Monitor.Terminate(self); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Self termination must be refused");
        bool selfTrimRejected = false;
        try { Monitor.TrimWorkingSet(self); } catch (InvalidOperationException) { selfTrimRejected = true; }
        Check(selfTrimRejected, "Self memory trim must be refused");
        using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            { Arguments = "--test-child", UseShellExecute = false, CreateNoWindow = true })!)
        {
            try
            {
                var childRow = Monitor.Capture(new HashSet<string>()).Processes.Single(p => p.Id == child.Id);
                Check(!childRow.Protected, "Dedicated test child should be eligible");
                bool staleRejected = false;
                try { Monitor.Terminate(childRow with { StartedUtc = DateTime.UnixEpoch }); }
                catch (InvalidOperationException) { staleRejected = true; }
                Check(staleRejected && !child.HasExited, "Reused PID / stale identity must be refused");
                bool staleTrimRejected = false;
                try { Monitor.TrimWorkingSet(childRow with { StartedUtc = DateTime.UnixEpoch }); }
                catch (InvalidOperationException) { staleTrimRejected = true; }
                Check(staleTrimRejected, "Stale identity must refuse memory trimming");
                Monitor.TrimWorkingSet(childRow);
                Check(!child.HasExited, "Memory Boost must leave the dedicated test app running");
                Monitor.Terminate(childRow);
                Check(child.WaitForExit(5000), "Dedicated test child termination");
            }
            finally { if (!child.HasExited) child.Kill(); }
        }
        string testFile = Path.Combine(AppContext.BaseDirectory, "test-watchlist.json");
        try
        {
            var store = new WatchStore(testFile);
            store.Toggle(@"C:\apps\example.exe");
            Check(new WatchStore(testFile).Paths.Contains(@"c:\APPS\example.exe"), "Persistence and case-insensitive identity");
            store.Toggle(@"c:\apps\example.exe");
            Check(new WatchStore(testFile).Paths.Count == 0, "Watch removal persistence");
            File.WriteAllText(testFile, "invalid json");
            Check(new WatchStore(testFile).LoadWarning is not null, "Corrupt watchlist recovery");
        }
        finally { if (File.Exists(testFile)) File.Delete(testFile); }
        BoostTests.Run();
        MemoryTests.Run();
        if (snapshot.Processes.Any(p => p.PrivateBytes is not null))
            Check(self.PrivateBytes is > 0, "Native private working set reading for the running app");
        File.WriteAllText("self-test-result.txt", $"PASS: live memory, {snapshot.Processes.Count} processes, ordering, protected processes, self termination refusal, stale identity refusal, dedicated child termination, watchlist persistence, corrupt data recovery. Boost: activity/window rules, system/sync exclusions, old temporary file cleanup, busy/changed/replaced/read-only/recent/executable/hardlink protections, root boundary checks.");
    }
}
