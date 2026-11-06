using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PCBoost;

// PCBoost.exe --boost-test: liga e desliga o boost e confere que energia e prioridades voltaram ao estado anterior.
internal static class AutoBoostTest
{
    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static Guid Plan() { PowerGetActiveScheme(IntPtr.Zero, out var p); try { return Marshal.PtrToStructure<Guid>(p); } finally { LocalFree(p); } }
    private static Dictionary<(int, DateTime), ProcessPriorityClass> Priorities()
    {
        var map = new Dictionary<(int, DateTime), ProcessPriorityClass>();
        foreach (var p in Process.GetProcesses()) using (p) { try { map[(p.Id, p.StartTime)] = p.PriorityClass; } catch (Exception) { } }
        return map;
    }
    public static async Task<string> Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcboost-boost-test"); Directory.CreateDirectory(root);
        Guid? overlayBefore = PowerGetEffectiveOverlayScheme(out var o) == 0 ? o : null;
        var before = Priorities(); Guid planBefore = Plan();
        var boost = new AutoBoost(root);
        await boost.Activate();
        if (!boost.Active) throw new Exception("Boost did not activate.");
        Guid? overlayDuring = PowerGetEffectiveOverlayScheme(out var d) == 0 ? d : null;
        var during = Priorities();
        int lowered = during.Count(kv => kv.Value == ProcessPriorityClass.BelowNormal && before.TryGetValue(kv.Key, out var b) && b == ProcessPriorityClass.Normal);
        string summary = boost.Summary;
        boost.Deactivate();
        var after = Priorities();
        Guid? overlayAfter = PowerGetEffectiveOverlayScheme(out var a) == 0 ? a : null;
        int notRestored = after.Count(kv => before.TryGetValue(kv.Key, out var b) && b == ProcessPriorityClass.Normal && kv.Value == ProcessPriorityClass.BelowNormal);
        if (notRestored > 0) throw new Exception($"{notRestored} processes still below normal after deactivate.");
        if (Plan() != planBefore) throw new Exception("Power plan not restored.");
        if (overlayBefore != overlayAfter) throw new Exception($"Power overlay not restored: {overlayBefore} -> {overlayAfter}.");
        if (File.Exists(Path.Combine(root, "boost-restore.json"))) throw new Exception("Restore marker left behind.");
        return $"PASS: activate → {summary}; {lowered} processes lowered during boost; plan {planBefore} restored; overlay {overlayBefore} → {overlayDuring} → {overlayAfter}; all priorities and power restored after deactivate.";
    }
}
