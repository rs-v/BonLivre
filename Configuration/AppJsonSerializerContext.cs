using System.Text.Json.Serialization;
using BonLivre.Models;

namespace BonLivre.Configuration;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Book))]
[JsonSerializable(typeof(BookProgress))]
[JsonSerializable(typeof(BookChapter))]
[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(SearchBook))]
[JsonSerializable(typeof(List<Book>))]
[JsonSerializable(typeof(List<BookChapter>))]
[JsonSerializable(typeof(List<SearchBook>))]
[JsonSerializable(typeof(LeagdoApiResponse<string>))]
[JsonSerializable(typeof(LeagdoApiResponse<List<Book>>))]
[JsonSerializable(typeof(LeagdoApiResponse<List<BookChapter>>))]
[JsonSerializable(typeof(LeagdoApiResponse<List<object>>))]
[JsonSerializable(typeof(List<object>))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
