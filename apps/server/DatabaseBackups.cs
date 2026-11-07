using Microsoft.Data.Sqlite;

namespace PCBoost.Server;

public sealed class DatabaseBackups(Db db, IConfiguration config, ILogger<DatabaseBackups> log) : BackgroundService
{
    public static void Verify(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        if (check.ExecuteScalar()?.ToString() != "ok") throw new InvalidDataException("Backup SQLite corrompido.");
        check.CommandText = "PRAGMA foreign_key_check";
        using var reader = check.ExecuteReader();
        if (reader.Read()) throw new InvalidDataException("Backup com referências inválidas.");
        reader.Close();
        // Restaura em disco para não carregar todo o banco na RAM do servidor.
        string restoredPath = Path.Combine(Path.GetTempPath(), "pcboost-restore-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var restored = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = restoredPath, Pooling = false }.ToString());
            restored.Open(); connection.BackupDatabase(restored);
            using var probe = restored.CreateCommand();
            foreach (string table in new[] { "users", "tokens", "groups", "members", "channels", "messages", "ideas", "votes" })
            { probe.CommandText = $"SELECT COUNT(*) FROM {table}"; probe.ExecuteScalar(); }
        }
        finally { if (File.Exists(restoredPath)) File.Delete(restoredPath); }
    }
    public static string Create(Db db, string folder)
    {
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, $"pcboost-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        string partial = target + ".partial";
        try
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db.PathOnDisk, Mode = SqliteOpenMode.ReadOnly }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = partial, Pooling = false }.ToString()))
            { source.Open(); destination.Open(); source.BackupDatabase(destination); }
            Verify(partial);
            File.Move(partial, target);
            return target;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string folder = config["PCBOOST_BACKUP_DIR"] ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db.PathOnDisk))!, "backups");
        int hours = Math.Clamp(config.GetValue("PCBOOST_BACKUP_HOURS", 6), 1, 168);
        int keep = Math.Clamp(config.GetValue("PCBOOST_BACKUP_KEEP", 28), 1, 1000);
        do
        {
            try
            {
                string backup = await Task.Run(() => Create(db, folder), stoppingToken);
                log.LogInformation("Backup SQLite verificado: {Path}", backup);
                foreach (var old in new DirectoryInfo(folder).GetFiles("pcboost-*.db").OrderByDescending(f => f.CreationTimeUtc).Skip(keep)) old.Delete();
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested) { log.LogError(e, "Falha no backup SQLite"); }
            await Task.Delay(TimeSpan.FromHours(hours), stoppingToken);
        } while (!stoppingToken.IsCancellationRequested);
    }
}
