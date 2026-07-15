using System.IO;
using Microsoft.Data.Sqlite;

namespace BonLivre.Services;

/// <summary>
/// 简单的 key-value 设置存储（SQLite），用于持久化重启需要保留的应用级设置，
/// 目前主要是前端的阅读配置（字体、主题等）。与 <see cref="BookProgressStore"/> 共用 data/ 目录。
/// </summary>
internal sealed class SettingsStore
{
    private readonly string _connectionString;

    public SettingsStore()
    {
        var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "settings.sqlite");
        _connectionString = $"Data Source={dbPath}";
        EnsureTable();
    }

    private void EnsureTable()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS Settings (
    Key TEXT NOT NULL PRIMARY KEY,
    Value TEXT NOT NULL
);
";
        command.ExecuteNonQuery();
    }

    /// <summary>取设置值；不存在时返回 <paramref name="defaultValue"/>。</summary>
    public string Get(string key, string defaultValue)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = $key";
        command.Parameters.AddWithValue("$key", key);
        var result = command.ExecuteScalar();
        return result as string ?? defaultValue;
    }

    /// <summary>写入设置值（存在则覆盖）。</summary>
    public void Set(string key, string value)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO Settings (Key, Value)
VALUES ($key, $value)
ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}
