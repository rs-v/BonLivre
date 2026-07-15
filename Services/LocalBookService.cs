using System.Text.RegularExpressions;
using System.Text;
using System.Linq;
using HtmlAgilityPack;
using VersOne.Epub;
using BonLivre.Models;

namespace BonLivre.Services;

public partial class LocalBookService
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

    /// <summary>
    /// 从 local:// URL 解析出 books/ 目录内的真实文件路径，并阻止路径穿越。
    /// 取 '#' 之前的文件名部分，与 _booksDir 拼接并用 GetFullPath 规范化；
    /// 若规范化结果逃逸出 _booksDir（如 ../ 穿越），返回 null。所有本地文件访问都应经此解析。
    /// 这里不能做 URL 解码：BookUrl 由原始文件名生成，查询参数又已被 ASP.NET 解码过一次，
    /// 再解码会破坏含 '+' 或 %xx 形态字符的文件名（'+' 会变成空格）。
    /// </summary>
    private string? ResolveLocalPath(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        var fileName = url.Replace("local://", "").Split('#')[0];
        if (string.IsNullOrEmpty(fileName)) return null;

        var baseDir = Path.GetFullPath(_booksDir);
        var fullPath = Path.GetFullPath(Path.Combine(baseDir, fileName));

        var baseWithSep = baseDir.EndsWith(Path.DirectorySeparatorChar)
            ? baseDir
            : baseDir + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase)) return null;

        return fullPath;
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
        var filePath = ResolveLocalPath(url);
        if (filePath == null || !File.Exists(filePath)) return null;

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
        var spans = BuildTxtChapters(content);
        var chapters = new List<BookChapter>(spans.Count);
        for (int i = 0; i < spans.Count; i++)
        {
            chapters.Add(new BookChapter(spans[i].Title, $"{url}#{i}", i, spans[i].IsVolume));
        }
        return chapters;
    }

    public string? GetBookContent(string url, int index)
    {
        var filePath = ResolveLocalPath(url);
        if (filePath == null || !File.Exists(filePath)) return null;

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
        var spans = BuildTxtChapters(content);

        if (index < 0 || index >= spans.Count) return null;

        return content.Substring(spans[index].Start, spans[index].Length);
    }

    /// <summary>TXT 章节切分结果：标题、在原文中的起始偏移与长度，以及是否为分卷标题。</summary>
    private readonly record struct TxtChapterSpan(string Title, int Start, int Length, bool IsVolume);

    // 章节标题正则（逐行匹配）。覆盖三类：
    //   1. 「第X章/节/回/卷/集/部/篇 …」X 为中文数字或阿拉伯数字；
    //   2. 无编号的特殊卷目：序/序章/序言/楔子/前言/引子/后记/尾声/番外/外传/终章；
    //   3. 英文 Chapter N。
    // 限定整行仅由「标题 + 可选副标题」构成（长度受限），避免正文段落中以「第…章」开头的句子被误判为标题。
    [GeneratedRegex(
        @"^[ \t　]*(?<title>(?:第[零一二三四五六七八九十百千万两0-9]{1,9}[章节回卷集部篇](?:[ \t　].{0,30})?|(?:序章|序言|序|楔子|前言|引子|后记|尾声|番外|外传|终章)(?:[ \t　].{0,30})?|(?:Chapter|CHAPTER)[ \t]+[0-9]{1,4}(?:[ \t].{0,30})?))[ \t　]*$",
        RegexOptions.Multiline)]
    private static partial Regex ChapterHeadingRegex();

    // 分卷标题（卷/部/篇），前端可据 IsVolume 作层级展示；单独成章的「第X卷」等归为分卷。
    [GeneratedRegex(@"[卷部篇]")]
    private static partial Regex VolumeMarkerRegex();

    /// <summary>
    /// 将 TXT 全文按章节标题切分为若干区间。目录与正文共用此方法，保证章节索引一致。
    /// 规则：无标题命中时整篇作为单章「正文」；首个标题之前若存在非空内容，保留为「前言」章，
    /// 避免序言/引子被丢弃。
    /// </summary>
    private static List<TxtChapterSpan> BuildTxtChapters(string content)
    {
        var matches = ChapterHeadingRegex().Matches(content);
        var spans = new List<TxtChapterSpan>();

        if (matches.Count == 0)
        {
            spans.Add(new TxtChapterSpan("正文", 0, content.Length, false));
            return spans;
        }

        // 首个标题之前的正文（序言/前言等），非空则保留为一章
        int firstStart = matches[0].Index;
        if (content.AsSpan(0, firstStart).Trim().Length > 0)
        {
            spans.Add(new TxtChapterSpan("前言", 0, firstStart, false));
        }

        for (int i = 0; i < matches.Count; i++)
        {
            int start = matches[i].Index;
            int end = (i + 1 < matches.Count) ? matches[i + 1].Index : content.Length;
            var title = matches[i].Groups["title"].Value.Trim();
            bool isVolume = VolumeMarkerRegex().IsMatch(title);
            spans.Add(new TxtChapterSpan(title, start, end - start, isVolume));
        }

        return spans;
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
            var filePath = ResolveLocalPath(bookUrl);
            if (filePath == null || !File.Exists(filePath))
            {
                Console.WriteLine($"[EPUB Resource] File not found or invalid path for: {bookUrl}");
                return null;
            }

            EpubBook book = GetOrAddEpub(filePath);

            // 规范化目标路径。与 ResolveLocalPath 不同，这里必须解码：正文里的 img src
            // 由 ExtractTextWithImages 用 Uri.AbsolutePath 生成，是 percent-escaped 的。
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
        var filePath = ResolveLocalPath(path);
        if (filePath != null && File.Exists(filePath) && filePath.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                EpubBook book = GetOrAddEpub(filePath);
                return book.CoverImage;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EPUB Cover Error] path={path} ex={ex.Message}");
            }
        }
        return null;
    }

    /// <summary>
    /// EPUB 元数据中的标题；取不到时回退到文件名（不含扩展名）。
    /// </summary>
    public string GetEpubTitle(string path)
    {
        var filePath = ResolveLocalPath(path);
        if (filePath != null && File.Exists(filePath) && filePath.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                EpubBook book = GetOrAddEpub(filePath);
                if (!string.IsNullOrWhiteSpace(book.Title)) return book.Title.Trim();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EPUB Title Error] path={path} ex={ex.Message}");
            }
        }
        // 回退到文件名（不含扩展名）；解析失败时从原始 URL 尽力提取。
        var rawName = path.Replace("local://", "").Split('#')[0];
        return Path.GetFileNameWithoutExtension(rawName);
    }

    /// <summary>
    /// 用标题文字生成一张 SVG 占位封面。Native AOT 下不依赖 System.Drawing 等原生图像库，
    /// 直接拼字符串即可，矢量渲染在任意分辨率都清晰。
    /// </summary>
    public static byte[] GenerateTitleCoverSvg(string title)
    {
        const int width = 300;
        const int height = 400;

        // 稳定地由标题派生一个背景色，让不同书籍的占位封面有区分度。
        int hash = 0;
        foreach (var ch in title) hash = unchecked(hash * 31 + ch);
        int hue = Math.Abs(hash) % 360;

        var lines = WrapTitle(title, 8, 5);
        // 垂直居中排布文本块。
        const int lineHeight = 42;
        double blockHeight = lines.Count * lineHeight;
        double startY = (height - blockHeight) / 2 + lineHeight * 0.75;

        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(width)
          .Append("\" height=\"").Append(height)
          .Append("\" viewBox=\"0 0 ").Append(width).Append(' ').Append(height).Append("\">");
        sb.Append("<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">")
          .Append("<stop offset=\"0\" stop-color=\"hsl(").Append(hue).Append(",55%,42%)\"/>")
          .Append("<stop offset=\"1\" stop-color=\"hsl(").Append((hue + 40) % 360).Append(",60%,28%)\"/>")
          .Append("</linearGradient></defs>");
        sb.Append("<rect width=\"").Append(width).Append("\" height=\"").Append(height).Append("\" fill=\"url(#g)\"/>");
        sb.Append("<rect x=\"12\" y=\"12\" width=\"").Append(width - 24).Append("\" height=\"").Append(height - 24)
          .Append("\" fill=\"none\" stroke=\"rgba(255,255,255,0.35)\" stroke-width=\"2\"/>");
        sb.Append("<text x=\"").Append(width / 2)
          .Append("\" text-anchor=\"middle\" fill=\"#ffffff\" font-family=\"serif\" font-size=\"32\" font-weight=\"bold\">");
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append("<tspan x=\"").Append(width / 2)
              .Append("\" y=\"").Append((int)(startY + i * lineHeight)).Append("\">")
              .Append(EscapeXml(lines[i])).Append("</tspan>");
        }
        sb.Append("</text></svg>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static List<string> WrapTitle(string title, int charsPerLine, int maxLines)
    {
        title = title.Trim();
        var lines = new List<string>();
        for (int i = 0; i < title.Length && lines.Count < maxLines; i += charsPerLine)
        {
            lines.Add(title.Substring(i, Math.Min(charsPerLine, title.Length - i)));
        }
        // 文本被截断时在末行加省略号。
        if (lines.Count == maxLines && maxLines * charsPerLine < title.Length)
        {
            var last = lines[maxLines - 1];
            lines[maxLines - 1] = (last.Length > charsPerLine - 1 ? last.Substring(0, charsPerLine - 1) : last) + "…";
        }
        if (lines.Count == 0) lines.Add(title);
        return lines;
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
         .Replace("\"", "&quot;").Replace("'", "&apos;");
}
