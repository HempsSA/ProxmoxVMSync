using System.IO;
using Microsoft.Data.Sqlite;
using DarkSync.Models;

namespace DarkSync.Services;

public static class HistoryService
{
    private static string DbPath => Path.Combine(AppContext.BaseDirectory, "darksync_history.db");

    public static void Initialize()
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                operation TEXT NOT NULL,
                result TEXT NOT NULL,
                details TEXT DEFAULT '',
                source_count INTEGER DEFAULT 0,
                external_count INTEGER DEFAULT 0,
                actions INTEGER DEFAULT 0,
                warnings INTEGER DEFAULT 0,
                vm_snapshot TEXT DEFAULT '[]'
            );
        """;
        cmd.ExecuteNonQuery();
    }

    public static List<HistoryEntry> LoadAll()
    {
        var entries = new List<HistoryEntry>();
        if (!File.Exists(DbPath)) return entries;

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, timestamp, operation, result, details, source_count, external_count, actions, warnings, vm_snapshot FROM history ORDER BY id DESC LIMIT 1000";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new HistoryEntry
            {
                Id = reader.GetInt64(0),
                Timestamp = reader.GetString(1),
                Operation = reader.GetString(2),
                Result = reader.GetString(3),
                Details = reader.IsDBNull(4) ? "" : reader.GetString(4),
                SourceCount = reader.GetInt32(5),
                ExternalCount = reader.GetInt32(6),
                Actions = reader.GetInt32(7),
                Warnings = reader.GetInt32(8),
                VmSnapshotJson = reader.IsDBNull(9) ? "[]" : reader.GetString(9)
            });
        }
        return entries;
    }

    public static void Add(string operation, string result, string details = "",
        int sourceCount = 0, int externalCount = 0, int actions = 0, int warnings = 0,
        string vmSnapshotJson = "[]")
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        Initialize();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history (timestamp, operation, result, details, source_count, external_count, actions, warnings, vm_snapshot)
            VALUES (@ts, @op, @res, @det, @src, @ext, @act, @warn, @vm);
        """;
        cmd.Parameters.AddWithValue("@ts", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
        cmd.Parameters.AddWithValue("@op", operation);
        cmd.Parameters.AddWithValue("@res", result);
        cmd.Parameters.AddWithValue("@det", details.Length > 4000 ? details[..4000] : details);
        cmd.Parameters.AddWithValue("@src", sourceCount);
        cmd.Parameters.AddWithValue("@ext", externalCount);
        cmd.Parameters.AddWithValue("@act", actions);
        cmd.Parameters.AddWithValue("@warn", warnings);
        cmd.Parameters.AddWithValue("@vm", vmSnapshotJson);
        cmd.ExecuteNonQuery();
    }

    public static void Clear()
    {
        if (!File.Exists(DbPath)) return;
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM history";
        cmd.ExecuteNonQuery();
    }
}
