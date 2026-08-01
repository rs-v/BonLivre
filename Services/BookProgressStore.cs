using BonLivre.Models;
using LiteDB;

namespace BonLivre.Services;

internal sealed class BookProgressStore
{
    private readonly ILiteCollection<BsonDocument> _progress;

    public BookProgressStore(LiteDbStore store)
    {
        _progress = store.Database.GetCollection("bookProgress");
    }

    public void Save(BookProgress progress)
    {
        // 旧客户端可能不带 BookUrl；空串作主键会让所有书的进度写进同一行、互相覆盖。
        // 回退到合成键 'local://' + Name，getBookshelf 的 (Name, Author) 回退匹配仍能读到这行进度。
        var bookUrl = string.IsNullOrEmpty(progress.BookUrl)
            ? $"local://{progress.Name}"
            : progress.BookUrl;

        _progress.Upsert(new BsonDocument
        {
            ["_id"] = bookUrl,
            ["name"] = progress.Name,
            ["author"] = progress.Author,
            ["durChapterIndex"] = progress.DurChapterIndex,
            ["durChapterPos"] = progress.DurChapterPos,
            ["durChapterTime"] = progress.DurChapterTime,
            ["durChapterTitle"] = progress.DurChapterTitle
        });
    }

    public List<BookProgress> GetAllProgress()
    {
        return _progress.FindAll().Select(progress => new BookProgress(
            Name: progress["name"].AsString,
            Author: progress["author"].AsString,
            DurChapterIndex: progress["durChapterIndex"].AsInt32,
            DurChapterPos: progress["durChapterPos"].AsInt32,
            DurChapterTime: progress["durChapterTime"].AsInt64,
            DurChapterTitle: progress["durChapterTitle"].AsString,
            BookUrl: progress["_id"].AsString
        )).ToList();
    }
}
