using System.Text.Json.Serialization;
using BonLivre.Models;

namespace BonLivre.Configuration;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Book))]
[JsonSerializable(typeof(BookProgress))]
[JsonSerializable(typeof(Bookmark))]
[JsonSerializable(typeof(CreateBookmarkRequest))]
[JsonSerializable(typeof(DeleteBookmarkRequest))]
[JsonSerializable(typeof(BookChapter))]
[JsonSerializable(typeof(BookContentSearchResult))]
[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(SearchBook))]
[JsonSerializable(typeof(List<Book>))]
[JsonSerializable(typeof(List<Bookmark>))]
[JsonSerializable(typeof(List<BookChapter>))]
[JsonSerializable(typeof(List<BookContentSearchResult>))]
[JsonSerializable(typeof(List<SearchBook>))]
[JsonSerializable(typeof(LeagdoApiResponse<string>))]
[JsonSerializable(typeof(LeagdoApiResponse<Bookmark>))]
[JsonSerializable(typeof(LeagdoApiResponse<List<Book>>))]
[JsonSerializable(typeof(LeagdoApiResponse<List<Bookmark>>))]
[JsonSerializable(typeof(LeagdoApiResponse<List<BookChapter>>))]
[JsonSerializable(typeof(LeagdoApiResponse<List<BookContentSearchResult>>))]
[JsonSerializable(typeof(LeagdoApiResponse<List<object>>))]
[JsonSerializable(typeof(List<object>))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
