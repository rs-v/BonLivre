using System.Text.Json.Serialization;

namespace BonLivre.Models;

public record Book(
    string Name,
    string Author,
    string BookUrl,
    string TocUrl,
    string Origin,
    string OriginName,
    int Type = 0,
    int Group = 0,
    string LatestChapterTitle = "未更新",
    long LatestChapterTime = 0,
    long LastCheckTime = 0,
    int LastCheckCount = 0,
    int TotalChapterNum = 0,
    string DurChapterTitle = "未阅读",
    int DurChapterIndex = 0,
    int DurChapterPos = 0,
    long DurChapterTime = 0,
    bool CanUpdate = true,
    int Order = 0,
    int OriginOrder = 0,
    long SyncTime = 0,
    // 本地书籍文件 LastWriteTimeUtc 的 Unix 毫秒，与 DurChapterTime 同口径，供前端按导入时间排序。
    long ImportedAt = 0,
    string? CoverUrl = null,
    string? Intro = ""
);

public record BookProgress(
    string Name,
    string Author,
    int DurChapterIndex,
    int DurChapterPos,
    long DurChapterTime,
    string DurChapterTitle,
    string BookUrl = ""
);

public record Bookmark(
    long Id,
    string BookUrl,
    int ChapterIndex,
    int ChapterPos,
    long CreatedAt
);

public record CreateBookmarkRequest(string BookUrl, int ChapterIndex, int ChapterPos);

public record DeleteBookmarkRequest(string BookUrl, long Id);

public record BookChapter(
    string Title,
    string Url,
    int Index,
    bool IsVolume = false,
    string BaseUrl = "",
    string BookUrl = "",
    bool IsVip = false,
    bool IsPay = false,
    int? ContentLength = null
);

public record BookContentSearchResult(
    int ChapterIndex,
    string ChapterTitle,
    int ChapterPos,
    string Snippet
);

// API 响应包装类
public record LeagdoApiResponse<T>(bool IsSuccess, string ErrorMsg, T Data);

public record SearchRequest(string Key);

public record SearchBook(
    string Name,
    string Author,
    string BookUrl,
    string Origin,
    string OriginName,
    int Type = 0,
    string? TocUrl = null,
    long Time = 0,
    int OriginOrder = 0,
    string? CoverUrl = null,
    string? Intro = "",
    string? LatestChapterTitle = ""
);
