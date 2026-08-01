using LiteDB;

namespace BonLivre.Services;

/// <summary>应用进程共享的 LiteDB 数据库。</summary>
internal sealed class LiteDbStore : IDisposable
{
    public LiteDatabase Database { get; }

    public LiteDbStore()
    {
        var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
        Directory.CreateDirectory(dataDir);
        Database = new LiteDatabase(Path.Combine(dataDir, "bonlivre.db"));
    }

    public void Dispose() => Database.Dispose();
}
