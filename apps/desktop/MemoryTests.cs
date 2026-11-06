namespace PCBoost;

internal static class MemoryTests
{
    public static void Run()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        ProcessRow Row(int pid, string? path, long? privateBytes) => new(pid, "example", "example.exe", path,
            DateTime.UnixEpoch, 200, 1, false, "", 1) { PrivateBytes = privateBytes };
        var snapshot = new Snapshot(10000, 4000,
            [Row(1, @"C:\apps\example.exe", 100), Row(2, @"c:\APPS\example.exe", 150),
             Row(3, @"C:\other\example.exe", 50), Row(4, null, null), Row(5, null, null)]);
        var apps = MemoryAnalysis.Group(snapshot);
        Check(apps.Count == 4, "Group by executable path; keep unknown origins separate");
        Check(apps[0].Count == 2 && apps[0].PrivateBytes == 250, "Private memory aggregation");
        Check(apps[0].PrivateBytes != apps[0].Processes.Sum(p => p.Bytes), "Do not substitute total working set for private RAM");
        var memory = MemoryBreakdown.From(snapshot);
        Check(memory.PrivateRead == 300 && memory.Remainder == 5700 && memory.MissingReadings == 2, "Physical RAM reconciliation with missing readings");
        Check(apps.Count(a => a.PrivateBytes is null && a.MemoryLabel == "—") == 2, "Unavailable readings must not become zeros");
        var partial = MemoryAnalysis.Group(new Snapshot(10000, 4000,
            [Row(1, @"C:\apps\example.exe", 100), Row(2, @"C:\apps\example.exe", null)])).Single();
        Check(partial.PrivateBytes == 100 && partial.MissingReadings == 1 && partial.MemoryLabel.StartsWith("≥"), "Partial readings must be labeled");
        Check(MemoryBreakdown.From(new Snapshot(100, 90, [Row(1, "C:\\a.exe", 50)])).Inconsistent, "Non-atomic snapshot inconsistency must be detected");
        var target = new AppRow { Key = "target", DisplayName = "Example", Description = "", TotalPhysical = 10000,
            Processes = [Row(int.MaxValue, @"C:\apps\example.exe", 100)] };
        Check(AppOptimizer.CanBoost(target), "Ordinary user app is eligible for targeted boost");
        var failed = AppOptimizer.Boost(target);
        Check(failed.Completed == 0 && failed.Skipped == 1, "Exited or nonexistent processes must be reported as skipped");
        var protectedTarget = new AppRow { Key = "protected", DisplayName = "Protected", Description = "", TotalPhysical = 10000,
            Processes = [Row(100, @"C:\apps\example.exe", 100) with { Protected = true }] };
        Check(!AppOptimizer.CanBoost(protectedTarget), "Protected app cannot receive a targeted boost");
        var sensitive = new AppRow { Key = "sensitive", DisplayName = "Security", Description = "", TotalPhysical = 10000,
            Processes = [Row(100, @"C:\apps\security.exe", 100) with { Name = "MsMpEng" }] };
        Check(!AppOptimizer.CanBoost(sensitive), "Sensitive apps must be excluded from targeted boost");
    }
}
