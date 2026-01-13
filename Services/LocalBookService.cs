using System.Text.RegularExpressions;
using System.Text;
using System.Linq;
using HtmlAgilityPack;
using VersOne.Epub;
using BonLivre.Models;

namespace BonLivre.Services;

public class LocalBookService
{
    private readonly string _booksDir;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, EpubBook> _epubCache = new();

    public LocalBookService()
    {
        _booksDir = Path.Combine(Directory.GetCurrentDirectory(), "books");
        if (!Directory.Exists(_booksDir)) Directory.CreateDirectory(_booksDir);
    }

    private EpubBook GetOrAddEpub(string filePath)
    {
        return _epubCache.GetOrAdd(filePath, (path) => EpubReader.ReadBook(path));
    }

    public List<Book> GetLocalBooks()
    {
        var bookshelf = new List<Book>();
        var localFiles = Directory.GetFiles(_booksDir, "*.*")
            .Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var file in localFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var isEpub = file.EndsWith(".epub", StringComparison.OrdinalIgnoreCase);
            var book = new Book(
                Name: fileName,
                Author: isEpub ? "EPUB" : "本地作者",
                BookUrl: $"local://{Path.GetFileName(file)}",
                TocUrl: $"local://{Path.GetFileName(file)}",
                Origin: "local",
                OriginName: isEpub ? "EPUB" : "本地导入"
            );
            bookshelf.Add(book);
        }
        return bookshelf;
    }

    public List<BookChapter>? GetChapterList(string url)
    {
        var fileName = url.Replace("local://", "").Split('#')[0];
        var filePath = Path.Combine(_booksDir, fileName);

        if (!File.Exists(filePath)) return null;

        if (filePath.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        {
            var epubChapters = new List<BookChapter>();
            try
            {
                EpubBook book = GetOrAddEpub(filePath);
                int idx = 0;
                foreach (var textFile in book.ReadingOrder)
                {
                    string title = book.Navigation?.FirstOrDefault(n => n.Link?.ContentFilePath == textFile.FilePath)?.Title ?? $"章节 {idx + 1}";
                    epubChapters.Add(new BookChapter(title, $"{url}#epub#{textFile.FilePath}", idx++));
                }
                return epubChapters;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"EPUB TOC Error: {ex.Message}");
                return null;
            }
        }

        var content = File.ReadAllText(filePath, Encoding.UTF8);
        var chapters = new List<BookChapter>();
        var matches = Regex.Matches(content, @"^\s*(第[零一二三四五六七八九十百千万\d]+[章节回卷集部篇]).*$", RegexOptions.Multiline);

        if (matches.Count == 0)
        {
            chapters.Add(new BookChapter("正文", url, 0));
        }
        else
        {
            for (int i = 0; i < matches.Count; i++)
            {
                chapters.Add(new BookChapter(matches[i].Value.Trim(), $"{url}#{i}", i));
            }
        }
        return chapters;
    }

    public string? GetBookContent(string url, int index)
    {
        var rawUrl = url.Split('#')[0];
        var fileName = rawUrl.Replace("local://", "");
        var filePath = Path.Combine(_booksDir, fileName);

        if (!File.Exists(filePath)) return null;

        if (filePath.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var epubParts = url.Split("#epub#", StringSplitOptions.RemoveEmptyEntries);
                string? targetFile = epubParts.Length > 1 ? epubParts[1] : null;

                EpubBook book = GetOrAddEpub(filePath);
                string? htmlContent = null;
                if (targetFile != null)
                {
                    var foundFile = book.ReadingOrder.FirstOrDefault(f => f.FilePath == targetFile);
                    if (foundFile != null) htmlContent = foundFile.Content;
                }
                else if (index >= 0 && index < book.ReadingOrder.Count)
                {
                    htmlContent = book.ReadingOrder[index].Content;
                }

                if (!string.IsNullOrEmpty(htmlContent))
                {
                    var doc = new HtmlDocument();
                    doc.LoadHtml(htmlContent);
                    doc.DocumentNode.Descendants("script").ToList().ForEach(n => n.Remove());
                    doc.DocumentNode.Descendants("style").ToList().ForEach(n => n.Remove());

                    // 获取当前 HTML 文件的目录路径，用于解析图片的相对路径
                    string currentDir = "";
                    if (targetFile != null)
                    {
                        currentDir = Path.GetDirectoryName(targetFile) ?? "";
                    }
                    else if (index >= 0 && index < book.ReadingOrder.Count)
                    {
                        currentDir = Path.GetDirectoryName(book.ReadingOrder[index].FilePath) ?? "";
                    }
                    currentDir = currentDir.Replace("\\", "/");

                    var sb = new StringBuilder();
                    ExtractTextWithImages(doc.DocumentNode, sb, currentDir);
                    return HtmlEntity.DeEntitize(sb.ToString());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"EPUB Content Error: {ex.Message}");
                return null;
            }
        }

        var content = File.ReadAllText(filePath, Encoding.UTF8);
        var matches = Regex.Matches(content, @"^\s*(第[零一二三四五六七八九十百千万\d]+[章节回卷集部篇]).*$", RegexOptions.Multiline);

        if (matches.Count == 0) return content;

        if (index < 0 || index >= matches.Count) return null;

        int startPos = matches[index].Index;
        int endPos = (index + 1 < matches.Count) ? matches[index + 1].Index : content.Length;

        return content.Substring(startPos, endPos - startPos);
    }

    private void ExtractTextWithImages(HtmlNode node, StringBuilder sb, string currentDir)
    {
        if (node.Name == "img")
        {
            var src = node.GetAttributeValue("src", "");
            if (!string.IsNullOrEmpty(src))
            {
                // 处理相对路径，转换为 EPUB 内部的“绝对”路径
                string absolutePath = src;
                if (!src.StartsWith("http") && !src.StartsWith("data:"))
                {
                    try
                    {
                        // 简单的路径拼接和规范化
                        var combined = string.IsNullOrEmpty(currentDir) ? src : $"{currentDir}/{src}";
                        var uri = new Uri("http://epub/" + combined);
                        absolutePath = uri.AbsolutePath.TrimStart('/'); // 得到 OEBPS/Images/1.jpg
                    }
                    catch { }
                }
                sb.Append($"\n<img src=\"{absolutePath}\">\n");
            }
            return;
        }

        if (node.Name == "br" || node.Name == "p" || node.Name == "div")
        {
            sb.Append('\n');
        }

        if (node.HasChildNodes)
        {
            foreach (var child in node.ChildNodes)
            {
                ExtractTextWithImages(child, sb, currentDir);
            }
        }
        else if (node.NodeType == HtmlNodeType.Text)
        {
            sb.Append(node.InnerText);
        }

        if (node.Name == "p" || node.Name == "div")
        {
            sb.Append('\n');
        }
    }

    public (byte[] Content, string MimeType)? GetEpubResource(string bookUrl, string resourcePath)
    {
        try
        {
            var fileName = bookUrl.Replace("local://", "");
            fileName = System.Net.WebUtility.UrlDecode(fileName);
            var filePath = Path.Combine(_booksDir, fileName);

            if (!File.Exists(filePath))
            {
                Console.WriteLine($"[EPUB Resource] File not found: {filePath}");
                return null;
            }

            EpubBook book = GetOrAddEpub(filePath);

            // 规范化目标路径
            var targetPath = System.Net.WebUtility.UrlDecode(resourcePath).Replace("\\", "/").Trim().TrimStart('/');
            // 处理路径中的 ../
            if (targetPath.Contains("../"))
            {
                var parts = targetPath.Split('/');
                var stack = new Stack<string>();
                foreach (var part in parts)
                {
                    if (part == "..") { if (stack.Count > 0) stack.Pop(); }
                    else if (part != "." && !string.IsNullOrEmpty(part)) stack.Push(part);
                }
                var resolvedParts = stack.ToArray();
                Array.Reverse(resolvedParts);
                targetPath = string.Join("/", resolvedParts);
            }
            var targetFileName = Path.GetFileName(targetPath);

            // 1. 优先从图片目录查找 (VersOne.Epub 自动提取的图片资源)
            if (book.Content?.Images?.Local != null)
            {
                var imageMatch = book.Content.Images.Local.FirstOrDefault(f =>
                {
                    if (string.IsNullOrEmpty(f.FilePath)) return false;
                    var fPath = f.FilePath.Replace("\\", "/").Trim().TrimStart('/');
                    // 匹配逻辑：全匹配、后缀匹配、包含匹配、文件名匹配
                    return fPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase) ||
                           fPath.EndsWith("/" + targetPath, StringComparison.OrdinalIgnoreCase) ||
                           targetPath.EndsWith("/" + fPath, StringComparison.OrdinalIgnoreCase) ||
                           Path.GetFileName(fPath).Equals(targetFileName, StringComparison.OrdinalIgnoreCase);
                });

                if (imageMatch != null && imageMatch.Content != null)
                {
                    return (imageMatch.Content, imageMatch.ContentMimeType ?? GetMimeType(targetFileName));
                }
            }

            // 2. 备选方案：从所有本地文件查找 (处理一些未标注为 Image 的资源)
            var allFiles = book.Content?.AllFiles?.Local;
            if (allFiles != null)
            {
                var match = allFiles.FirstOrDefault(f =>
                {
                    if (string.IsNullOrEmpty(f.FilePath)) return false;
                    var fPath = f.FilePath.Replace("\\", "/").Trim().TrimStart('/');
                    return fPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase) ||
                           fPath.EndsWith("/" + targetPath, StringComparison.OrdinalIgnoreCase) ||
                           targetPath.EndsWith("/" + fPath, StringComparison.OrdinalIgnoreCase) ||
                           Path.GetFileName(fPath).Equals(targetFileName, StringComparison.OrdinalIgnoreCase);
                });

                if (match != null)
                {
                    if (match is EpubLocalByteContentFile byteFile && byteFile.Content != null)
                    {
                        return (byteFile.Content, byteFile.ContentMimeType ?? GetMimeType(targetFileName));
                    }

                    if (match is EpubLocalTextContentFile textFile && !string.IsNullOrEmpty(textFile.Content))
                    {
                        return (Encoding.UTF8.GetBytes(textFile.Content), textFile.ContentMimeType ?? "text/plain");
                    }
                }
            }

            Console.WriteLine($"[EPUB Resource Miss] Target: {targetPath}, FileName: {targetFileName}");
            if (allFiles != null)
            {
                var samples = allFiles.Take(10).Select(f => f.FilePath).ToList();
                Console.WriteLine($"[EPUB Resource Debug] Available files (sample): {string.Join(", ", samples)}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EPUB Resource Exception] url={bookUrl} path={resourcePath} ex={ex}");
        }
        return null;
    }

    private string GetMimeType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLower();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
    }

    public byte[]? GetEpubCover(string path)
    {
        var fileName = path.Replace("local://", "");
        var filePath = Path.Combine(_booksDir, fileName);
        if (File.Exists(filePath) && filePath.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                EpubBook book = GetOrAddEpub(filePath);
                return book.CoverImage;
            }
            catch { }
        }
        return null;
    }
}
