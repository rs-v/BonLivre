using System.IO;
using BonLivre.Models;
using Microsoft.Data.Sqlite;

namespace BonLivre.Services;

internal sealed class BookProgressStore
{
    private readonly string _connectionString;

    public BookProgressStore()
    {
        var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "bookprogress.sqlite");
        _connectionString = $"Data Source={dbPath}";
        EnsureTable();
    }

    private void EnsureTable()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // 进度以 BookUrl（唯一的 local://文件名）为主键。旧版本用 (Name, Author) 作主键，
        // 但所有 EPUB 的 Author 都是 "EPUB"、TXT 都是 "本地作者"，同名文件会在进度上互相覆盖。
        if (TableExists(connection, "BookProgress") && !ColumnExists(connection, "BookProgress", "BookUrl"))
        {
            MigrateToBookUrlKey(connection);
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS BookProgress (
    BookUrl TEXT NOT NULL PRIMARY KEY,
    Name TEXT NOT NULL,
    Author TEXT NOT NULL,
    DurChapterIndex INTEGER NOT NULL,
    DurChapterPos INTEGER NOT NULL,
    DurChapterTime INTEGER NOT NULL,
    DurChapterTitle TEXT NOT NULL
);
";
        command.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() != null;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // table_info 的第 2 列（索引 1）是列名。
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 把旧的 (Name, Author) 主键表重建为 BookUrl 主键表。旧行没有 BookUrl，用
    /// 'local://' || Name 合成一个稳定且唯一的键——不含扩展名故不一定与真实 BookUrl 相等，
    /// 但保证不丢进度：GetAllProgress 返回后，getBookshelf 会先按 BookUrl 匹配、再回退到
    /// (Name, Author) 匹配，迁移行仍能正确合并。同名冲突按 DurChapterTime 升序插入，
    /// INSERT OR REPLACE 使最近阅读的一行胜出。整个迁移在事务内执行，中途失败不丢原表。
    /// </summary>
    private static void MigrateToBookUrlKey(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
DROP TABLE IF EXISTS BookProgress_new;
CREATE TABLE BookProgress_new (
    BookUrl TEXT NOT NULL PRIMARY KEY,
    Name TEXT NOT NULL,
    Author TEXT NOT NULL,
    DurChapterIndex INTEGER NOT NULL,
    DurChapterPos INTEGER NOT NULL,
    DurChapterTime INTEGER NOT NULL,
    DurChapterTitle TEXT NOT NULL
);
INSERT OR REPLACE INTO BookProgress_new (BookUrl, Name, Author, DurChapterIndex, DurChapterPos, DurChapterTime, DurChapterTitle)
SELECT 'local://' || Name, Name, Author, DurChapterIndex, DurChapterPos, DurChapterTime, DurChapterTitle
FROM BookProgress ORDER BY DurChapterTime ASC;
DROP TABLE BookProgress;
ALTER TABLE BookProgress_new RENAME TO BookProgress;
";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Save(BookProgress progress)
    {
        // 旧客户端可能不带 BookUrl；空串作主键会让所有书的进度写进同一行、互相覆盖。
        // 回退到与 MigrateToBookUrlKey 一致的合成键 'local://' + Name，
        // getBookshelf 的 (Name, Author) 回退匹配仍能读到这行进度。
        var bookUrl = string.IsNullOrEmpty(progress.BookUrl)
            ? $"local://{progress.Name}"
            : progress.BookUrl;

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO BookProgress (BookUrl, Name, Author, DurChapterIndex, DurChapterPos, DurChapterTime, DurChapterTitle)
VALUES ($bookUrl, $name, $author, $index, $pos, $time, $title)
ON CONFLICT(BookUrl) DO UPDATE SET
    Name = excluded.Name,
    Author = excluded.Author,
    DurChapterIndex = excluded.DurChapterIndex,
    DurChapterPos = excluded.DurChapterPos,
    DurChapterTime = excluded.DurChapterTime,
    DurChapterTitle = excluded.DurChapterTitle;
";
        command.Parameters.AddWithValue("$bookUrl", bookUrl);
        command.Parameters.AddWithValue("$name", progress.Name);
        command.Parameters.AddWithValue("$author", progress.Author);
        command.Parameters.AddWithValue("$index", progress.DurChapterIndex);
        command.Parameters.AddWithValue("$pos", progress.DurChapterPos);
        command.Parameters.AddWithValue("$time", progress.DurChapterTime);
        command.Parameters.AddWithValue("$title", progress.DurChapterTitle);
        command.ExecuteNonQuery();
    }

    public List<BookProgress> GetAllProgress()
    {
        var result = new List<BookProgress>();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT BookUrl, Name, Author, DurChapterIndex, DurChapterPos, DurChapterTime, DurChapterTitle FROM BookProgress";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new BookProgress(
                BookUrl: reader.GetString(0),
                Name: reader.GetString(1),
                Author: reader.GetString(2),
                DurChapterIndex: reader.GetInt32(3),
                DurChapterPos: reader.GetInt32(4),
                DurChapterTime: reader.GetInt64(5),
                DurChapterTitle: reader.GetString(6)
            ));
        }
        return result;
    }
}

