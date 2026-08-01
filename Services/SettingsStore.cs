using LiteDB;

namespace BonLivre.Services;

/// <summary>简单的 key-value 设置存储（LiteDB），用于持久化重启需要保留的应用级设置，
/// 目前主要是前端的阅读配置（字体、主题等）。与 <see cref="BookProgressStore"/> 共用 data/ 目录。</summary>
internal sealed class SettingsStore
{
    private readonly ILiteCollection<BsonDocument> _settings;

    public SettingsStore(LiteDbStore store)
    {
        _settings = store.Database.GetCollection("settings");
    }

    /// <summary>取设置值；不存在时返回 <paramref name="defaultValue"/>。</summary>
    public string Get(string key, string defaultValue)
    {
        var setting = _settings.FindById(key);
        return setting?["value"].AsString ?? defaultValue;
    }

    /// <summary>写入设置值（存在则覆盖）。</summary>
    public void Set(string key, string value)
    {
        _settings.Upsert(new BsonDocument
        {
            ["_id"] = key,
            ["value"] = value
        });
    }
}
