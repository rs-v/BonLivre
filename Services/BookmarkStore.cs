using System.Text;
using BonLivre.Models;
using LiteDB;

namespace BonLivre.Services;

internal sealed class BookmarkStore
{
    private readonly object _lock = new();
    private readonly ILiteCollection<BsonDocument> _bookmarks;
    private long _nextId;

    public BookmarkStore(LiteDbStore store)
    {
        _bookmarks = store.Database.GetCollection("bookmarks");
        _bookmarks.EnsureIndex("locationKey", "$.locationKey", unique: true);
        _nextId = _bookmarks.FindAll().Select(bookmark => bookmark["_id"].AsInt64).DefaultIfEmpty().Max() + 1;
    }

    public List<Bookmark> GetByBookUrl(string bookUrl)
    {
        return _bookmarks.Find(Query.EQ("bookUrl", bookUrl))
            .Select(ToBookmark)
            .OrderByDescending(bookmark => bookmark.CreatedAt)
            .ThenByDescending(bookmark => bookmark.Id)
            .ToList();
    }

    /// <summary>返回 null 表示该位置已有书签。</summary>
    public Bookmark? Create(string bookUrl, int chapterIndex, int chapterPos)
    {
        lock (_lock)
        {
            var locationKey = CreateLocationKey(bookUrl, chapterIndex, chapterPos);
            if (_bookmarks.Exists(Query.EQ("locationKey", locationKey))) return null;

            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var bookmark = new Bookmark(_nextId++, bookUrl, chapterIndex, chapterPos, createdAt);
            _bookmarks.Insert(ToDocument(bookmark, locationKey));
            return bookmark;
        }
    }

    public bool Delete(string bookUrl, long id)
    {
        lock (_lock)
        {
            var bookmark = _bookmarks.FindById(id);
            if (bookmark == null || bookmark["bookUrl"].AsString != bookUrl) return false;
            return _bookmarks.Delete(id);
        }
    }

    private static Bookmark ToBookmark(BsonDocument bookmark) => new(
        bookmark["_id"].AsInt64,
        bookmark["bookUrl"].AsString,
        bookmark["chapterIndex"].AsInt32,
        bookmark["chapterPos"].AsInt32,
        bookmark["createdAt"].AsInt64);

    private static BsonDocument ToDocument(Bookmark bookmark, string locationKey) => new()
    {
        ["_id"] = bookmark.Id,
        ["bookUrl"] = bookmark.BookUrl,
        ["chapterIndex"] = bookmark.ChapterIndex,
        ["chapterPos"] = bookmark.ChapterPos,
        ["createdAt"] = bookmark.CreatedAt,
        ["locationKey"] = locationKey
    };

    private static string CreateLocationKey(string bookUrl, int chapterIndex, int chapterPos) =>
        $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(bookUrl))}:{chapterIndex}:{chapterPos}";
}
