using Microsoft.Data.Sqlite;

namespace Campfire.Features.Operations;

public static class BackupOperations
{
    public static void PrepareBackup(IConfiguration configuration)
    {
        var storage = Path.GetFullPath(configuration["CAMPFIRE_STORAGE"] ?? "./storage");
        var railsEnvironment = configuration["RAILS_ENV"] ?? "production";
        if (railsEnvironment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || railsEnvironment.Contains('/') || railsEnvironment.Contains('\\'))
            throw new ArgumentException("RAILS_ENV must be a filename component.");
        var filename = railsEnvironment + ".sqlite3";
        var sourcePath = Path.Combine(storage, "db", filename);
        var destinationPath = Path.Combine(storage, "backups", filename);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 30
        }.ToString());
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath, Pooling = false, DefaultTimeout = 30
        }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }
}
