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
        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS BookProgress (
    Name TEXT NOT NULL,
    Author TEXT NOT NULL,
    DurChapterIndex INTEGER NOT NULL,
    DurChapterPos INTEGER NOT NULL,
    DurChapterTime INTEGER NOT NULL,
    DurChapterTitle TEXT NOT NULL,
    PRIMARY KEY (Name, Author)
);
";
        command.ExecuteNonQuery();
    }

    public void Save(BookProgress progress)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO BookProgress (Name, Author, DurChapterIndex, DurChapterPos, DurChapterTime, DurChapterTitle)
VALUES ($name, $author, $index, $pos, $time, $title)
ON CONFLICT(Name, Author) DO UPDATE SET
    DurChapterIndex = excluded.DurChapterIndex,
    DurChapterPos = excluded.DurChapterPos,
    DurChapterTime = excluded.DurChapterTime,
    DurChapterTitle = excluded.DurChapterTitle;
";
        command.Parameters.AddWithValue("$name", progress.Name);
        command.Parameters.AddWithValue("$author", progress.Author);
        command.Parameters.AddWithValue("$index", progress.DurChapterIndex);
        command.Parameters.AddWithValue("$pos", progress.DurChapterPos);
        command.Parameters.AddWithValue("$time", progress.DurChapterTime);
        command.Parameters.AddWithValue("$title", progress.DurChapterTitle);
        command.ExecuteNonQuery();
    }
}