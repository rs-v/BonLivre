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

    public LocalBookService()
    {
        _booksDir = Path.Combine(Directory.GetCurrentDirectory(), "books");
        if (!Directory.Exists(_booksDir)) Directory.CreateDirectory(_booksDir);
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
                EpubBook book = EpubReader.ReadBook(filePath);
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

                EpubBook book = EpubReader.ReadBook(filePath);
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
            var filePath = Path.Combine(_booksDir, fileName);
            if (!File.Exists(filePath)) return null;

            // 使用 ReadBook 会读取所有文件结构到内存，适合随机查询资源
            EpubBook book = EpubReader.ReadBook(filePath);
            var normalizedPath = resourcePath.Replace("\\", "/").TrimStart('/');

            // 搜索所有本地资源 (Images, Fonts, Html, Css 等)
            var allFiles = book.Content.AllFiles.Local;

            // 匹配策略：
            // 1. 完全一致
            // 2. 结尾匹配且前面有斜杠 (防止 images/001.jpg 匹配了 other_images/001.jpg)
            // 3. 文件名直接匹配 (最后手段)
            var match = allFiles.FirstOrDefault(f =>
            {
                var fPath = f.FilePath.Replace("\\", "/");
                return fPath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                       fPath.EndsWith("/" + normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                       fPath.Equals(normalizedPath.Split('/').Last(), StringComparison.OrdinalIgnoreCase);
            });

            if (match != null)
            {
                if (match is EpubLocalByteContentFile byteFile)
                {
                    if (byteFile.Content == null || byteFile.Content.Length == 0)
                    {
                        Console.WriteLine($"[EPUB Resource Warning] Content empty: url={bookUrl} path={resourcePath} normalized={normalizedPath} real={byteFile.FilePath}");
                        return null;
                    }
                    return (byteFile.Content, byteFile.ContentMimeType ?? "image/jpeg");
                }

                if (match is EpubLocalTextContentFile textFile)
                {
                    if (string.IsNullOrEmpty(textFile.Content))
                    {
                        Console.WriteLine($"[EPUB Resource Warning] Text content empty: url={bookUrl} path={resourcePath} normalized={normalizedPath} real={textFile.FilePath}");
                        return null;
                    }
                    return (Encoding.UTF8.GetBytes(textFile.Content), textFile.ContentMimeType ?? "text/plain");
                }

                Console.WriteLine($"[EPUB Resource Warning] Unknown match type: {match.GetType().Name} for path={resourcePath} normalized={normalizedPath}");
            }
            else
            {
                Console.WriteLine($"[EPUB Resource Miss] url={bookUrl} path={resourcePath} normalized={normalizedPath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EPUB Resource Error]: url={bookUrl} path={resourcePath} message={ex}");
        }
        return null;
    }

    public byte[]? GetEpubCover(string path)
    {
        var fileName = path.Replace("local://", "");
        var filePath = Path.Combine(_booksDir, fileName);
        if (File.Exists(filePath) && filePath.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                EpubBook book = EpubReader.ReadBook(filePath);
                return book.CoverImage;
            }
            catch { }
        }
        return null;
    }
}
