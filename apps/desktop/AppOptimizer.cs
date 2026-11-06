namespace PCBoost;

public sealed record AppBoostResult(int Completed, int Skipped)
{
    public string Message(string app) => Completed > 0 ? $"Boost aplicado em {app}. O app continua aberto."
        : $"Não foi possível aplicar o Boost em {app}. Atualize a lista e tente novamente.";
}

public static class AppOptimizer
{
    public static bool CanBoost(AppRow app) => app.Path is not null && app.Processes.Count > 0
        && app.Processes.All(p => !p.Protected && !BoostScanner.IsSensitive(p.Name));

    public static AppBoostResult Boost(AppRow app, CancellationToken cancellation = default)
    {
        if (!CanBoost(app)) return new(0, app.Count);
        int completed = 0, skipped = 0;
        foreach (var row in app.Processes)
        {
            cancellation.ThrowIfCancellationRequested();
            try { Monitor.TrimWorkingSet(row); completed++; }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or NotSupportedException)
            { skipped++; }
        }
        return new(completed, skipped);
    }
}
